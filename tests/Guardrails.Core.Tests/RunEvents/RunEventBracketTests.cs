using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// Pins the two `events.jsonl` contract changes design 585-layer3-webhooks-contract.md asks for: the
/// `bracket` field on every row (§4.2 — the delivery key's discriminator across a resume, which the
/// shipped `(runId, seq)` key cannot survive) and the `onRow` wire copy `RunEventStream` hands a webhook
/// dispatcher from inside its append lock (§3.1), including the `detail` withholding/capping policy
/// (§4.3/§6.3/§4.4).
///
/// <para>Authored RED against task 02's compile-only stubs — <see cref="EventDelivery"/> and the two new,
/// defaulted constructor parameters (<c>onRow</c>, <c>includeDetail</c>), which are accepted and ignored;
/// task 03 implements the behaviour these tests pin. The one exception is
/// <see cref="AThrowingOnRowCallbackDoesNotPropagate"/>, a declared exemption from the red census: against
/// the stub <c>onRow</c> is never invoked, so nothing can throw and the test is green on both sides.</para>
/// </summary>
public sealed class RunEventBracketTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures — copied from RunEventVocabularyTests.cs rather than shared, since that file is out of
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
        string dir = Path.Combine(Path.GetTempPath(), "gr-run-event-bracket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Every non-empty line of <c>events.jsonl</c> under <paramref name="directory"/>, raw (unparsed).</summary>
    private static List<string> ReadEventLines(string directory) =>
        [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(line => line.Length > 0)];

    /// <summary>
    /// The inner observer a decorator is supposed to be transparent to. Records WHICH member arrived —
    /// copied from RunEventStreamTests.cs's <c>RecordingObserver</c>, trimmed to the members this file
    /// raises, since that file is out of this task's write scope.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Calls { get; } = [];

        public void TaskStarting(TaskNode task) => Calls.Add(nameof(TaskStarting));
        public void AttemptStarting(TaskNode task, int attempt, int budget) => Calls.Add(nameof(AttemptStarting));
        public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
            Calls.Add(nameof(AttemptModelResolved));
        public void AttemptRouteResolved(
            TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
            Calls.Add(nameof(AttemptRouteResolved));
        public void AttemptFinished(TaskNode task, AttemptRecord record) => Calls.Add(nameof(AttemptFinished));
        public void TaskFinished(TaskResult result) => Calls.Add(nameof(TaskFinished));
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => Calls.Add(nameof(GuardrailFinished));
        public void RunFinished(int? exitCode, string? faultKind) => Calls.Add(nameof(RunFinished));
        public void PlanHashMismatch(string previousPlanHash) => Calls.Add(nameof(PlanHashMismatch));
        public void ParallelismClampedNoProvider(int requested) => Calls.Add(nameof(ParallelismClampedNoProvider));
        public void CleanupFailed(string owner, Exception error) => Calls.Add(nameof(CleanupFailed));
        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
            Calls.Add(nameof(PromptPaused));
        public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
            Calls.Add(nameof(OutOfScopeStripped));
        public void DecisionRecorded(DecisionEntry entry) => Calls.Add(nameof(DecisionRecorded));
        public void VerifierAdvisoryFound(string taskId, string finding) => Calls.Add(nameof(VerifierAdvisoryFound));
        public void OverwatchNoVerdict(string taskId, string reason) => Calls.Add(nameof(OverwatchNoVerdict));
        public void WaveStarting(WaveNode wave, int index, int total) => Calls.Add(nameof(WaveStarting));
        public void WaveFinished(WaveNode wave, WaveStatus status, bool skipped) => Calls.Add(nameof(WaveFinished));
        public void WaveGateFinished(WaveNode wave, bool isEntryGate, IReadOnlyList<PlanPreflightCheck> checks) =>
            Calls.Add(nameof(WaveGateFinished));
        public void WaveBreakdownStarting(WaveBreakdownContext context) => Calls.Add(nameof(WaveBreakdownStarting));
        public void WaveBreakdownFinished(
            WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
            WaveNode? authoredWave) => Calls.Add(nameof(WaveBreakdownFinished));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixed strings the design pins (§6.3 / §4.4) — hard-coded, not paraphrased.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private const string WithheldMarker = "(detail withheld; pass --on-event-detail)";
    private const string TruncatedSuffix = "…[truncated]";

    /// <summary><c>&lt;unix-ms&gt;-&lt;4 lowercase hex&gt;</c> — §4.2.</summary>
    private static readonly Regex BracketShape = new("^[0-9]{13}-[0-9a-f]{4}$");

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // bracket (§4.2)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void BracketIsPresentOnEveryRow()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = true });
            stream.AttemptFinished(task, new AttemptRecord
            {
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Succeeded,
                LogDir = "logs/fixture"
            });
            stream.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });
            // run-finished: the only run-scoped kind, and the row a CI wrapper keys on — a bracket
            // stamped per-task would miss it.
            stream.RunFinished(0, null);

            List<string> lines = ReadEventLines(dir);
            Assert.Equal(6, lines.Count);

            foreach (string line in lines)
            {
                JsonElement root = JsonDocument.Parse(line).RootElement;
                Assert.True(root.TryGetProperty("bracket", out JsonElement bracket), $"row missing 'bracket': {line}");
                Assert.False(string.IsNullOrEmpty(bracket.GetString()));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void BracketMatchesUnixMillisAndFourHex()
    {
        string dir = NewTempDirectory();
        try
        {
            long beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            stream.RunFinished(0, null);
            long afterMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string bracket = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement.GetProperty("bracket").GetString()!;

            // The regex alone accepts "0000000000000-abcd", which would satisfy the shape while
            // destroying the ordering §4.2 buys with the millisecond prefix — so the prefix is also
            // checked against a real clock window, not just its shape.
            Assert.Matches(BracketShape, bracket);

            string[] parts = bracket.Split('-');
            long prefixMs = long.Parse(parts[0]);
            TimeSpan tolerance = TimeSpan.FromMinutes(5);
            Assert.InRange(prefixMs, beforeMs - (long)tolerance.TotalMilliseconds, afterMs + (long)tolerance.TotalMilliseconds);

            // Lowercase hex, checked explicitly rather than only via the regex's [0-9a-f] class.
            string suffix = parts[1];
            Assert.Equal(suffix.ToLowerInvariant(), suffix, StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void BracketIsStableAcrossRowsInOneStream()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.RunFinished(0, null);

            List<string> brackets =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("bracket").GetString()!)
            ];

            Assert.Equal(3, brackets.Count);
            // Generated once in the constructor, not per row — the property that fails against a
            // bracket built inside AppendLine.
            Assert.Single(brackets.Distinct());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void BracketDiffersAcrossTwoStreams()
    {
        string dir = NewTempDirectory();
        try
        {
            // Same directory AND same runId — the discriminating part. A bracket derived from the
            // run id would wrongly pass this test.
            string runId = Path.GetFileName(dir);
            IRunObserver first = new RunEventStream(IRunObserver.Null, dir, runId);
            first.RunFinished(0, null);

            IRunObserver second = new RunEventStream(IRunObserver.Null, dir, runId);
            second.RunFinished(0, null);

            List<string> brackets =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("bracket").GetString()!)
            ];

            Assert.Equal(2, brackets.Count);
            // Whole-string inequality only: two constructions in one test routinely share a
            // millisecond, and the 4-hex suffix is what §4.2 says keeps them distinct.
            Assert.NotEqual(brackets[0], brackets[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The onRow wire copy (§3.1 / §4.3 / §6.3 / §4.4)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void WireLineEqualsFileLineWhenDetailIsNull()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            string runId = Path.GetFileName(dir);
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, runId, onRow: d => collected.Enqueue(d), includeDetail: false);
            TaskNode task = FlatTask("01-first");

            // Only kinds that carry no `detail`.
            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.RunFinished(0, null);

            List<string> fileLines = ReadEventLines(dir);
            List<EventDelivery> deliveries = [.. collected];

            Assert.Equal(fileLines.Count, deliveries.Count);

            for (int i = 0; i < fileLines.Count; i++)
            {
                // Ordinally equal — byte-for-byte, not "parses to the same object".
                Assert.Equal(fileLines[i], deliveries[i].JsonLine, StringComparer.Ordinal);

                JsonElement row = JsonDocument.Parse(fileLines[i]).RootElement;
                Assert.Equal(row.GetProperty("kind").GetString(), deliveries[i].Kind);

                string expectedDeliveryId =
                    $"{runId}:{row.GetProperty("bracket").GetString()}:{row.GetProperty("seq").GetInt32()}";
                Assert.Equal(expectedDeliveryId, deliveries[i].DeliveryId);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void WireLineEqualsFileLineForPassingGuardrailFinished()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir), onRow: d => collected.Enqueue(d), includeDetail: false);
            TaskNode task = FlatTask("01-first");

            // Passed = true → the row's own `detail` is null, so the wire copy must be byte-identical
            // to the file line — the case the design's first draft got wrong.
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = true });

            string fileLine = ReadEventLines(dir).Single();
            EventDelivery delivery = Assert.Single(collected);

            Assert.Equal(fileLine, delivery.JsonLine, StringComparer.Ordinal);

            JsonElement wireRoot = JsonDocument.Parse(delivery.JsonLine).RootElement;
            Assert.False(wireRoot.TryGetProperty("detail", out _));
            Assert.DoesNotContain(WithheldMarker, delivery.JsonLine);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void WireLineCarriesWithheldMarkerWhenDetailPresent()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir), onRow: d => collected.Enqueue(d), includeDetail: false);
            TaskNode task = FlatTask("01-first");

            const string secretGuardrailReason = "sk-fake-AKIA00000000000000EXAMPLE-guardrail-reason";
            const string secretTaskSummary = "sk-fake-AKIA00000000000000EXAMPLE-task-summary";

            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = false, Reason = secretGuardrailReason });
            stream.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.GuardrailFailed, Summary = secretTaskSummary });

            List<string> fileLines = ReadEventLines(dir);
            List<EventDelivery> deliveries = [.. collected];
            Assert.Equal(2, fileLines.Count);
            Assert.Equal(2, deliveries.Count);

            for (int i = 0; i < fileLines.Count; i++)
            {
                // events.jsonl fidelity is never affected by the wire policy (§6.3).
                JsonElement fileRoot = JsonDocument.Parse(fileLines[i]).RootElement;
                Assert.True(fileRoot.TryGetProperty("detail", out JsonElement fileDetail));
                Assert.False(string.IsNullOrEmpty(fileDetail.GetString()));

                JsonElement wireRoot = JsonDocument.Parse(deliveries[i].JsonLine).RootElement;
                Assert.True(wireRoot.TryGetProperty("detail", out JsonElement wireDetail));
                Assert.Equal(WithheldMarker, wireDetail.GetString());

                Assert.DoesNotContain(secretGuardrailReason, deliveries[i].JsonLine);
                Assert.DoesNotContain(secretTaskSummary, deliveries[i].JsonLine);

                // `detail` is the only field that may differ — compare every other property/value.
                foreach (JsonProperty prop in fileRoot.EnumerateObject())
                {
                    if (prop.Name == "detail")
                    {
                        continue;
                    }

                    Assert.True(wireRoot.TryGetProperty(prop.Name, out JsonElement wireValue), $"wire line missing '{prop.Name}'");
                    Assert.Equal(prop.Value.GetRawText(), wireValue.GetRawText());
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void WireLineCapsDetailAtMaxCharsWhenIncludeDetailIsTrue()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir), onRow: d => collected.Enqueue(d), includeDetail: true);
            TaskNode task = FlatTask("01-first");

            string longReason = new('x', GuardrailFailureReason.MaxChars + 500);
            string shortReason = new('y', 10);

            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-long", Passed = false, Reason = longReason });
            stream.GuardrailFinished(task, new GuardrailResult { Name = "02-short", Passed = false, Reason = shortReason });

            List<EventDelivery> deliveries = [.. collected];
            Assert.Equal(2, deliveries.Count);

            JsonElement longWireDetail = JsonDocument.Parse(deliveries[0].JsonLine).RootElement.GetProperty("detail");
            string cappedDetail = longWireDetail.GetString()!;
            Assert.Equal(longReason[..GuardrailFailureReason.MaxChars], cappedDetail[..GuardrailFailureReason.MaxChars], StringComparer.Ordinal);
            Assert.EndsWith(TruncatedSuffix, cappedDetail, StringComparison.Ordinal);
            Assert.Equal(GuardrailFailureReason.MaxChars + TruncatedSuffix.Length, cappedDetail.Length);

            // A cap that fires unconditionally is as wrong as one that never fires: a comfortably
            // shorter reason must pass through unchanged, with no suffix.
            JsonElement shortWireDetail = JsonDocument.Parse(deliveries[1].JsonLine).RootElement.GetProperty("detail");
            Assert.Equal(shortReason, shortWireDetail.GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void SeqAndBracketStayConsistentUnderConcurrentWriters()
    {
        string dir = NewTempDirectory();
        try
        {
            var collected = new ConcurrentQueue<EventDelivery>();
            IRunObserver stream = new RunEventStream(
                IRunObserver.Null, dir, Path.GetFileName(dir), onRow: d => collected.Enqueue(d), includeDetail: false);
            TaskNode task = FlatTask("01-first");
            const int writerCount = 8;
            const int perWriter = 25;

            Parallel.For(0, writerCount, _ =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    stream.TaskStarting(task);
                }
            });

            List<string> fileLines = ReadEventLines(dir);
            List<(int Seq, string Bracket)> fileKeysInFileOrder =
            [
                .. fileLines.Select(line =>
                {
                    JsonElement root = JsonDocument.Parse(line).RootElement;
                    return (root.GetProperty("seq").GetInt32(), root.GetProperty("bracket").GetString()!);
                })
            ];

            Assert.Equal(writerCount * perWriter, fileLines.Count);

            List<int> seqs = [.. fileKeysInFileOrder.Select(k => k.Seq)];
            Assert.Equal(seqs.Count, seqs.Distinct().Count());
            Assert.Equal([.. seqs.OrderBy(s => s)], seqs);

            Assert.Single(fileKeysInFileOrder.Select(k => k.Bracket).Distinct());

            List<(int Seq, string Bracket)> deliveredKeysInEnqueueOrder =
            [
                .. collected.Select(d =>
                {
                    JsonElement root = JsonDocument.Parse(d.JsonLine).RootElement;
                    return (root.GetProperty("seq").GetInt32(), root.GetProperty("bracket").GetString()!);
                })
            ];

            // Enqueue order equals file order — the assertion that fails against an onRow invoked
            // OUTSIDE the append lock.
            Assert.Equal(fileKeysInFileOrder, deliveredKeysInEnqueueOrder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Declared exemption from the red census — will be GREEN, and that is correct (see class doc).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Trait("Plan", "36-onevent")]
    [Fact]
    public void AThrowingOnRowCallbackDoesNotPropagate()
    {
        string dir = NewTempDirectory();
        try
        {
            var inner = new RecordingObserver();
            IRunObserver stream = new RunEventStream(
                inner, dir, Path.GetFileName(dir), onRow: _ => throw new InvalidOperationException("boom"));
            TaskNode task = FlatTask("01-first");

            var exception = Record.Exception(() =>
            {
                stream.TaskStarting(task);
                stream.AttemptStarting(task, 1, 3);
                stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = true });
                stream.RunFinished(0, null);
            });

            Assert.Null(exception);
            Assert.Equal(4, ReadEventLines(dir).Count);
            Assert.Equal(
                [nameof(IRunObserver.TaskStarting), nameof(IRunObserver.AttemptStarting), nameof(IRunObserver.GuardrailFinished), nameof(IRunObserver.RunFinished)],
                inner.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
