using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.Review;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-RED (issue #361 Phase 4, doc 12 §5.2 Option E / Option P + §5 floor 3 "never forged"): the
/// <b>unattended review-gate resolution</b> for a freshly machine-authored, UNREVIEWED wave. Drives the REAL
/// <see cref="Scheduler"/> wave loop (the #120 composition-root discipline — NOT a hand-injected resolver in
/// isolation): a waved plan reaches the between-wave JIT checkpoint (SSOT §14.4), the breakdown authors +
/// validates the next wave, and the run arrives at the review gate. This mirrors
/// <see cref="SchedulerWaveBreakdownTests"/> for shape (real on-disk waved plan via
/// <see cref="WavePlanBuilder"/> + real <see cref="RunJournal"/> + <see cref="RecordingWorktreeProvider"/>,
/// no git/process, a STUB <see cref="IPromptRunner"/> that simulates plan-breakdown by writing a valid
/// <c>tasks/</c>).
///
/// <para>The resolution is NOT wired yet (task 05 owns <see cref="Scheduler"/>): current code always
/// honest-halts <see cref="WaveHaltKind.BreakdownComplete"/> after the breakdown, so tests 1 and 2 FAIL
/// against current code (the <c>tests-fail-on-current-code</c> gate). Test 3 asserts the §5 floor-3 invariant
/// (the harness never writes a <see cref="ReviewMarker"/>), which already holds and must keep holding at every
/// dial setting.</para>
/// </summary>
public sealed class SchedulerReviewGateTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    /// <summary>The freshly authored wave's single task id (<see cref="AuthorValidWave"/> writes <c>01-compile</c>).</summary>
    private const string Wave2Task = "wave-02-build/01-compile";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A recording fake executor: every green task defers its settle to the Scheduler's B1 (which journals it
    /// succeeded, exactly as a real worktree run does), and every started task id is captured in
    /// <see cref="Started"/> so a test can assert whether the unreviewed wave RAN.
    /// </summary>
    private sealed class RecordingExecutor : ITaskExecutor
    {
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
        }
    }

    /// <summary>
    /// A STUB breakdown prompt runner: on invocation it runs <paramref name="author"/> (the test's simulated
    /// authoring of the wave's <c>tasks/</c>) and returns a canned success — NO real Claude process is spawned.
    /// </summary>
    private sealed class StubBreakdownRunner(Action<PromptInvocation> author) : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            author(invocation);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored the wave",
                CostUsd = 0.42m,
                Summary = "breakdown authored the wave"
            });
        }
    }

    /// <summary>A plan with wave-01 authored + wave-02 an empty JIT stub carrying an opt-in <c>brief.md</c>.</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) WavedPlanWithStubWave2()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.RootDir(Path.Combine(Wave2, "tasks")); // wave-02 folder present, tasks/ empty = JIT stub
        File.WriteAllText(
            Path.Combine(b.PlanDir, Wave2, WaveNode.BriefFileName),
            "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");
        return (b, b.Load().Plan!);
    }

    /// <summary>Author a VALID single-task wave into <c>&lt;plan&gt;/wave-02-build/tasks/01-compile</c> (task.json + action + a guardrail).</summary>
    private static void AuthorValidWave(PromptInvocation inv)
    {
        string taskDir = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-compile");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "compile", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    /// <summary>
    /// The plan under <c>autonomyPolicy: auto</c> (so the JIT checkpoint auto-invokes the breakdown) with the
    /// dial (<see cref="AutonomyConfig"/>) engaged: <paramref name="reviewGate"/> ⇒ the
    /// <c>gateThresholds.review-gate</c> override, or its ABSENT default (escalate) when null.
    /// </summary>
    private static PlanDefinition AutoWithReviewGate(PlanDefinition plan, ReviewGateDecision? reviewGate) =>
        plan with
        {
            Config = plan.Config with
            {
                AutonomyPolicy = AutonomyPolicy.Auto,
                Autonomy = new AutonomyConfig
                {
                    GateThresholds = reviewGate is { } rg ? new GateThresholds { ReviewGate = rg } : null
                }
            }
        };

    /// <summary>The firstmate escalation seam a real unattended run wires — the review-gate escalation records through it (doc 12 §7.2).</summary>
    private static FileEscalationSink Sink(WavePlanBuilder b, RunJournal journal) =>
        new(logsRoot: Path.Combine(b.PlanDir, "logs"), journal, IRunObserver.Null, escalationThreshold: "high");

    private static Scheduler NewScheduler(
        PlanDefinition plan, ITaskExecutor exec, RunJournal journal, IWorktreeProvider provider,
        WaveBreakdownInvoker invoker, IEscalationSink sink) =>
        new(plan, exec, journal,
            worktreeProvider: provider, observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, escalationSink: sink);

    // --- 1. proceed-unreviewed ⇒ the unreviewed wave RUNS + a proceeded-unreviewed decision is recorded -----

    [Fact]
    public async Task ProceedUnreviewed_UnreviewedWaveRuns_RecordsProceededUnreviewedDecision()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = AutoWithReviewGate(plan, ReviewGateDecision.ProceedUnreviewed);

        var exec = new RecordingExecutor();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        await NewScheduler(autoPlan, exec, journal, new RecordingWorktreeProvider(), invoker, Sink(b, journal))
            .RunAsync(autoPlan, Ct);

        // Option P (§5.2): with review-gate == proceed-unreviewed the freshly authored wave is NOT halted for
        // review — it RUNS. (Current code halts BreakdownComplete, so its task never starts → this FAILS now.)
        Assert.Contains(Wave2Task, exec.Started);

        // The run's durable decisions[] carries a boundary:"wave", decision:"proceeded-unreviewed" entry.
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == DecisionTokens.ProceededUnreviewed && d.Subject == Wave2);
    }

    // --- 2. default (escalate) ⇒ the unreviewed wave HALTS with a review-gate escalation + does NOT run ------

    [Fact]
    public async Task DefaultEscalate_UnreviewedWaveHalts_RecordsReviewGateEscalation_DoesNotRun()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = AutoWithReviewGate(plan, reviewGate: null); // review-gate at its default (escalate)

        var exec = new RecordingExecutor();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        RunReport report = await NewScheduler(autoPlan, exec, journal, new RecordingWorktreeProvider(), invoker, Sink(b, journal))
            .RunAsync(autoPlan, Ct);

        // Option E (§5.2): the default review gate ESCALATES — a review-gate escalation is recorded in the
        // durable decisions[] (via the sink). (Current code records only the breakdown 'auto-applied' entry, so
        // no such escalated/review-gate decision exists → this FAILS now.)
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.Escalated && d.Gate == "review-gate" && d.Subject == Wave2);

        // The unreviewed wave's tasks do NOT run, and the run does not report the wave green-and-reviewed.
        Assert.DoesNotContain(Wave2Task, exec.Started);
        Assert.False(report.AllSucceeded);
    }

    // --- 3. §5 floor 3: neither resolution writes a review marker (the harness never self-attests) -----------

    [Fact]
    public async Task NeitherResolution_WritesAReviewMarker_TheNeverForgedFloor()
    {
        // Resolution P (proceed-unreviewed): the wave runs unreviewed — but the harness STILL writes no marker.
        (WavePlanBuilder bp, PlanDefinition planP) = WavedPlanWithStubWave2();
        using WavePlanBuilder _p = bp;
        PlanDefinition proceedPlan = AutoWithReviewGate(planP, ReviewGateDecision.ProceedUnreviewed);
        RunJournal journalP = RunJournal.LoadOrCreate(proceedPlan);
        await NewScheduler(proceedPlan, new RecordingExecutor(), journalP, new RecordingWorktreeProvider(),
            new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave)), Sink(bp, journalP))
            .RunAsync(proceedPlan, Ct);

        Assert.False(File.Exists(ReviewMarker.PathFor(bp.PlanDir)),
            "proceed-unreviewed ran the wave — but the harness must NOT forge state/guardrails-review.json (§5 floor 3)");

        // Resolution E (default escalate): the wave halts for review — and the harness writes no marker either.
        (WavePlanBuilder be, PlanDefinition planE) = WavedPlanWithStubWave2();
        using WavePlanBuilder _e = be;
        PlanDefinition escalatePlan = AutoWithReviewGate(planE, reviewGate: null);
        RunJournal journalE = RunJournal.LoadOrCreate(escalatePlan);
        await NewScheduler(escalatePlan, new RecordingExecutor(), journalE, new RecordingWorktreeProvider(),
            new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave)), Sink(be, journalE))
            .RunAsync(escalatePlan, Ct);

        Assert.False(File.Exists(ReviewMarker.PathFor(be.PlanDir)),
            "the review gate is a human action — the harness never self-attests a review at any dial setting (§5 floor 3)");
    }
}
