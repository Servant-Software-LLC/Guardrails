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
/// <para>Every test here is written to FAIL right now: <see cref="RunEventStream"/> exists (task 05) but
/// every member throws <see cref="NotImplementedException"/> (task 06 implements the real behaviour), so
/// every call below throws before any assertion runs.</para>
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
        public void AttemptFinished(TaskNode task, int attempt, AttemptOutcome outcome) => Calls.Add(nameof(AttemptFinished));
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
            var stream = new RunEventStream(IRunObserver.Null, dir);
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, 2, AttemptOutcome.GuardrailFailed);

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
            var stream = new RunEventStream(IRunObserver.Null, dir);
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, 1, outcome);

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
            var stream = new RunEventStream(IRunObserver.Null, dir);
            TaskNode taskA = FlatTask("01-first");
            TaskNode taskB = FlatTask("02-second");

            ((IRunObserver)stream).AttemptFinished(taskA, 1, AttemptOutcome.Succeeded);
            ((IRunObserver)stream).AttemptFinished(taskA, 2, AttemptOutcome.MaxTurns);
            ((IRunObserver)stream).AttemptFinished(taskB, 1, AttemptOutcome.GuardrailFailed);

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
            var stream = new RunEventStream(IRunObserver.Null, runDir);
            TaskNode task = FlatTask("01-first");

            ((IRunObserver)stream).AttemptFinished(task, 3, AttemptOutcome.RateLimited);

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

    [Trait("Category", "RunEvents")]
    [Fact]
    public void Decorator_ForwardsEveryObservedCallToTheInner()
    {
        string dir = NewTempDirectory();
        try
        {
            var inner = new RecordingObserver();
            IRunObserver decorator = new RunEventStream(inner, dir);

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
            decorator.AttemptFinished(task, 1, AttemptOutcome.Succeeded);
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
