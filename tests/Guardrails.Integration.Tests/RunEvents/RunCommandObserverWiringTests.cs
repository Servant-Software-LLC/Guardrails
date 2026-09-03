using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// The COMPOSITION-ROOT half of plan 34's two projections — the guardrail that decides whether any of
/// the rest is real. <see cref="RunEventStream"/> and <see cref="ObserverProjection"/> can be fully built
/// and fully unit-tested while <c>RunCommand</c> never actually constructs either of them, in which case
/// the feature is reachable only from xUnit and inert from the CLI. That failure has recurred three times
/// in this repo at exactly this kind of seam.
///
/// <para>Every test here drives the REAL <see cref="RunCommand.BuildObserverChain"/> extracted in task 13
/// (the exact method both the live-UI and <c>--no-ui</c> branches of <c>guardrails run</c> call) and
/// observes its EXTERNAL, on-disk effect — <c>events.jsonl</c> / <c>observer.jsonl</c> appearing under the
/// run's log directory after an event is driven through the chain it returns. None of them construct
/// <see cref="RunEventStream"/>/<see cref="ObserverProjection"/> directly and hand them in: injecting the
/// thing under test would make the assertion pass even when production never wires it, which is the whole
/// defect this class exists to catch.</para>
///
/// <para>Written to FAIL right now: task 13 extracted <c>BuildObserverChain</c> without changing what it
/// builds — still only <see cref="OnTheFlyLogSiteObserver"/> wrapped by <see cref="OnTheFlyDiagramObserver"/>
/// — so neither projection is constructed yet and neither file appears. Task 15 wires them in; this file is
/// not touched by that task, or by any task after this one.</para>
/// </summary>
public sealed class RunCommandObserverWiringTests
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

    private static PlanDefinition MinimalPlan(IReadOnlyList<TaskNode> tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks
    };

    /// <summary>A throwaway directory tree standing in for the run's own <c>logs/&lt;runId&gt;/</c> shape.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-observer-wiring-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        /// <summary>A created subdirectory under <see cref="Root"/> — mirrors <c>logs/&lt;runId&gt;/</c>.</summary>
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
    /// The pre-existing observer <c>BuildObserverChain</c> is already built around in production (the live
    /// table or the plain console). Records the WHOLE call, not a count — the CONTRAST test needs to know
    /// the chain still reaches it with the right arguments, not merely that something fired.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(TaskNode Task, int Attempt, AttemptOutcome Outcome)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void AttemptFinished(TaskNode task, int attempt, AttemptOutcome outcome) =>
            Calls.Add((task, attempt, outcome));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void BuildObserverChain_ConstructsTheEventsProjection()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "events-projection-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);

        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "events-projection-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.AttemptFinished(task, 1, AttemptOutcome.Succeeded);

        string eventsPath = Path.Combine(logsRoot, "events.jsonl");
        Assert.True(
            File.Exists(eventsPath),
            "events.jsonl was never written — BuildObserverChain does not construct RunEventStream, so an "
            + "AttemptFinished driven through the real chain leaves no trace a supervising agent could read.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllLines(eventsPath).Single());
        JsonElement root = doc.RootElement;
        Assert.Equal("attempt-finished", root.GetProperty("kind").GetString());
        Assert.Equal("events-projection-run", root.GetProperty("runId").GetString());
        Assert.Equal(task.Id, root.GetProperty("taskId").GetString());
        Assert.Equal(1, root.GetProperty("attempt").GetInt32());
        Assert.Equal(JournalJson.OutcomeToken(AttemptOutcome.Succeeded), root.GetProperty("outcome").GetString());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void BuildObserverChain_ConstructsTheObserverProjection()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "observer-projection-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);

        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "observer-projection-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.AttemptFinished(task, 2, AttemptOutcome.GuardrailFailed);

        string observerPath = Path.Combine(logsRoot, "observer.jsonl");
        Assert.True(
            File.Exists(observerPath),
            "observer.jsonl was never written — BuildObserverChain does not construct ObserverProjection, so "
            + "`guardrails attach` has nothing to replay.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllLines(observerPath).Single());
        JsonElement root = doc.RootElement;
        Assert.Equal("AttemptFinished", root.GetProperty("member").GetString());
        Assert.Equal(task.Id, root.GetProperty("taskId").GetString());
        Assert.Equal(2, root.GetProperty("attempt").GetInt32());
        Assert.Equal(nameof(AttemptOutcome.GuardrailFailed), root.GetProperty("outcome").GetString());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void BuildObserverChain_WiresBothProjections_InTheNoUiBranch()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "no-ui-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);

        // Mirrors RunCommand's --no-ui branch exactly: BuildObserverChain(new ConsoleRunObserver(io.Out), ...).
        // An unattended run is exactly the configuration this feature exists to serve: a supervising agent
        // has no live table to watch, only these two files.
        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            new ConsoleRunObserver(TextWriter.Null), logsRoot, "no-ui-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.AttemptFinished(task, 1, AttemptOutcome.Succeeded);

        Assert.True(
            File.Exists(Path.Combine(logsRoot, "events.jsonl")),
            "the --no-ui branch's real observer chain never wrote events.jsonl.");
        Assert.True(
            File.Exists(Path.Combine(logsRoot, "observer.jsonl")),
            "the --no-ui branch's real observer chain never wrote observer.jsonl.");
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task BuildObserverChain_WiresBothProjections_InTheLiveUiBranch()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "live-ui-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);

        // Mirrors RunCommand's live-UI branch exactly: BuildObserverChain(new LiveRunObserver(...), ...).
        await using var liveObserver = new LiveRunObserver(
            plan.Tasks, logUrlForTask: null, plan.PlanDirectory, "live-ui-run", plan.Waves, showAllTasks: false);

        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            liveObserver, logsRoot, "live-ui-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.AttemptFinished(task, 1, AttemptOutcome.Succeeded);

        Assert.True(
            File.Exists(Path.Combine(logsRoot, "events.jsonl")),
            "the live-UI branch's real observer chain never wrote events.jsonl.");
        Assert.True(
            File.Exists(Path.Combine(logsRoot, "observer.jsonl")),
            "the live-UI branch's real observer chain never wrote observer.jsonl.");
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void BuildObserverChain_StillWiresTheExistingObservers()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "still-wires-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var inner = new RecordingObserver();

        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            inner, logsRoot, "still-wires-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.AttemptFinished(task, 4, AttemptOutcome.MaxTurns);

        // The CONTRAST: the observer this chain was already built around (the live table or plain console
        // in production) must STILL receive the call — proving the projections were ADDED into the chain,
        // not swapped in over the top of it. "Wired" means both halves work, not merely that two new files
        // happen to exist.
        (TaskNode Task, int Attempt, AttemptOutcome Outcome) call = Assert.Single(inner.Calls);
        Assert.Same(task, call.Task);
        Assert.Equal(4, call.Attempt);
        Assert.Equal(AttemptOutcome.MaxTurns, call.Outcome);

        Assert.True(
            File.Exists(Path.Combine(logsRoot, "events.jsonl")),
            "the chain kept its existing observer but never wrote events.jsonl.");
        Assert.True(
            File.Exists(Path.Combine(logsRoot, "observer.jsonl")),
            "the chain kept its existing observer but never wrote observer.jsonl.");
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptFinished_ThroughTheRealComposedChain_WritesEventsJsonl()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "real-chain-run");
        TaskNode taskOne = FlatTask("01-first");
        TaskNode taskTwo = FlatTask("02-second");
        PlanDefinition plan = MinimalPlan([taskOne, taskTwo]);

        OnTheFlyDiagramObserver chain = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "real-chain-run", plan, logUrlForTask: null, diagramSeed: null);

        // Two attempts, raised in sequence through the SAME composed chain instance — the shape a real run
        // actually produces (many attempts across a run), not a single isolated call. Each must land as its
        // own independent, parseable line on disk: this is the end-to-end proof that "an attempt raised
        // through the real composed chain reaches events.jsonl on disk", distinct from the narrower
        // single-shot wiring checks above.
        chain.AttemptFinished(taskOne, 1, AttemptOutcome.Succeeded);
        chain.AttemptFinished(taskTwo, 1, AttemptOutcome.GuardrailFailed);

        string eventsPath = Path.Combine(logsRoot, "events.jsonl");
        Assert.True(
            File.Exists(eventsPath),
            "an attempt raised through the REAL composed chain never reached events.jsonl on disk — "
            + "BuildObserverChain does not construct RunEventStream, so a supervising agent tailing "
            + "events.jsonl during a live run sees nothing.");

        string[] lines = File.ReadAllLines(eventsPath);
        Assert.Equal(2, lines.Length);

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(taskOne.Id, first.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(
            JournalJson.OutcomeToken(AttemptOutcome.Succeeded), first.RootElement.GetProperty("outcome").GetString());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal(taskTwo.Id, second.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(
            JournalJson.OutcomeToken(AttemptOutcome.GuardrailFailed), second.RootElement.GetProperty("outcome").GetString());
    }
}
