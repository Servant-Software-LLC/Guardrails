using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// TDD-RED (design 39 §1a, sharpened in review round 4 <c>d39-interlock-ride-along</c>): the #361/#340
/// delivery interlock — "machine-decided work defaults delivery OFF" — RESCOPED from the whole run to the
/// SET of waves a single delivery carries. Per-wave delivery merges BEFORE the run ends, so a run-scoped
/// check (evaluated once, at the end) can be bypassed two ways: a wave ships while a later wave's decision
/// still lands, or a later decision reaches back and holds waves that already shipped.
///
/// <para><b>DECIDED (round 2, sharpened round 4):</b> a delivery is held when ANY wave it carries recorded a
/// suppressing decision — not just the delivering wave's own. The narrower reading would let a clean wave's
/// delivery carry an earlier HELD wave's machine-decided commits onto the user's branch, which is exactly
/// the #361 escape the decision closes.</para>
///
/// <para><b>§1a's premise was false, and correcting it is the first stub here (review, 2026-09-11).</b>
/// <see cref="DecisionEntry"/> carried no wave attribution; <see cref="DecisionEntry.Subject"/> is free text
/// whose meaning varies by <see cref="DecisionEntry.Boundary"/> — a wave dir, a task id, or a comma-joined
/// drift list — so deriving the wave by splitting it would be silently wrong for exactly the boundaries that
/// matter. <see cref="DecisionEntry.Wave"/> is the real member task 06 populates.</para>
///
/// <para>The second stub is the delivery-scoped entry point, <see cref="RunOutcomePolicy.SuppressingDecisionForDelivery"/>
/// / <see cref="RunOutcomePolicy.SuppressesDelivery(IEnumerable{DecisionEntry}, IReadOnlyCollection{string})"/>
/// — both throw <see cref="NotImplementedException"/> until task 06. Every row below except the
/// declared-exempt one either calls the new pair or reads a <see cref="DecisionEntry.Wave"/> that no
/// decision-creating site populates yet, which is what makes each one red on the stubs.</para>
/// </summary>
public sealed class WaveScopedInterlockTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";
    private const string Wave3 = "wave-03-later";

    /// <summary>The freshly authored wave's single task id (<see cref="AuthorValidWave"/> writes <c>01-compile</c>).</summary>
    private const string Wave2Task = "wave-02-build/01-compile";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Required-member helper: a DecisionEntry carrying the decision token + wave attribution under test.
    // Mirrors RunOutcomePolicyTests.Decision, plus the Wave member this task adds.
    private static DecisionEntry Decision(string token, string? wave, string subject = "wave-02-build") => new()
    {
        Boundary = wave is not null ? "wave" : "task",
        Policy = "auto",
        Decision = token,
        Subject = subject,
        Headline = $"recorded decision: {token}",
        Wave = wave
    };

    // ── The DECIDED per-delivery scoping (design 39 §1a, round 2/round 4) ──────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void AWaveWithNoSuppressingDecision_Delivers()
    {
        var decisions = new[]
        {
            Decision(DecisionTokens.Escalated, Wave1),
            Decision(DecisionTokens.Halted, Wave2)
        };

        Assert.False(RunOutcomePolicy.SuppressesDelivery(decisions, new[] { Wave2 }));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void AWaveWithASuppressingDecision_DoesNotDeliver()
    {
        var decisions = new[] { Decision(DecisionTokens.ProceededBestGuess, Wave2) };

        Assert.True(RunOutcomePolicy.SuppressesDelivery(decisions, new[] { Wave2 }));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ADecisionInALaterWave_DoesNotRetroactivelySuppressAnEarlierDelivery()
    {
        // The direction that cannot be undone: wave-01 already delivered, covering only itself. A
        // suppressing decision recorded later, in wave-02, cannot reach back and hold that merge.
        var decisions = new[] { Decision(DecisionTokens.ProceededBestGuess, Wave2) };

        Assert.False(RunOutcomePolicy.SuppressesDelivery(decisions, new[] { Wave1 }));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ADecisionInAnEarlierDeliveredWave_DoesNotSuppressALaterCleanWave()
    {
        // The opposite direction: wave-01's work already reached the user's branch, so it is not in the
        // LATER delivery's covered set — its decision does not hold a clean wave-02/wave-03 delivery. The
        // scoping is genuinely per delivery, not a sticky run-level flag wearing a wave's name.
        var decisions = new[] { Decision(DecisionTokens.ProceededBestGuess, Wave1) };

        Assert.False(RunOutcomePolicy.SuppressesDelivery(decisions, new[] { Wave2, Wave3 }));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void AHeldWavesWorkRidingAlong_HoldsTheLaterDelivery()
    {
        // wave-02 recorded a proceeded-best-guess and is held; wave-03 is clean. The delivery AT wave-03
        // covers both wave-02 and wave-03 (a delivery carries every wave since the previous delivery
        // point), so wave-02's held work riding along holds the wave-03 delivery too — and the evidence
        // returned is wave-02's decision, not a bare bool.
        DecisionEntry heldAtWave2 = Decision(DecisionTokens.ProceededBestGuess, Wave2);
        var decisions = new[] { heldAtWave2 };
        var coveredWaves = new[] { Wave2, Wave3 };

        Assert.True(RunOutcomePolicy.SuppressesDelivery(decisions, coveredWaves));
        Assert.Same(heldAtWave2, RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, coveredWaves));
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void TheWaveAttributionIsRecordedOnTheDecision_NotParsedFromSubject()
    {
        // Subject deliberately names a DIFFERENT wave-looking token than the recorded Wave member. A test
        // that passed by string-splitting Subject would certify the exact parsing convention this task
        // exists to avoid: it would derive "wave-99-decoy" here, missing the real match entirely and
        // wrongly matching on a coveredWaves set that merely happens to contain the decoy string.
        DecisionEntry decision = Decision(DecisionTokens.ProceededBestGuess, Wave2, subject: "wave-99-decoy/some-task-id");
        var decisions = new[] { decision };

        Assert.Same(decision, RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, new[] { Wave2 }));
        Assert.Null(RunOutcomePolicy.SuppressingDecisionForDelivery(decisions, new[] { "wave-99-decoy" }));
    }

    // ── Driving the REAL Scheduler (the composition-root discipline, #120) ─────────────────────────────
    // Copied/trimmed from SchedulerReviewGateTests, whose helpers are private nested types: a WavePlanBuilder
    // plan with wave-01 authored and wave-02 an empty JIT stub carrying a brief.md, autonomyPolicy: auto with
    // gateThresholds.review-gate set to proceed-unreviewed, a stub breakdown runner that authors wave-02, a
    // RecordingWorktreeProvider, a real RunJournal and a FileEscalationSink.

    private sealed class RecordingExecutor : ITaskExecutor
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Started { get; } = [];

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

    private static (WavePlanBuilder Builder, PlanDefinition Plan) WavedPlanWithStubWave2()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.RootDir(Path.Combine(Wave2, "tasks"));
        File.WriteAllText(
            Path.Combine(b.PlanDir, Wave2, WaveNode.BriefFileName),
            "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");
        return (b, b.Load().Plan!);
    }

    private static void AuthorValidWave(PromptInvocation inv)
    {
        string taskDir = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-compile");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "compile", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    private static PlanDefinition AutoWithReviewGate(PlanDefinition plan, ReviewGateDecision reviewGate) =>
        plan with
        {
            Config = plan.Config with
            {
                AutonomyPolicy = AutonomyPolicy.Auto,
                Autonomy = new AutonomyConfig { GateThresholds = new GateThresholds { ReviewGate = reviewGate } }
            }
        };

    private static FileEscalationSink Sink(WavePlanBuilder b, RunJournal journal) =>
        new(logsRoot: Path.Combine(b.PlanDir, "logs"), journal, IRunObserver.Null, escalationThreshold: "high");

    private static Scheduler NewScheduler(
        PlanDefinition plan, ITaskExecutor exec, RunJournal journal, IWorktreeProvider provider,
        WaveBreakdownInvoker invoker, IEscalationSink sink) =>
        new(plan, exec, journal,
            worktreeProvider: provider, observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, escalationSink: sink);

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task TheSchedulersProceededUnreviewedDecision_RecordsItsWave()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = AutoWithReviewGate(plan, ReviewGateDecision.ProceedUnreviewed);

        var exec = new RecordingExecutor();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        await NewScheduler(autoPlan, exec, journal, new RecordingWorktreeProvider(), invoker, Sink(b, journal))
            .RunAsync(autoPlan, Ct);

        // Without a real decision site stamping Wave, this proves nothing on its own — that is why every
        // other decision creator here is hand-built. This is the ONE row that drives the real Scheduler, so
        // it is what catches task 06 implementing the policy while stamping Wave at neither call site.
        DecisionEntry? recorded = (journal.Document.Decisions ?? []).FirstOrDefault(d =>
            d.Boundary == "wave" && d.Decision == DecisionTokens.ProceededUnreviewed && d.Subject == Wave2);

        Assert.NotNull(recorded);
        Assert.Equal(Wave2, recorded!.Wave);
    }

    // ── Issue #710: the report carries the resolved delivery setting beside the decision ───────────────
    // Not a wave-scoping row: it lives here because this is the Core harness that drives the real Scheduler to a
    // recorded suppressing decision. #597 stamped the decision and nothing else, so the CLI could not tell "the
    // interlock held it" from "delivery was off AND the interlock would have held it" — and on the second it
    // printed "mergeOnSuccess is ON". Both facts have to reach the report from the REAL Finalize path.

    [Fact]
    public async Task WithDeliveryTurnedOff_TheReportCarriesTheResolvedSetting_BesideTheSuppressingDecision()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = AutoWithReviewGate(plan, ReviewGateDecision.ProceedUnreviewed);
        PlanDefinition offPlan = autoPlan with
        {
            // Exactly what RunCommand writes for --no-merge-on-success.
            Config = autoPlan.Config with { MergeOnSuccess = false, MergeOnSuccessSource = MergeOnSuccessSource.Flag }
        };

        var exec = new RecordingExecutor();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunJournal journal = RunJournal.LoadOrCreate(offPlan);

        RunReport report = await NewScheduler(
                offPlan, exec, journal, new RecordingWorktreeProvider(), invoker, Sink(b, journal))
            .RunAsync(offPlan, Ct);

        // Both causes are true on this run: the fixture reached the interlock, AND delivery was off.
        Assert.True(report.WhollyGreenButUndelivered);
        Assert.NotNull(report.DeliverySuppressingDecision);

        // The report must say so. RunReport.MergeOnSuccess defaults to true, so a Scheduler that never stamped it
        // would leave this run reading "on, held by the interlock" — the exact false statement #710 reported.
        Assert.False(report.MergeOnSuccess);
        Assert.Equal(MergeOnSuccessSource.Flag, report.MergeOnSuccessSource);
    }

    // ── DECLARED EXEMPT from the red census: the operator override already wins on current code ────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task TheOperatorOverrideStillLiftsTheInterlock()
    {
        // #361/#597: --merge-on-success already lifts the RUN-END interlock today, through the
        // single-argument RunOutcomePolicy.SuppressingDecision this row never changes. Wave scoping
        // changes WHICH decisions suppress, never whether the override lifts them — so this is green on
        // arrival, and pins that the change must keep it that way. The barrier delivery's own override
        // belongs to task 08 (task 07 pins it); this exercises only Finalize's run-end call.
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = AutoWithReviewGate(plan, ReviewGateDecision.ProceedUnreviewed);
        PlanDefinition forcedPlan = autoPlan with
        {
            Config = autoPlan.Config with { MergeOnSuccessForcedByOperator = true }
        };

        var exec = new RecordingExecutor();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunJournal journal = RunJournal.LoadOrCreate(forcedPlan);

        RunReport report = await NewScheduler(
                forcedPlan, exec, journal, new RecordingWorktreeProvider(), invoker, Sink(b, journal))
            .RunAsync(forcedPlan, Ct);

        // The run recorded a suppressing decision (proceeded-unreviewed) — the interlock would otherwise
        // hold delivery — but the explicit operator override forced it through anyway.
        Assert.NotNull(report.DeliverySuppressingDecision);
        Assert.True(report.DeliveryForcedPastDecision);
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);
    }
}
