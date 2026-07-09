namespace Guardrails.Core.Execution;

/// <summary>
/// AI-powered merge conflict resolver (plan 08 §9.1). Consumed by the <see cref="Scheduler"/>
/// when <see cref="IWorktreeProvider.Integrate"/> returns <see cref="IntegrationResult.Conflict"/>.
/// Presents the conflicted files to an AI via the three-way env contract and applies the
/// resolution. <see cref="PromptInvocation"/>/<see cref="PromptResult"/> semantics apply:
/// <see cref="Prompts.PromptResult.IsError"/> is NEVER the verdict — the re-verify is.
/// </summary>
public interface IAiMergeWorker
{
    /// <summary>
    /// Attempt to resolve all git conflicts in <paramref name="worktreePath"/> using AI.
    /// Implements a 1-retry budget (2 total attempts) internally.
    /// </summary>
    /// <param name="worktreePath">The integration worktree path where the conflict lives.</param>
    /// <param name="segmentBranch">The segment branch name — used to re-merge on retry after reset.</param>
    /// <param name="planDirectory">The plan directory, passed through to the prompt invocation.</param>
    /// <param name="journal">
    /// The run journal, used to charge each merge-prompt attempt's own spend to the run's cumulative cost via
    /// the shared overhead sink (SSOT §7/§9.1, #314). The charge happens immediately after the runner returns,
    /// BEFORE the deterministic gates read the resolution — so the spend is counted regardless of whether the
    /// attempt passes, fails, or is retried — exactly as the overwatcher's diagnose charge does.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when all conflicted files have been resolved and staged (ready for re-verify).
    /// <c>false</c> when budget exhausted or a deterministic check failed; the worktree is reset
    /// to the pre-merge HEAD before returning.
    /// </returns>
    Task<bool> TryResolveAsync(
        string worktreePath,
        string segmentBranch,
        string planDirectory,
        ISchedulerJournal journal,
        CancellationToken ct);
}
