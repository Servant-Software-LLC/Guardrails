using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// The DECORATOR half of the attempt-completion seam (the gap between <c>AttemptStarting</c> and
/// <c>TaskFinished</c> that left no observer able to say WHY a single attempt failed).
/// <see cref="IRunObserver.AttemptFinished"/> is a DEFAULT interface member, so a decorator that simply
/// omits it still compiles, still satisfies the interface, and silently swallows the event — the identical
/// trap <c>AttemptModelResolved</c> (#349) and <c>WaveGateFinished</c> (#513) each carry, and the reason this
/// file asserts on the DECORATORS themselves rather than only on whatever eventually renders the event.
///
/// <para>These tests are written to FAIL right now: the interface member exists (this task) but neither
/// shipped decorator declares it yet (task 02), so every call below resolves to the interface's empty
/// default body and the recording inner observer never hears it.</para>
/// </summary>
public sealed class AttemptCompletionForwardingTests
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

    /// <summary>A throwaway directory tree — both decorators write their artefacts under a real root.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-attempt-finished-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public string Dir(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// The inner observer a decorator is supposed to be transparent to. Records the WHOLE payload, not a
    /// count: the failure mode this seam is about is a decorator that arrives with a mangled argument list
    /// just as much as one that never arrives at all.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(TaskNode Task, int Attempt, AttemptOutcome Outcome)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void AttemptFinished(TaskNode task, AttemptRecord record) =>
            Calls.Add((task, record.Attempt, record.Outcome));
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
    // The two decorators that ship today.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void OnTheFlyLogSiteObserver_ForwardsAttemptFinished()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        var inner = new RecordingObserver();
        TaskNode task = FlatTask("01-first");
        var decorator = new OnTheFlyLogSiteObserver(inner, logsRoot, "test-run", [task], liveUrlForTask: null);

        ((IRunObserver)decorator).AttemptFinished(task, AttemptRecordFixture(1, AttemptOutcome.ActionFailed));

        Assert.Single(inner.Calls);
        Assert.Same(task, inner.Calls[0].Task);
        Assert.Equal(1, inner.Calls[0].Attempt);
        Assert.Equal(AttemptOutcome.ActionFailed, inner.Calls[0].Outcome);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void OnTheFlyDiagramObserver_ForwardsAttemptFinished()
    {
        using var tree = new TempTree();
        var inner = new RecordingObserver();
        TaskNode task = FlatTask("01-first");
        var plan = new PlanDefinition
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };
        var decorator = new OnTheFlyDiagramObserver(inner, tree.Dir("logs"), plan, journalForSeed: null);

        ((IRunObserver)decorator).AttemptFinished(task, AttemptRecordFixture(1, AttemptOutcome.ActionFailed));

        Assert.Single(inner.Calls);
        Assert.Same(task, inner.Calls[0].Task);
        Assert.Equal(1, inner.Calls[0].Attempt);
        Assert.Equal(AttemptOutcome.ActionFailed, inner.Calls[0].Outcome);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void OnTheFlyLogSiteObserver_ForwardsOutcomeVerbatim()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        var inner = new RecordingObserver();
        TaskNode task = FlatTask("01-first");
        var decorator = new OnTheFlyLogSiteObserver(inner, logsRoot, "test-run", [task], liveUrlForTask: null);

        ((IRunObserver)decorator).AttemptFinished(task, AttemptRecordFixture(2, AttemptOutcome.MaxTurns));

        Assert.Single(inner.Calls);
        // Not merely "something arrived" — the EXACT outcome, so a decorator that forwards a hard-coded or
        // mis-mapped value cannot pass by coincidence.
        Assert.Equal(AttemptOutcome.MaxTurns, inner.Calls[0].Outcome);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Every AttemptOutcome, on every decorator — a reflection-style sweep over the enum so a decorator
    // that special-cases one value (or maps several to the same wrong one) cannot hide behind a test that
    // only ever tried ActionFailed/MaxTurns.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllOutcomes() =>
        Enum.GetValues<AttemptOutcome>().Select(o => new object[] { o });

    [Trait("Category", "RunEvents")]
    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void EveryDecorator_ForwardsAttemptFinished_ForEveryOutcome(AttemptOutcome outcome)
    {
        using var tree = new TempTree();
        TaskNode task = FlatTask("01-first");
        var plan = new PlanDefinition
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        var logSiteInner = new RecordingObserver();
        var logSiteDecorator = new OnTheFlyLogSiteObserver(
            logSiteInner, tree.Dir("logs", "log-site-run"), "log-site-run", [task], liveUrlForTask: null);
        ((IRunObserver)logSiteDecorator).AttemptFinished(task, AttemptRecordFixture(1, outcome));

        Assert.Single(logSiteInner.Calls);
        Assert.Equal(outcome, logSiteInner.Calls[0].Outcome);

        var diagramInner = new RecordingObserver();
        var diagramDecorator = new OnTheFlyDiagramObserver(diagramInner, tree.Dir("logs", "diagram-run"), plan, journalForSeed: null);
        ((IRunObserver)diagramDecorator).AttemptFinished(task, AttemptRecordFixture(1, outcome));

        Assert.Single(diagramInner.Calls);
        Assert.Equal(outcome, diagramInner.Calls[0].Outcome);
    }
}
