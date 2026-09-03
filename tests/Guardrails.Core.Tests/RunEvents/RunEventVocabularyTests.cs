using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// Pins the two `events.jsonl` contract changes design 595 asks for, on top of what
/// <see cref="RunEventStreamTests"/> already covers: run-level bracketing
/// (<see cref="IRunObserver.RunFinished"/> → the `run-finished` kind), the widened `attempt-finished`
/// row (the journal's own <see cref="AttemptRecord"/>, not five identity fields), and the `seq`
/// ordering key a live stream needs because <c>at</c> is neither unique nor monotonic under parallel
/// workers.
///
/// <para>Authored RED against the current writer (task 04); task 05 implements. The one exception is
/// <see cref="RunIdComesFromTheConstructor_NotTheDirectoryName"/>, which pins a property task 01
/// already shipped and must stay green.</para>
/// </summary>
public sealed class RunEventVocabularyTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures — copied from RunEventStreamTests.cs rather than shared, since that file is out of
    // this task's write scope.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    /// <summary>A fresh directory under the OS temp root — never under the repo. Caller deletes it in a <c>finally</c>.</summary>
    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-run-event-vocab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Every non-empty line of <c>events.jsonl</c> under <paramref name="directory"/>, raw (unparsed).</summary>
    private static List<string> ReadEventLines(string directory) =>
        [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(line => line.Length > 0)];

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Decision 1 — RunFinished / the `run-finished` kind
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunFinished_AppendsARunFinishedRow_CarryingExitCode()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));

            stream.RunFinished(0, null);

            List<string> lines = ReadEventLines(dir);
            Assert.Single(lines);

            JsonElement root = JsonDocument.Parse(lines[0]).RootElement;
            Assert.Equal("run-finished", root.GetProperty("kind").GetString());
            Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunFinishedRow_HasNoTaskId_BecauseItIsRunScoped()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));

            stream.RunFinished(0, null);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;

            // Absent, not null: `run-finished` is the only kind with no task to name.
            Assert.False(root.TryGetProperty("taskId", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunFinishedRow_CarriesFaultKindButNeverAMessage()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));

            // The message is the one value on this row that can carry an absolute path, a token, or a
            // fragment of source (#585 layer 3 POSTs these rows to an operator-supplied URL). The raise
            // site only ever hands RunFinished the TYPE NAME, never the message.
            var fault = new InvalidOperationException("sk-fake-AKIA00000000000000EXAMPLE-token");

            stream.RunFinished(null, fault.GetType().Name);

            string line = ReadEventLines(dir).Single();
            JsonElement root = JsonDocument.Parse(line).RootElement;

            Assert.Equal(nameof(InvalidOperationException), root.GetProperty("faultKind").GetString());

            // Null is honest: the run never reached a verdict, and a fabricated exit code would claim one.
            Assert.False(root.TryGetProperty("exitCode", out _));

            Assert.DoesNotContain(fault.Message, line);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // seq — the ordering key (§2e), because `at` is neither unique nor monotonic under parallel workers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void EveryRow_CarriesAStrictlyIncreasingSeq()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });
            stream.RunFinished(0, null);

            List<int> seqs =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt32())
            ];

            // 1-based, per-process, in file order — the four kinds raised above, in the order raised.
            Assert.Equal([1, 2, 3, 4], seqs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void Seq_IsUniqueAndOrdered_UnderConcurrentWriters()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");
            const int writerCount = 8;
            const int perWriter = 25;

            // seq (and `at`) must be assigned INSIDE the append lock — today `at` is built outside it,
            // so M4 parallel workers can interleave both its order and, on Windows' ~15.6ms tick, its
            // very value. seq is the field #585 layer 3 keys retry and ordering on instead.
            Parallel.For(0, writerCount, _ =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    stream.TaskStarting(task);
                }
            });

            List<int> seqsInFileOrder =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt32())
            ];

            Assert.Equal(writerCount * perWriter, seqsInFileOrder.Count);
            Assert.Equal(seqsInFileOrder.Count, seqsInFileOrder.Distinct().Count());

            // File order agrees with seq order — the property a reader relies on without re-sorting.
            Assert.Equal([.. seqsInFileOrder.OrderBy(s => s)], seqsInFileOrder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Decision 2 — AttemptFinished carries the journal's own AttemptRecord
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptFinishedRow_CarriesTheFieldsThatDecideAResponse()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            DateTimeOffset endedAt = DateTimeOffset.UtcNow;

            var record = new AttemptRecord
            {
                Attempt = 2,
                StartedAt = startedAt,
                EndedAt = endedAt,
                Outcome = AttemptOutcome.NeedsHuman,
                CostUsd = 3.5m,
                Turns = 12,
                LogDir = "logs/fixture",
                NeedsHumanKind = "blocked-work",
                Provenance = new AttemptProvenance
                {
                    Model = "claude-sonnet-5",
                    Runner = "claude-main",
                    Tier = "hard"
                }
            };

            stream.AttemptFinished(task, record);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;

            // Named for its TelemetryRow twin verbatim — the event IS the journal's attempt record,
            // emitted live, not a second vocabulary someone has to keep in sync with it.
            Assert.Equal(3.5m, root.GetProperty("costUsd").GetDecimal());
            Assert.Equal(12, root.GetProperty("turns").GetInt32());
            Assert.Equal("claude-sonnet-5", root.GetProperty("model").GetString());
            Assert.Equal("hard", root.GetProperty("tier").GetString());
            Assert.Equal("claude-main", root.GetProperty("runner").GetString());
            Assert.Equal(startedAt, root.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(endedAt, root.GetProperty("endedAt").GetDateTimeOffset());
            Assert.Equal("blocked-work", root.GetProperty("needsHumanKind").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptFinishedRow_OmitsFieldsTheRecordDoesNotHold()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            // Four of FailedAttempt's ten call sites pass no Provenance at all — model/tier/runner have
            // nothing to report. CostUsd is NOT sourced from Provenance, so it must still surface: the
            // stream reports exactly what the journal holds, never a blanket "no provenance means omit
            // everything" shortcut that would make the projection a second owner of the fact.
            var attemptRecord = new AttemptRecord
            {
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.GuardrailFailed,
                CostUsd = 0.42m,
                LogDir = "logs/fixture",
                Provenance = null
            };

            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = true });
            stream.AttemptFinished(task, attemptRecord);
            stream.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });
            stream.RunFinished(0, null);

            List<string> lines = ReadEventLines(dir);

            // Index 3: task-started, attempt-started, guardrail-finished, attempt-finished, ...
            JsonElement attemptRow = JsonDocument.Parse(lines[3]).RootElement;
            Assert.False(attemptRow.TryGetProperty("model", out _));
            Assert.False(attemptRow.TryGetProperty("tier", out _));
            Assert.False(attemptRow.TryGetProperty("runner", out _));
            Assert.Equal(0.42m, attemptRow.GetProperty("costUsd").GetDecimal());

            // elapsedSeconds / attemptsMax are prose prohibitions otherwise — the forked vocabulary
            // #585 warns against two paragraphs after proposing them. Checked across every kind raised
            // above, not just attempt-finished.
            foreach (string line in lines)
            {
                JsonElement row = JsonDocument.Parse(line).RootElement;
                Assert.False(row.TryGetProperty("elapsedSeconds", out _));
                Assert.False(row.TryGetProperty("attemptsMax", out _));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Declared exemption from the red census (task 01 already shipped this)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunIdComesFromTheConstructor_NotTheDirectoryName()
    {
        string dir = NewTempDirectory();
        try
        {
            // Deliberately NOT the directory's own name (which RunEventStreamTests's "my-test-run"
            // fixture happens to equal) — a runId that could not be derived from the path is the only
            // fixture that can tell a real constructor value apart from a silent Path.GetFileName fallback.
            const string runId = "distinct-run-id-never-a-directory-name";
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, runId);
            TaskNode task = FlatTask("01-first");

            stream.TaskStarting(task);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;
            Assert.Equal(runId, root.GetProperty("runId").GetString());
            Assert.NotEqual(Path.GetFileName(dir), root.GetProperty("runId").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
