using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The JIT breakdown's OBSERVER contract and its halt text (design of record
/// <c>docs/plans/23-jit-breakdown-visibility.md</c>, issue #469). Same shape as
/// <see cref="SchedulerBreakdownDurabilityTests"/>: a real on-disk waved plan, a real journal, and a stub
/// <see cref="IPromptRunner"/> standing in for the plan-breakdown sub-process.
///
/// <para>What these pin:</para>
/// <list type="number">
///   <item><b>The silence was total.</b> <c>WaveStarting</c> fires only AFTER the checkpoint, so before this
///     a wave could be authored for 30 minutes with no observer call of any kind. <c>Starting</c> must fire
///     BEFORE the runner is invoked, and carry the real probe targets.</item>
///   <item><b>The count is the HARNESS's.</b> <c>Finished</c> is raised after <c>guardrails validate</c>, so
///     <c>authoredTaskCount</c> can never be the session's own claim (invariant 1).</item>
///   <item><b>A halting path never hands on the authored wave</b> — that argument is the #404 seam and it
///     means "the run will PROCEED with this", not "the breakdown produced something".</item>
///   <item><b>The #485 rule.</b> A fully-authored waved plan raises NEITHER event.</item>
///   <item><b>T9/T10 — the halt text.</b> The next action INVERTS between a failed breakdown (starts from
///     scratch) and an incomplete one (resumes from the preserved prefix), and an incomplete wave must say
///     out loud that it is not ready for review.</item>
/// </list>
/// </summary>
public sealed class SchedulerBreakdownPhaseEventsTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- fakes -------------------------------------------------------------------------------------

    private sealed class GreenExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct) =>
            Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
    }

    /// <summary>Records the phase events (and the ORDER they interleave with the invocation) for assertion.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Order { get; } = [];

        public List<WaveBreakdownContext> Started { get; } = [];

        public List<(WaveBreakdownContext Context, TimeSpan Elapsed, int Count, string? Failure, WaveNode? Wave)>
            Finished { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void WaveStarting(WaveNode wave, int index, int total) => Order.Add($"wave-starting:{wave.Dir}");

        public void WaveBreakdownStarting(WaveBreakdownContext context)
        {
            Order.Add($"breakdown-starting:{context.WaveDir}");
            Started.Add(context);
        }

        public void WaveBreakdownFinished(
            WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
            WaveNode? authoredWave)
        {
            Order.Add($"breakdown-finished:{context.WaveDir}");
            Finished.Add((context, elapsed, authoredTaskCount, failureKind, authoredWave));
        }
    }

    private sealed class StubBreakdownRunner(
        Action<PromptInvocation> author,
        PromptFailureKind failureKind = PromptFailureKind.None,
        Action? onInvoke = null) : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            onInvoke?.Invoke();
            author(invocation);
            bool clean = failureKind == PromptFailureKind.None;
            return Task.FromResult(new PromptResult
            {
                Completed = clean,
                IsError = false,
                ResultText = clean ? "authored the wave" : null,
                CostUsd = 0.10m,
                FailureKind = failureKind,
                Summary = clean ? "breakdown authored the wave" : "breakdown was cut off"
            });
        }
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, RunJournal journal, IRunObserver observer, WaveBreakdownInvoker invoker) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: observer, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null);

    /// <summary>wave-01 authored; wave-02 a JIT stub with a brief; autonomyPolicy auto so the checkpoint fires.</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) JitPlan()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n- package\n");
        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    /// <summary>Both waves authored up front — the case the #485 rule protects: nothing new may be raised.</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) FullyAuthoredPlan()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.Task(Wave2, "01-compile");
        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    private static void AuthorTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    private static void DeclareIntent(string planDir, params string[] folders)
    {
        string stateDir = Path.Combine(planDir, Wave2, "state");
        Directory.CreateDirectory(stateDir);
        string entries = string.Join(",\n    ",
            folders.Select(f => $$"""{ "folder": "{{f}}", "purpose": "author {{f}}" }"""));
        File.WriteAllText(Path.Combine(stateDir, Loading.BreakdownIntent.FileName),
            $$"""
            {
              "version": 1,
              "declaredAt": "2026-08-20T05:00:00Z",
              "tasks": [
                {{entries}}
              ]
            }
            """);
    }

    // --- 1. the phase is announced BEFORE the session, with the real probe targets -----------------

    [Fact]
    public async Task BreakdownStarting_FiresBeforeTheRunner_AndBeforeWaveStarting_WithRealProbeTargets()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            inv => AuthorTask(inv.WorkingDirectory, "01-compile"),
            onInvoke: () => observer.Order.Add("runner-invoked")));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        // The whole finding of #469: this event has to exist and has to precede the session. WaveStarting
        // fires only after the checkpoint, so it can never stand in for it.
        Assert.Equal(
            ["breakdown-starting:wave-02-build", "runner-invoked", "breakdown-finished:wave-02-build"],
            observer.Order.Where(o => o != $"wave-starting:{Wave1}").Take(3).ToArray());

        WaveBreakdownContext context = Assert.Single(observer.Started);
        Assert.Equal(Wave2, context.WaveDir);
        Assert.Equal(2, context.Index);
        Assert.Equal(2, context.Total);
        Assert.Equal(WaveBreakdownInvoker.BreakdownTimeout, context.Ceiling);

        // The probe targets are real paths the UI can stat, not names it has to reconstruct.
        Assert.Equal(Path.Combine(b.PlanDir, Wave2, "tasks"), context.TasksDirectory);
        Assert.True(Directory.Exists(context.BreakdownLogDir));
        Assert.Equal(
            Path.Combine(context.BreakdownLogDir, "claude-stream.jsonl"),
            context.StreamLogPath);

        // The composed prompt was teed and its size reported — the log-site evidence figure, and the one
        // signal design 23 §4 keeps OFF every live surface.
        Assert.True(context.ComposedPromptBytes > 0);
        Assert.True(File.Exists(Path.Combine(context.BreakdownLogDir, "composed-prompt.md")));
    }

    [Fact]
    public async Task BreakdownFinished_CarriesTheHarnessesOwnTaskCount_NotTheSessionsClaim()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(inv =>
        {
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
        }));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        var finished = Assert.Single(observer.Finished);
        Assert.Equal(2, finished.Count);   // what `guardrails validate` LOADED, after the gate ran
        Assert.Null(finished.Failure);     // the session ended cleanly and the wave was accepted

        // The default review gate ESCALATES, so the run halts for /guardrails-review and does NOT proceed
        // with the wave — the authoredWave argument means "the run will run this", not "this exists".
        Assert.Null(finished.Wave);
    }

    [Fact]
    public async Task CutOffSession_ReportsTheRunnersOwnStopToken_SoTheUiCanNameTheBound()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            inv => AuthorTask(inv.WorkingDirectory, "01-compile"), PromptFailureKind.Timeout));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        // "timeout" and "max-turns" are two different remedies and only one of them is a budget; the token
        // is carried so no surface has to re-derive it from prose.
        Assert.All(observer.Finished, f => Assert.Equal("timeout", f.Failure));
    }

    [Fact]
    public async Task ValidateRejectedTheWave_ReportsInvalid_NotAGreenAuthored()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        // A clean session that wrote nothing usable: the SESSION succeeded, the harness's gate did not.
        // Reporting null here would settle the live row GREEN for a run that is about to halt.
        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(_ => { }));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        var finished = Assert.Single(observer.Finished);
        Assert.Equal(0, finished.Count);
        Assert.Equal("invalid", finished.Failure);
    }

    [Fact]
    public async Task PreservedPrefix_ReportsIncomplete_SoTheRowNeverReadsAsCutOffOrAuthored()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(inv =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
        }, PromptFailureKind.Timeout));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        // Every declared folder is on disk and the wave validates, but the session never reported
        // completion — the settlement is INCOMPLETE, and the phase event says so rather than "timeout".
        Assert.Equal("incomplete", observer.Finished[^1].Failure);
    }

    // --- 2. the #485 rule: a fully-authored waved plan raises NEITHER event ------------------------

    [Fact]
    public async Task FullyAuthoredWavedPlan_RaisesNeitherPhaseEvent()
    {
        (WavePlanBuilder b, PlanDefinition plan) = FullyAuthoredPlan();
        using WavePlanBuilder _ = b;

        var observer = new RecordingObserver();
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(_ => { }));

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, invoker).RunAsync(plan, Ct);

        Assert.Empty(observer.Started);
        Assert.Empty(observer.Finished);
    }

    // --- 3. T9 / T10: the halt text, and the next action that INVERTS between the two --------------

    [Fact]
    public async Task T9_BreakdownFailedDetail_SaysTheRerunStartsFromScratch_AndNeverClaimsAnEmptyStub()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(_ => { }, PromptFailureKind.Timeout));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingObserver(), invoker)
            .RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);

        // #471's regression, on the text: the deleted sentence stays deleted.
        Assert.DoesNotContain("reverted to its empty stub", report.WaveHalt.Detail);

        // The next action, and the half of it that inverts against BreakdownIncomplete: this attempt was
        // reverted, so a re-run does NOT resume — it starts over, and something has to change first.
        Assert.Contains("starts FROM SCRATCH", report.WaveHalt.Detail);
        Assert.DoesNotContain("RESUMES", report.WaveHalt.Detail);
    }

    [Fact]
    public async Task T10_BreakdownIncompleteDetail_SaysNotReadyForReview_AndNamesTheResume()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        // Declares two, authors one, is cut off — a valid PREFIX that a human will read as a finished wave
        // unless the halt says otherwise. That sentence is design 20 §4.2's safety floor, operator-facing.
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(inv =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingObserver(), invoker)
            .RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);
        Assert.Contains("is NOT ready for review", report.WaveHalt.Detail);
        Assert.Contains("Do not run /guardrails-review on it yet", report.WaveHalt.Detail);

        // The inverted next action: this one RESUMES from the preserved prefix.
        Assert.Contains("RESUMES", report.WaveHalt.Detail);
        Assert.Contains("preserved prefix", report.WaveHalt.Detail);
        Assert.DoesNotContain("FROM SCRATCH", report.WaveHalt.Detail);
    }

    // --- 4. the durable discriminator the log site keys its post-mortem off ------------------------

    [Fact]
    public async Task BreakdownSettlement_IsJournaledWithItsGateToken_SoThePostMortemIsNotProseMatching()
    {
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(_ => { }, PromptFailureKind.Timeout));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        await NewScheduler(plan, journal, new RecordingObserver(), invoker).RunAsync(plan, Ct);

        // A breakdown halt is NOT a RunHalt (every halt.kind is a deterministic-gate kind), so decisions[]
        // is the only durable record the log site can key its wave-page phase panel off.
        DecisionEntry entry = Assert.Single(
            JournalReader.Read(RunJournal.PathFor(b.PlanDir)).Decisions ?? [],
            d => BreakdownGates.IsBreakdown(d.Gate));
        Assert.Equal(BreakdownGates.Failed, entry.Gate);
        Assert.Equal(Wave2, entry.Subject);
        Assert.Contains("starts FROM SCRATCH", entry.Detail);
    }
}
