using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Three operator-facing messages that asserted a fact nothing had checked — #598, #497 and #471's
/// residual. They are one defect wearing three costumes, and the shape is worth naming because this
/// repository keeps paying for it: <b>a message that is true in the common case, false in one case, and
/// identical in both.</b>
///
/// <para>
/// What makes the class expensive is that the reader has no way to tell the two apart. Each of these is
/// the ONLY thing its audience reads about what just happened — the retry prompt is all the next attempt
/// gets, the halt detail is all the operator gets — so a false clause does not merely fail to inform, it
/// actively aims the reader at a problem that is not there.
/// </para>
/// </summary>
public sealed class HaltTextAssertsOnlyWhatItCheckedTests
{
    // ---- #598: the rollback disclosure blamed a rejection that never happened ----------------------

    /// <summary>
    /// Measured on plan 35, task <c>10-author-tests-run-finished-exit-paths</c> attempt 1: outcome
    /// <c>MaxTurns</c>, attempt directory holding <c>state-in.json</c> and NO <c>state-out.json</c>. There
    /// was no fragment, so nothing was rejected — and the feedback said one had been, a few lines after
    /// correctly diagnosing the turn cap. The cost is turns, charged to an attempt that has just proved it
    /// did not have enough, spent chasing the state-fragment key (#164) — a real failure mode with a
    /// specific remedy, none of which applies.
    /// </summary>
    [Theory]
    [InlineData("max-turns")]
    [InlineData("timeout")]
    public void ABudgetStop_DoesNotBlameAStateFragmentRejection(string stop)
    {
        string feedback = stop == "max-turns"
            ? RetryPolicy.ForMaxTurnsExceeded(
                PlanFixtures.PromptTask("10-author-tests"), attempt: 2, fileWritesRolledBack: true)
            : RetryPolicy.ForTimeout(
                PlanFixtures.PromptTask("10-author-tests"), attempt: 2, fileWritesRolledBack: true);

        // The consequence is unchanged and must stay: the tree WAS reset, and an agent that assumes
        // otherwise re-authors nothing and fails the same way again.
        Assert.Contains("## File writes were also rolled back", feedback, StringComparison.Ordinal);
        Assert.Contains("re-author ALL files from scratch", feedback, StringComparison.Ordinal);

        // The CAUSE is the part that was false.
        Assert.DoesNotContain("Because the state fragment was rejected", feedback, StringComparison.Ordinal);
        Assert.Contains("Because this attempt did not settle", feedback, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, without which the fix could have been "delete the clause": on a path where a fragment
    /// really WAS rejected, the wording that names the rejection is the correct and useful one, and it
    /// stays.
    /// </summary>
    [Fact]
    public void AnActualFragmentRejection_StillNamesTheRejection()
    {
        string feedback = RetryPolicy.ForForeignKey(
            PlanFixtures.PromptTask("04-author-tests"), attempt: 2, ["some-other-task"], fileWritesRolledBack: true);

        Assert.Contains("Because the state fragment was rejected", feedback, StringComparison.Ordinal);
    }

    // ---- #497: the wave-1 checkpoint claimed an upstream that does not exist -----------------------

    /// <summary>
    /// Measured verbatim on a wave-1 checkpoint:
    /// <code>
    /// WAVE CHECKPOINT: Wave 'wave-01-alpha' has no authored tasks - halting for JIT breakdown (SSOT 14.4).
    ///   The prior wave(s) completed and are materialized on the plan branch. Break down + review
    ///   'wave-01-alpha' against the materialized upstream artifacts
    /// </code>
    /// Both clauses are false on the FIRST wave: no prior wave completed, and nothing is materialized. The
    /// headline above it was accurate; only the guidance beneath it was wrong.
    ///
    /// <para>
    /// It is not cosmetic, because the whole premise of the JIT flow is that a wave is authored AGAINST the
    /// materialized upstream. An agent told to do that with nothing materialized has been handed a premise
    /// it cannot satisfy and no signal that the premise is wrong — it goes looking, finds nothing, and
    /// cannot tell "I looked in the wrong place" from "there is nothing to find".
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheFirstWavesCheckpoint_DoesNotClaimAMaterializedUpstream()
    {
        using var b = new WavePlanBuilder();
        b.WaveStub("wave-01-alpha");
        b.Task("wave-02-beta", "01-later");   // a later authored wave, so GR1009 does not fire (#496)

        PlanDefinition plan = b.Load().Plan!;
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.DoesNotContain("prior wave(s) completed", report.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "against the materialized upstream artifacts", report.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.Contains("FIRST wave", report.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing is materialized yet", report.WaveHalt.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control that keeps the fix from becoming "never mention the upstream": at a LATER wave's
    /// checkpoint the original wording is true and useful, and it must survive. Without this, branching on
    /// the index could be reduced to deleting the clause, which loses the one piece of guidance a JIT
    /// breakdown actually needs.
    /// </summary>
    [Fact]
    public async Task ALaterWavesCheckpoint_StillNamesTheMaterializedUpstream()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-alpha", "01-first");
        b.WaveStub("wave-02-beta");
        b.Task("wave-03-gamma", "01-later");  // keeps the stub from being the LAST wave (GR1009)

        PlanDefinition plan = b.Load().Plan!;
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Contains("prior wave(s) completed", report.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST wave", report.WaveHalt.Detail, StringComparison.Ordinal);
    }

    private static Scheduler NewScheduler(PlanDefinition plan, RunJournal journal) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: IRunObserver.Null,
            maxParallelism: 4, reVerifier: null, breakdownInvoker: null, breakdownConfirmations: null);

    /// <summary>An executor that succeeds without doing anything — the halt arrives before any task runs.</summary>
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

    // ---- #471 residual: byte-identity claimed without being checked --------------------------------

    /// <summary>
    /// A precise revert that lost nothing is the case the claim was written for, and it keeps it.
    /// </summary>
    [Fact]
    public void APreciseRevertWithNoFailures_ClaimsByteIdentity()
    {
        var revert = new RevertSummary
        {
            MovedPaths = ["tasks/01-a/task.json"],
            KeptPaths = ["guardrails/00-hand-authored.sh"]
        };

        Assert.True(revert.Precise);
        Assert.True(revert.RestoredToPreBreakdownState);
    }

    /// <summary>
    /// The degraded fallback moves the whole <c>tasks/</c> and leaves any <c>guardrails/</c> or
    /// <c>preflights/</c> the attempt wrote where they are — <b>verbatim the defect #471 was opened
    /// about</b>, measured as eight files staying behind. Reporting that as byte-identical re-tells the
    /// original lie in the replacement text.
    /// </summary>
    [Fact]
    public void ACoarseRevert_DoesNotClaimByteIdentity()
    {
        var revert = new RevertSummary { MovedPaths = ["tasks/"], Precise = false };

        Assert.False(revert.RestoredToPreBreakdownState);
    }

    /// <summary>
    /// And a move or restore that FAILED on the precise path. Every one of these used to be swallowed by a
    /// bare <c>continue</c>: the file stayed in the wave folder, appeared in no list, and the halt closed
    /// with the byte-identity claim anyway.
    /// </summary>
    [Fact]
    public void APreciseRevertThatCouldNotMoveAFile_DoesNotClaimByteIdentity()
    {
        var revert = new RevertSummary
        {
            MovedPaths = ["tasks/01-a/task.json"],
            FailedPaths = ["tasks/02-b/action.sh"]
        };

        Assert.True(revert.Precise);
        Assert.False(revert.RestoredToPreBreakdownState);
    }

    /// <summary>
    /// Why the claim is load-bearing rather than decorative, stated as a test so it cannot be optimised
    /// away: <c>PlanDefinitionHash</c> covers guardrail and preflight bodies (#260). If the folder is not
    /// byte-identical the hash MOVED, the plan's <c>/guardrails-review</c> attestation staled, and the next
    /// <c>validate</c> raises GR2025 over work the breakdown never touched. An operator told the hash is
    /// unchanged has been told not to expect that warning — and a staleness warning nobody expects is one
    /// that gets waved through, which is how a REAL staleness warning gets lost later.
    /// </summary>
    [Fact]
    public void TheDefaultSummary_IsPreciseAndClean_SoTheFlagsAreOptInFailures()
    {
        var revert = new RevertSummary();

        Assert.True(revert.Precise);
        Assert.Empty(revert.FailedPaths);
        Assert.True(revert.RestoredToPreBreakdownState);
    }
}
