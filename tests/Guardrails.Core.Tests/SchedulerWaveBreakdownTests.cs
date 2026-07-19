using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.Review;

namespace Guardrails.Core.Tests;

/// <summary>
/// #360 Phase 1 — the between-wave breakdown invocation at the JIT wave checkpoint (SSOT §14.4, doc 11 §9).
/// Uses a real on-disk waved plan (wave-01 authored + wave-02 an empty JIT stub with a <c>brief.md</c>) + a
/// real <see cref="RunJournal"/> + <see cref="RecordingWorktreeProvider"/> (no git) + a fake executor, and a
/// STUB <see cref="IPromptRunner"/> that SIMULATES the plan-breakdown sub-process by writing a valid (or
/// invalid) <c>tasks/</c> — exactly how the overwatcher's tests stub its diagnose runner. NO real Claude call
/// is ever made (the stub returns a canned <see cref="PromptResult"/>).
/// </summary>
public sealed class SchedulerWaveBreakdownTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fake executor: every task defers its settle to the Scheduler's B1 (which journals it succeeded).</summary>
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

    /// <summary>
    /// A STUB breakdown prompt runner: on invocation it runs <paramref name="author"/> (the test's simulated
    /// authoring of the wave's <c>tasks/</c>) and returns a canned success result with a non-zero cost — NO
    /// real Claude process is spawned. <see cref="Invocations"/> counts calls so a test can assert the
    /// checkpoint did / did NOT invoke.
    /// </summary>
    private sealed class StubBreakdownRunner(Action<PromptInvocation> author) : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
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

    /// <summary>A stub that THROWS if invoked — proves the checkpoint never fired a breakdown.</summary>
    private sealed class NeverInvokedRunner : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("breakdown runner must NOT be invoked in this scenario");
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, RunJournal journal, IWorktreeProvider provider,
        WaveBreakdownInvoker? invoker = null, IReadOnlyDictionary<string, bool>? confirmations = null) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: provider, observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: confirmations);

    /// <summary>A plan with wave-01 authored + wave-02 an empty JIT stub carrying an OPTIONAL brief.md.</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) WavedPlanWithStubWave2(bool withBrief = true)
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.RootDir(Path.Combine(Wave2, "tasks")); // wave-02 folder present, tasks/ empty = JIT stub
        if (withBrief)
        {
            File.WriteAllText(
                Path.Combine(b.PlanDir, Wave2, WaveNode.BriefFileName),
                "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");
        }

        return (b, b.Load().Plan!);
    }

    private static PlanDefinition With(PlanDefinition plan, AutonomyPolicy policy) =>
        plan with { Config = plan.Config with { AutonomyPolicy = policy } };

    /// <summary>Author a VALID single-task wave into <c>&lt;plan&gt;/wave-02-build/tasks/</c> (task.json + action + a guardrail).</summary>
    private static void AuthorValidWave(PromptInvocation inv)
    {
        string taskDir = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-compile");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "compile" }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    /// <summary>Author an INVALID wave: a task with NO guardrails folder (zero guardrails = a validation error).</summary>
    private static void AuthorInvalidWave(PromptInvocation inv)
    {
        string taskDir = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-bad");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "bad — no guardrails" }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        // deliberately NO guardrails/ folder → guardrails validate fails (zero guardrails).
    }

    // --- 1. auto + brief present + VALID wave → BreakdownComplete + review-gate + auto-applied + no marker ----

    [Fact]
    public async Task Auto_BriefPresent_StubAuthorsValidWave_BreakdownComplete_ReviewGate_AutoApplied_TranscriptWritten_NoMarker()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = With(plan, AutonomyPolicy.Auto);

        var stub = new StubBreakdownRunner(AuthorValidWave);
        var invoker = new WaveBreakdownInvoker(stub);
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        RunReport report = await NewScheduler(autoPlan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(autoPlan, Ct);

        // Breakdown was invoked and its output validated → halt for the human review gate.
        Assert.Equal(1, stub.Invocations);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);
        Assert.Equal(Wave2, report.WaveHalt.WaveDir);

        // The halt instructs the human to run /guardrails-review + re-run (never auto-satisfied).
        Assert.Contains("/guardrails-review", report.WaveHalt.Detail);
        Assert.Contains("guardrails run", report.WaveHalt.Detail);

        // wave-01 completed before the checkpoint.
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf(Wave1)!.Status);

        // decisions[] carries a boundary:"wave", decision:"auto-applied" entry for the invocation.
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "auto-applied" && d.Subject == Wave2);

        // The invocation transcript lives under logs/<runId>/<wave-dir>/breakdown/ (SSOT §8).
        string[] composed = Directory.GetFiles(
            Path.Combine(b.PlanDir, "logs"), "composed-prompt.md", SearchOption.AllDirectories);
        Assert.Contains(composed, p => p.Replace('\\', '/').Contains($"/{Wave2}/breakdown/"));

        // The breakdown's prompt spend was charged to the shared overhead sink (folds into the reported total).
        Assert.True(journal.CurrentCostUsd() >= 0.42m);

        // The review gate is NEVER auto-satisfied: the harness wrote NO review marker.
        Assert.False(File.Exists(ReviewMarker.PathFor(b.PlanDir)));
    }

    // --- 2. auto + INVALID wave → BreakdownFailed, quarantined, plan stays loadable, checkpoint re-fires -----

    [Fact]
    public async Task Auto_StubAuthorsInvalidWave_BreakdownFailed_Quarantined_PlanStaysLoadable_CheckpointReFires()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = With(plan, AutonomyPolicy.Auto);

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorInvalidWave));
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        RunReport report = await NewScheduler(autoPlan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(autoPlan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.Equal(Wave2, report.WaveHalt.WaveDir);

        // The partial invalid output was QUARANTINED to logs/<runId>/<wave-dir>/breakdown/rejected/tasks/.
        string[] rejectedDirs = Directory.GetDirectories(
            Path.Combine(b.PlanDir, "logs"), "rejected", SearchOption.AllDirectories);
        Assert.Contains(rejectedDirs, r => r.Replace('\\', '/').Contains($"/{Wave2}/breakdown/rejected"));
        Assert.Contains(rejectedDirs, r => Directory.Exists(Path.Combine(r, "tasks", "01-bad")));

        // The wave reverted to its empty JIT stub on disk.
        string wave2Tasks = Path.Combine(b.PlanDir, Wave2, "tasks");
        Assert.True(Directory.Exists(wave2Tasks));
        Assert.Empty(Directory.GetFileSystemEntries(wave2Tasks));

        // LOAD-BEARING: the plan stays LOADABLE — a fresh load has no errors (the partial did not wedge it).
        PlanLoadResult reload = new PlanLoader().Load(b.PlanDir);
        Assert.False(reload.HasErrors);
        Assert.Empty(reload.Plan!.Waves.Single(w => w.Dir == Wave2).Tasks);

        // The failed-breakdown decision was recorded.
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Subject == Wave2);

        // Resume: wave-01 skips, the JIT checkpoint RE-FIRES at wave-02, and a valid re-author now completes.
        PlanDefinition autoPlan2 = With(b.Load().Plan!, AutonomyPolicy.Auto);
        RunJournal journal2 = RunJournal.LoadOrCreate(autoPlan2);
        var invoker2 = new WaveBreakdownInvoker(new StubBreakdownRunner(AuthorValidWave));
        RunReport report2 = await NewScheduler(autoPlan2, journal2, new RecordingWorktreeProvider(), invoker2)
            .RunAsync(autoPlan2, Ct);

        Assert.NotNull(report2.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report2.WaveHalt!.Kind);
    }

    // --- 3. prompt + non-interactive → honest-halt, never invokes, names the brief ------------------------

    [Fact]
    public async Task PromptPolicy_NonInteractive_NoConfirmation_HonestHalts_NeverInvokes_NamesBrief()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2(); // default config = Prompt
        using WavePlanBuilder _ = b;

        // No confirmations captured (the non-interactive case the CLI produces) → the Scheduler must NOT invoke.
        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker,
            confirmations: null).RunAsync(plan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains($"{Wave2}/{WaveNode.BriefFileName}", report.WaveHalt.Detail);

        // A boundary:"wave", decision:"halted" entry (never invoked).
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "halted" && d.Subject == Wave2);
    }

    // --- 4. halt policy → honest-halt regardless of brief.md, never invokes -------------------------------

    [Fact]
    public async Task HaltPolicy_HonestHalts_RegardlessOfBrief_NeverInvokes()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition haltPlan = With(plan, AutonomyPolicy.Halt);

        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(haltPlan);

        RunReport report = await NewScheduler(haltPlan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(haltPlan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "halted" && d.Subject == Wave2);
    }

    // --- 5. prompt + APPROVED (interactive) → invokes → prompted-approved ---------------------------------

    [Fact]
    public async Task PromptPolicy_Approved_Invokes_BreakdownComplete_PromptedApproved_NoMarker()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2(); // Prompt
        using WavePlanBuilder _ = b;

        var stub = new StubBreakdownRunner(AuthorValidWave);
        var invoker = new WaveBreakdownInvoker(stub);
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var confirmations = new Dictionary<string, bool>(StringComparer.Ordinal) { [Wave2] = true };

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker, confirmations)
            .RunAsync(plan, Ct);

        Assert.Equal(1, stub.Invocations);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "prompted-approved" && d.Subject == Wave2);

        // The review gate is still never auto-satisfied under a prompt approval.
        Assert.False(File.Exists(ReviewMarker.PathFor(b.PlanDir)));
    }

    // --- 6. prompt + DECLINED (interactive) → honest-halt → prompted-declined, never invokes --------------

    [Fact]
    public async Task PromptPolicy_Declined_HonestHalts_PromptedDeclined_NeverInvokes()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var confirmations = new Dictionary<string, bool>(StringComparer.Ordinal) { [Wave2] = false };

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker, confirmations)
            .RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "prompted-declined" && d.Subject == Wave2);
    }

    // --- 7. auto + brief ABSENT → honest-halt, never invokes ----------------------------------------------

    [Fact]
    public async Task Auto_BriefAbsent_HonestHalts_NeverInvokes()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2(withBrief: false);
        using WavePlanBuilder _ = b;
        PlanDefinition autoPlan = With(plan, AutonomyPolicy.Auto);

        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(autoPlan);

        RunReport report = await NewScheduler(autoPlan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(autoPlan, Ct);

        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "halted" && d.Subject == Wave2);
    }
}
