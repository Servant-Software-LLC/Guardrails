using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// The first of plan 34's two projections off the one emission seam (task 05): the semantic,
/// low-frequency, agent-facing <c>events.jsonl</c> stream <see cref="RunEventStream"/> appends. A
/// supervising agent filters rows on FIELDS (<c>taskId</c>, <c>attempt</c>, …), so a row whose <c>kind</c>
/// it does not recognise is still a visible line rather than an invisible one — the property that would
/// have prevented all three of the stdout-grep failures in issue #585.
///
/// <para>(These were authored red against task 05's throwing stub and went green in task 06; plan 34 has
/// since merged, so they assert real behaviour now.)</para>
/// </summary>
public sealed class RunEventStreamTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
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
        string dir = Path.Combine(Path.GetTempPath(), "gr-run-event-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Every non-empty line of <c>events.jsonl</c> under <paramref name="directory"/>, raw (unparsed).</summary>
    private static List<string> ReadEventLines(string directory) =>
        [.. File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Where(line => line.Length > 0)];

    /// <summary>
    /// The inner observer a decorator is supposed to be transparent to. Records WHICH member arrived, in
    /// order — the failure mode this seam is about is a decorator that swallows ANY ONE call, not just
    /// <see cref="AttemptFinished"/> (the trap <see cref="IRunObserver"/> documents four times over).
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

    /// <summary>A minimal <see cref="AttemptRecord"/> fixture — only <c>Attempt</c>/<c>Outcome</c> matter to these tests.</summary>
    private static AttemptRecord AttemptRecordFixture(int attempt, AttemptOutcome outcome) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = outcome,
        LogDir = "logs/fixture"
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptFinished_AppendsOneJsonLine_CarryingTaskIdAttemptAndOutcome()
    {
        string dir = NewTempDirectory();
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, AttemptRecordFixture(2, AttemptOutcome.GuardrailFailed));

            List<string> lines = ReadEventLines(dir);
            Assert.Single(lines);

            using JsonDocument doc = JsonDocument.Parse(lines[0]);
            JsonElement root = doc.RootElement;
            Assert.Equal(task.Id, root.GetProperty("taskId").GetString());
            Assert.Equal(2, root.GetProperty("attempt").GetInt32());
            Assert.Equal(JournalJson.OutcomeToken(AttemptOutcome.GuardrailFailed), root.GetProperty("outcome").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static IEnumerable<object[]> AllOutcomes() =>
        Enum.GetValues<AttemptOutcome>().Select(o => new object[] { o });

    [Trait("Category", "RunEvents")]
    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void AttemptFinished_OutcomeTokenMatchesTheTelemetryCorpusVocabulary(AttemptOutcome outcome)
    {
        string dir = NewTempDirectory();
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, AttemptRecordFixture(1, outcome));

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;

            // The SAME token JournalJson (the journal's single source of truth) and TelemetryRow.Outcome
            // already write — e.g. "max-turns", "guardrail-failed" — never a second vocabulary invented
            // here. Compared against the exact text, not merely "is a string".
            Assert.Equal(JournalJson.OutcomeToken(outcome), root.GetProperty("outcome").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void EveryLine_IsIndependentlyParseableJson()
    {
        string dir = NewTempDirectory();
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode taskA = FlatTask("01-first");
            TaskNode taskB = FlatTask("02-second");

            ((IRunObserver)stream).AttemptFinished(taskA, AttemptRecordFixture(1, AttemptOutcome.Succeeded));
            ((IRunObserver)stream).AttemptFinished(taskA, AttemptRecordFixture(2, AttemptOutcome.MaxTurns));
            ((IRunObserver)stream).AttemptFinished(taskB, AttemptRecordFixture(1, AttemptOutcome.GuardrailFailed));

            List<string> lines = ReadEventLines(dir);
            Assert.Equal(3, lines.Count);

            // A consumer attaching late reads ONE row at a time: each line must parse entirely on its
            // own, never depending on a surrounding array or a sibling line (JSON Lines, not one big
            // JSON document).
            foreach (string line in lines)
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void UnrecognisedConsumer_StillSeesTheRow()
    {
        string dir = NewTempDirectory();
        try
        {
            string runDir = Path.Combine(dir, "my-test-run");
            Directory.CreateDirectory(runDir);
            var stream = new RunEventStream(IRunObserver.Null, runDir, Path.GetFileName(runDir));
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, AttemptRecordFixture(3, AttemptOutcome.RateLimited));

            JsonElement root = JsonDocument.Parse(ReadEventLines(runDir).Single()).RootElement;

            // Deliberately never reads `kind`: a consumer that does not recognise this row's kind still
            // gets the envelope fields it filters on — an unrecognised event is a visible row, not an
            // invisible one (issue #585).
            Assert.Equal("my-test-run", root.GetProperty("runId").GetString());
            Assert.Equal(task.Id, root.GetProperty("taskId").GetString());
            Assert.Equal(3, root.GetProperty("attempt").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Lifecycle kinds (issue #595)
    //
    // The shipped projection emitted ONLY `attempt-finished`, which left a consumer unable to tell a
    // healthy run that has not finished its first attempt from a run that never started — the exact
    // ambiguity #585 exists to remove, relocated from the stdout grep into the event vocabulary.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TaskStarting_EmitsTaskStarted_SoAnEmptyStreamMeansNotStarted()
    {
        string dir = NewTempDirectory();
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).TaskStarting(task);

            // The liveness proof: a row exists BEFORE any attempt has finished. Without this, a consumer
            // attaching to a just-started run sees an empty file and cannot distinguish "alive, still on
            // attempt 1" from "never started" from "already over".
            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;
            Assert.Equal("task-started", root.GetProperty("kind").GetString());
            Assert.Equal(task.Id, root.GetProperty("taskId").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void LifecycleKinds_BracketATaskFromStartToSettle_InOrder()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            stream.TaskStarting(task);
            stream.AttemptStarting(task, 1, 3);
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = false, Reason = "no file" });
            stream.AttemptFinished(task, AttemptRecordFixture(1, AttemptOutcome.GuardrailFailed));
            stream.AttemptStarting(task, 2, 3);
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = true });
            stream.AttemptFinished(task, AttemptRecordFixture(2, AttemptOutcome.Succeeded));
            stream.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });

            List<string> kinds =
            [
                .. ReadEventLines(dir).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!)
            ];

            // The whole retry story a supervisor needs, in the order it happened — not just the two
            // attempt settles the original projection emitted.
            Assert.Equal(
                [
                    "task-started",
                    "attempt-started", "guardrail-finished", "attempt-finished",
                    "attempt-started", "guardrail-finished", "attempt-finished",
                    "task-settled"
                ],
                kinds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptStarted_CarriesItsBudget()
    {
        string dir = NewTempDirectory();
        try
        {
            ((IRunObserver)new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir))).AttemptStarting(FlatTask("01-first"), 2, 5);

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;
            Assert.Equal(2, root.GetProperty("attempt").GetInt32());

            // attempt 2 of 5 vs 2 of 2 are different situations: one has room to retry, one is the last
            // chance. #585 asked for `attemptsMax` for exactly this.
            Assert.Equal(5, root.GetProperty("budget").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void GuardrailFinished_CarriesTheFailureReason_ButNotOnAPass()
    {
        string dir = NewTempDirectory();
        try
        {
            IRunObserver stream = new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir));
            TaskNode task = FlatTask("01-first");

            stream.GuardrailFinished(task, new GuardrailResult { Name = "02-fails", Passed = false, Reason = "out/x.txt missing" });
            stream.GuardrailFinished(task, new GuardrailResult { Name = "01-passes", Passed = true, Reason = "ignored" });

            List<string> lines = ReadEventLines(dir);

            JsonElement failed = JsonDocument.Parse(lines[0]).RootElement;
            Assert.Equal("02-fails", failed.GetProperty("guardrail").GetString());
            Assert.False(failed.GetProperty("passed").GetBoolean());

            // The point of the field: the supervisor learns WHY without opening feedback.md, which is the
            // filesystem read #585 was filed to remove.
            Assert.Equal("out/x.txt missing", failed.GetProperty("detail").GetString());

            JsonElement passed = JsonDocument.Parse(lines[1]).RootElement;
            Assert.True(passed.GetProperty("passed").GetBoolean());
            Assert.False(passed.TryGetProperty("detail", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static IEnumerable<object[]> AllTaskOutcomes() =>
        Enum.GetValues<TaskOutcome>().Select(o => new object[] { o });

    [Trait("Category", "RunEvents")]
    [Theory]
    [MemberData(nameof(AllTaskOutcomes))]
    public void TaskSettled_SharesOneOutcomeVocabularyWithAttemptFinished(TaskOutcome outcome)
    {
        string dir = NewTempDirectory();
        try
        {
            ((IRunObserver)new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir)))
                .TaskFinished(new TaskResult { TaskId = "01-first", Outcome = outcome, Summary = "s" });

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;
            string token = root.GetProperty("outcome").GetString()!;

            // Every member tokenizes (the switch throws on an unmapped one), and in the house kebab style
            // rather than the enum's PascalCase.
            Assert.NotEmpty(token);
            Assert.DoesNotMatch("[A-Z]", token);

            // The one that matters: where TaskOutcome and AttemptOutcome name the same thing, the wire
            // token is IDENTICAL — so a consumer filtering `outcome == "guardrail-failed"` catches it on
            // both kinds. #585: "do NOT invent a second vocabulary."
            if (Enum.TryParse(outcome.ToString(), out AttemptOutcome twin))
            {
                Assert.Equal(JournalJson.OutcomeToken(twin), token);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void ARowOmitsTheFieldsItsKindDoesNotDefine_RatherThanWritingThemNull()
    {
        string dir = NewTempDirectory();
        try
        {
            ((IRunObserver)new RunEventStream(IRunObserver.Null, dir, Path.GetFileName(dir))).TaskStarting(FlatTask("01-first"));

            JsonElement root = JsonDocument.Parse(ReadEventLines(dir).Single()).RootElement;

            // Absent, not null. A consumer testing for a field's presence must get a straight answer —
            // `"attempt": null` on a task-started row would read as "attempt unknown" instead of
            // "attempts do not apply here".
            Assert.False(root.TryGetProperty("attempt", out _));
            Assert.False(root.TryGetProperty("outcome", out _));
            Assert.False(root.TryGetProperty("guardrail", out _));
            Assert.False(root.TryGetProperty("passed", out _));

            // The envelope is always there.
            foreach (string field in (string[])["kind", "at", "runId", "taskId"])
            {
                Assert.True(root.TryGetProperty(field, out _), $"envelope field '{field}' missing");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void Decorator_ForwardsEveryObservedCallToTheInner()
    {
        string dir = NewTempDirectory();
        try
        {
            var inner = new RecordingObserver();
            IRunObserver decorator = new RunEventStream(inner, dir, Path.GetFileName(dir));

            TaskNode task = FlatTask("01-first");
            TaskNode waveTask = task with { Id = "wave-01-x/01-first", WaveDir = "wave-01-x" };
            var wave = new WaveNode
            {
                Dir = "wave-01-x",
                Number = 1,
                Slug = "x",
                Directory = "/fake/plan/wave-01-x",
                Tasks = [waveTask]
            };
            var taskResult = new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" };
            var guardrailResult = new GuardrailResult { Name = "01-check", Passed = true };
            var decisionEntry = new DecisionEntry
            {
                Boundary = "drift",
                Policy = "prompt",
                Decision = "halted",
                Subject = task.Id,
                Headline = "fixture"
            };
            var breakdownContext = new WaveBreakdownContext
            {
                WaveDir = "wave-01-x",
                Index = 1,
                Total = 1,
                BreakdownLogDir = "/fake/logs/wave-01-x/breakdown",
                StreamLogPath = "/fake/logs/wave-01-x/breakdown/claude-stream.jsonl",
                TasksDirectory = "/fake/plan/wave-01-x/tasks",
                ComposedPromptBytes = 0,
                Ceiling = TimeSpan.FromMinutes(30)
            };
            var writeScopeOffense = new WriteScopeOffense { Path = "stray.txt", Status = 'A' };
            var preflightCheck = new PlanPreflightCheck { Name = "01-check", Passed = true };

            // Every member IRunObserver declares — including its default-bodied ones — driven directly,
            // so a decorator that inherits even ONE default (and therefore never reaches the inner) fails
            // right here rather than hiding behind a test that only ever tried AttemptFinished.
            decorator.TaskStarting(task);
            decorator.AttemptStarting(task, 1, 3);
            decorator.AttemptModelResolved(task, 1, "claude-sonnet-5", requestedModel: null);
            decorator.AttemptRouteResolved(task, 1, "claude", "claude-sonnet-5", tier: null, requestedTier: null);
            decorator.AttemptFinished(task, AttemptRecordFixture(1, AttemptOutcome.Succeeded));
            decorator.TaskFinished(taskResult);
            decorator.GuardrailFinished(task, guardrailResult);
            decorator.PlanHashMismatch("sha256:old");
            decorator.ParallelismClampedNoProvider(4);
            decorator.CleanupFailed(task.Id, new InvalidOperationException("fixture"));
            decorator.PromptPaused(task, "rate limited", TimeSpan.FromSeconds(30), 1);
            decorator.OutOfScopeStripped(task, [writeScopeOffense]);
            decorator.DecisionRecorded(decisionEntry);
            decorator.VerifierAdvisoryFound(task.Id, "fixture finding");
            decorator.OverwatchNoVerdict(task.Id, "fixture reason");
            decorator.WaveStarting(wave, 1, 1);
            decorator.WaveFinished(wave, WaveStatus.Completed, skipped: false);
            decorator.WaveGateFinished(wave, isEntryGate: true, [preflightCheck]);
            decorator.WaveBreakdownStarting(breakdownContext);
            decorator.WaveBreakdownFinished(breakdownContext, TimeSpan.FromMinutes(5), 3, failureKind: null, authoredWave: wave);

            Assert.Equal(
                [
                    nameof(IRunObserver.TaskStarting),
                    nameof(IRunObserver.AttemptStarting),
                    nameof(IRunObserver.AttemptModelResolved),
                    nameof(IRunObserver.AttemptRouteResolved),
                    nameof(IRunObserver.AttemptFinished),
                    nameof(IRunObserver.TaskFinished),
                    nameof(IRunObserver.GuardrailFinished),
                    nameof(IRunObserver.PlanHashMismatch),
                    nameof(IRunObserver.ParallelismClampedNoProvider),
                    nameof(IRunObserver.CleanupFailed),
                    nameof(IRunObserver.PromptPaused),
                    nameof(IRunObserver.OutOfScopeStripped),
                    nameof(IRunObserver.DecisionRecorded),
                    nameof(IRunObserver.VerifierAdvisoryFound),
                    nameof(IRunObserver.OverwatchNoVerdict),
                    nameof(IRunObserver.WaveStarting),
                    nameof(IRunObserver.WaveFinished),
                    nameof(IRunObserver.WaveGateFinished),
                    nameof(IRunObserver.WaveBreakdownStarting),
                    nameof(IRunObserver.WaveBreakdownFinished)
                ],
                inner.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
