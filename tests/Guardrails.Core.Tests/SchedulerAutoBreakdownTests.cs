using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.Review;

namespace Guardrails.Core.Tests;

/// <summary>
/// The <c>autoBreakdown</c> between-wave breakdown-invocation knob (SSOT §14.4/§14.10, #360) — the NEW DEFAULT
/// (<c>true</c>) that auto-fires <c>plan-breakdown</c> at the JIT wave checkpoint with NO prompt, <b>decoupled
/// from <c>autonomyPolicy</c></b>. Companion to <see cref="SchedulerWaveBreakdownTests"/> (which covers the
/// LEGACY <c>autoBreakdown:false</c> = #368 <c>autonomyPolicy</c>-gated path). Same tokenless machinery: a real
/// on-disk waved plan (wave-01 authored + wave-02 an empty JIT stub with a <c>brief.md</c>), a real
/// <see cref="RunJournal"/>, a <see cref="RecordingWorktreeProvider"/> (no git), a fake executor, and a STUB
/// <see cref="IPromptRunner"/> that simulates the breakdown sub-process — NO real Claude call is ever made.
/// </summary>
public sealed class SchedulerAutoBreakdownTests
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

    /// <summary>A STUB breakdown runner: runs <paramref name="author"/> and returns a canned success (no Claude).</summary>
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

    /// <summary>A plan with wave-01 authored + wave-02 an empty JIT stub, optionally carrying a brief.md.</summary>
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

    private static PlanDefinition With(PlanDefinition plan, AutonomyPolicy policy, bool autoBreakdown) =>
        plan with { Config = plan.Config with { AutonomyPolicy = policy, AutoBreakdown = autoBreakdown } };

    /// <summary>Author a VALID single-task wave into <c>&lt;plan&gt;/wave-02-build/tasks/</c>.</summary>
    private static void AuthorValidWave(PromptInvocation inv)
    {
        string taskDir = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-compile");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "compile" }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    // --- 1. DEFAULT (autoBreakdown:true) + brief present + NON-INTERACTIVE → AUTO-INVOKES, no prompt --------

    [Fact]
    public async Task DefaultAutoBreakdown_BriefPresent_NonInteractive_AutoInvokes_BreakdownComplete_NoReviewMarker()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;

        // The field is OMITTED from guardrails.json → the loader default (true) is in force. Assert it, so the
        // test proves the DEFAULT (not an explicit set) is what auto-fires.
        Assert.True(plan.Config.AutoBreakdown);

        var stub = new StubBreakdownRunner(AuthorValidWave);
        var invoker = new WaveBreakdownInvoker(stub);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        // confirmations: null models the NON-INTERACTIVE case (the CLI captured no y/N). Under the DEFAULT the
        // Scheduler must auto-invoke anyway — no prompt, no confirmation needed.
        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker, confirmations: null)
            .RunAsync(plan, Ct);

        Assert.Equal(1, stub.Invocations);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);
        Assert.Equal(Wave2, report.WaveHalt.WaveDir);

        // decisions[] records the auto-invocation with an auto-applied token at the wave boundary.
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "auto-applied" && d.Subject == Wave2);

        // The review gate ALWAYS halts (autoBreakdown governs invocation only): the halt names /guardrails-review
        // and the harness wrote NO review marker.
        Assert.Contains("/guardrails-review", report.WaveHalt.Detail);
        Assert.False(File.Exists(ReviewMarker.PathFor(b.PlanDir)));
    }

    // --- 2. autoBreakdown:true + brief ABSENT → honest-halt, never invokes --------------------------------

    [Fact]
    public async Task AutoBreakdown_BriefAbsent_HonestHalts_NeverInvokes()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2(withBrief: false);
        using WavePlanBuilder _ = b;
        Assert.True(plan.Config.AutoBreakdown); // default on

        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker, confirmations: null)
            .RunAsync(plan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "halted" && d.Subject == Wave2);
    }

    // --- 3. autoBreakdown:false + brief present + prompt + NON-INTERACTIVE → honest-halt (#368 preserved) --

    [Fact]
    public async Task AutoBreakdownFalse_PromptPolicy_NonInteractive_HonestHalts_NeverInvokes()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;
        PlanDefinition legacy = With(plan, AutonomyPolicy.Prompt, autoBreakdown: false);

        // No confirmations captured (the non-interactive case) → the legacy path honest-halts, never invokes.
        var invoker = new WaveBreakdownInvoker(new NeverInvokedRunner());
        RunJournal journal = RunJournal.LoadOrCreate(legacy);

        RunReport report = await NewScheduler(legacy, journal, new RecordingWorktreeProvider(), invoker, confirmations: null)
            .RunAsync(legacy, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "halted" && d.Subject == Wave2);
    }

    // --- 4. DECOUPLING: autoBreakdown:true auto-invokes under EVERY autonomyPolicy (incl. halt) ------------

    [Theory]
    [InlineData(AutonomyPolicy.Prompt)]
    [InlineData(AutonomyPolicy.Halt)]
    [InlineData(AutonomyPolicy.Auto)]
    public async Task AutoBreakdown_IsDecoupledFromAutonomyPolicy_InvokesUnderEveryPolicy_NonInteractive(
        AutonomyPolicy policy)
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithStubWave2();
        using WavePlanBuilder _ = b;

        // autoBreakdown:true + this policy. Under the LEGACY path, `halt` (and `prompt` non-interactive) would
        // honest-halt; proving the breakdown fires under `halt` too is the load-bearing decoupling assertion.
        PlanDefinition p = With(plan, policy, autoBreakdown: true);

        var stub = new StubBreakdownRunner(AuthorValidWave);
        var invoker = new WaveBreakdownInvoker(stub);
        RunJournal journal = RunJournal.LoadOrCreate(p);

        RunReport report = await NewScheduler(p, journal, new RecordingWorktreeProvider(), invoker, confirmations: null)
            .RunAsync(p, Ct);

        Assert.Equal(1, stub.Invocations);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Decision == "auto-applied" && d.Subject == Wave2);

        // The decision still records the run-time policy in force (unchanged by autoBreakdown) — proving the two
        // knobs are independent: the policy token is faithfully reported, yet the breakdown fired regardless.
        Assert.Contains(journal.Document.Decisions ?? [],
            d => d.Boundary == "wave" && d.Policy == AutonomyPolicies.Token(policy) && d.Subject == Wave2);
    }
}
