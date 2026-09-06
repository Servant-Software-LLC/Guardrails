using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #502 — the waved fixtures were all serial, so every rule gated on WORKTREE mode was invisible at
/// the scheduler level.
///
/// <para>
/// <b>The gap, exactly.</b> <see cref="WavePlanBuilder"/> defaulted to <c>maxParallelism: 1</c> so that
/// validation would not need a git workspace. Reasonable in isolation; its consequence was not. The
/// harness has exactly two validation rules that exist only for worktree mode, and both open with the
/// same line:
/// </para>
/// <list type="bullet">
///   <item><b>GR2015</b> (<c>WorkspaceNotGitRoot</c>) — parallel tasks need per-segment worktrees, which
///     need a git repository.</item>
///   <item><b>GR2028</b> (<c>PlanGuardrailsMissingIntegrationReRun</c>) — a parallel-topology wave's exit
///     gate must carry a real integration re-run, because that gate is a union soundness boundary. A
///     serial run merges no parallel branches, so it has no union to certify.</item>
/// </list>
/// <para>
/// A fixture that cannot enter the mode cannot emit either. That is not hypothetical: <b>#501 shipped
/// under fifteen passing <c>SchedulerBreakdownDurabilityTests</c></b>. GR2028 vetoed a truncated JIT
/// prefix that the <c>breakdown-intent.json</c> manifest existed to preserve, the authored work was
/// reverted wholesale, and every one of those tests passed before and after the fix — because their
/// fixture runs serial and therefore cannot produce a GR2028. The bug was found by a real
/// <c>guardrails run</c>, and its behavioural reproduction is what this file is.
/// </para>
///
/// <para>
/// <b>The audit #502 asked for, and its result.</b> <c>MaxParallelism</c> is read in exactly two places in
/// <c>PlanValidator</c>, and they are the two rules above. GR2028 is the one that bit; it was not alone
/// only in the sense that GR2015 shares the same blindness. Both are pinned here in both directions, so
/// the fixture is PROVEN to enter worktree mode rather than assumed to.
/// </para>
/// </summary>
public sealed class WorktreeModeWavedValidationTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 1. the fixture can now enter worktree mode at all -----------------------------------------

    /// <summary>
    /// The headline. The SAME plan folder — a wave with two leaf tasks and no exit gate — is silent at
    /// <c>maxParallelism: 1</c> and emits GR2028 at <c>2</c>. Both halves matter: the first is what every
    /// waved fixture in this suite was measuring, the second is the mode the rule exists for.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void AParallelTopologyWaveWithNoExitGate_EmitsGr2028_OnlyInWorktreeMode(
        int maxParallelism, bool expectGr2028)
    {
        using var b = new WavePlanBuilder(maxParallelism, gitBacked: true);
        b.Task(Wave1, "01-config");
        b.Task(Wave2, "01-compile");
        b.Task(Wave2, "02-package");   // a SECOND leaf -> parallel topology, and no wave-02 guardrails/

        PlanLoadResult load = b.Load();
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics.Select(d => d.ToString())));

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan!);

        Assert.Equal(
            expectGr2028,
            diagnostics.Any(d => d.Code == DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun));
    }

    /// <summary>
    /// The other worktree-mode-only rule, pinned the same way. A non-git temp workspace is fine serially
    /// and is a GR2015 error in worktree mode — which is precisely why the serial default was chosen, and
    /// precisely what made GR2028 unreachable.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ANonGitWorkspace_EmitsGr2015_OnlyInWorktreeMode(int maxParallelism, bool expectGr2015)
    {
        using var b = new WavePlanBuilder(maxParallelism);   // NOT git-backed: workspace is the temp root
        b.Task(Wave1, "01-config");

        PlanLoadResult load = b.Load();
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan!);

        Assert.Equal(expectGr2015, diagnostics.Any(d => d.Code == DiagnosticCodes.WorkspaceNotGitRoot));
    }

    /// <summary>
    /// The fixture's own claim, checked rather than trusted: git-backing SATISFIES GR2015 instead of
    /// dodging it. Without this, a fixture that silently failed to <c>git init</c> would read as "the rule
    /// does not fire in this shape" and quietly restore the blindness this file exists to remove.
    /// </summary>
    [Fact]
    public void TheGitBackedFixture_SatisfiesGr2015_RatherThanAvoidingIt()
    {
        using var b = new WavePlanBuilder(maxParallelism: 2, gitBacked: true);
        b.Task(Wave1, "01-config");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(b.Load().Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.WorkspaceNotGitRoot);
    }

    // ---- 2. the #501 reproduction that could not be written before ---------------------------------

    /// <summary>
    /// <b>The behavioural #501 reproduction.</b> A JIT breakdown declares three folders, is cut off after
    /// authoring two, and — because a breakdown authors task folders first and the wave's exit gate last —
    /// leaves a parallel-topology wave with no <c>guardrails/</c> at all. In worktree mode that is a
    /// GR2028 ERROR, and the post-breakdown gate used to let it cast a veto: <c>valid</c> went false and
    /// the authored prefix was reverted wholesale, while GR2063 printed "the valid prefix is preserved"
    /// in the same halt.
    ///
    /// <para>
    /// The error is a restatement of <i>the wave is unfinished</i>, which the manifest had already told
    /// the harness. So it is excused, and the two authored folders SURVIVE. Reverted JIT authoring is the
    /// most expensive thing this harness can throw away.
    /// </para>
    ///
    /// <para>
    /// <b>The assertion is on the surviving folders and on the gate's own record</b>, not on an internal
    /// flag: <c>gate-decision.txt</c> is where an operator reads which errors decided the verdict, and a
    /// suppression that fired has to be distinguishable there from one that never ran.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TruncatedPrefix_TrippingGr2028_IsExcused_AndSurvives()
    {
        (WavePlanBuilder b, PlanDefinition plan) = TruncatablePlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;   // the resume segment makes no progress -> the no-progress rule stops the loop
            }

            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
            // ...and no wave-02 guardrails/ exit gate, because the session never got that far.
        }, PromptFailureKind.Timeout);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, journal, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        // The prefix is on disk. This is the assertion that was red before #501's fix and that no test in
        // this repo could make.
        string tasks = Path.Combine(b.PlanDir, Wave2, "tasks");
        Assert.True(Directory.Exists(Path.Combine(tasks, "01-compile")), "01-compile was reverted");
        Assert.True(Directory.Exists(Path.Combine(tasks, "02-package")), "02-package was reverted");

        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);

        // The gate's own record: GR2028 was seen, named, and EXCUSED — not silently dropped. A fix that
        // made the error disappear would satisfy the folder assertions above and hide a real finding from
        // the only person who can act on it.
        string decision = ReadGateDecision(b.PlanDir);
        Assert.Contains("excused (#501)", decision);
        Assert.Contains(DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, decision);
        Assert.Contains("KNOWINGLY INCOMPLETE", decision);
    }

    /// <summary>
    /// The boundary control, and the reason the excuse is a suppression rather than a repeal. A session
    /// that satisfied its OWN manifest is not unfinished — it authored every folder it declared and simply
    /// never wrote the wave's exit gate. GR2028 is then a real finding about a finished wave, it blocks,
    /// and the gate rejects. Without this, "excused" could quietly become "GR2028 no longer applies to
    /// waves", which is the over-correction that turns the post-breakdown gate into a rubber stamp.
    /// </summary>
    [Fact]
    public async Task CompletePrefix_TrippingGr2028_IsStillBlocked()
    {
        (WavePlanBuilder b, PlanDefinition plan) = TruncatablePlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;
            }

            // Declares TWO and authors TWO: the manifest owes nothing, so the prefix is not incomplete.
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
        });

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, journal, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.NotEqual(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);

        string decision = ReadGateDecision(b.PlanDir);
        Assert.Contains("gate verdict : REJECT", decision);
        Assert.Contains("blocking     : " + DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, decision);
        Assert.DoesNotContain("excused (#501)", decision);
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    /// <summary>
    /// A git-backed waved plan at <c>maxParallelism: 2</c>: wave-01 authored (one task, so no GR2028 of
    /// its own), wave-02 a JIT stub with a brief, <c>autonomyPolicy auto</c> so the checkpoint fires.
    /// Whatever wave-02's breakdown authors has a parallel topology and no exit gate — the shape the real
    /// August truncation had, and the one a serial fixture cannot express.
    /// </summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) TruncatablePlan()
    {
        var b = new WavePlanBuilder(maxParallelism: 2, gitBacked: true);
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n- package\n- publish\n");

        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    private static Scheduler NewScheduler(PlanDefinition plan, RunJournal journal, WaveBreakdownInvoker invoker) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null);

    /// <summary>The gate's teed reasoning — one per breakdown session, under the run's log directory.</summary>
    private static string ReadGateDecision(string planDir)
    {
        string[] found = Directory.GetFiles(planDir, "gate-decision.txt", SearchOption.AllDirectories);
        Assert.True(found.Length > 0, "the post-breakdown gate wrote no gate-decision.txt");
        return File.ReadAllText(found[^1]);
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
        File.WriteAllText(Path.Combine(stateDir, BreakdownIntent.FileName),
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

    /// <summary>An executor that succeeds without doing anything — no task in these plans ever runs.</summary>
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

    /// <summary>Simulates the plan-breakdown sub-process; no real Claude call is ever made.</summary>
    private sealed class StubBreakdownRunner(
        Action<PromptInvocation, int> author,
        PromptFailureKind failureKind = PromptFailureKind.None) : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            author(invocation, Invocations);
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
}
