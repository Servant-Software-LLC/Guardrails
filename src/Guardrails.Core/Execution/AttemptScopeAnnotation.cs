namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #816: the write-scope facts of ONE attempt, filled in as the attempt learns them and read by
/// <see cref="AttemptJournaler"/> when it builds that attempt's record, so <c>run.json</c>'s
/// <c>attempts[].scopeRevertedPaths</c> / <c>writeScopeNotChecked</c> and the <c>AttemptFinished</c> event carry
/// them. Every writer sets these BEFORE the journaler is called.
/// </summary>
internal sealed class AttemptScopeAnnotation
{
    /// <summary>Why the scope was NOT fully checked or enforced this attempt, or null when it was.</summary>
    public string? NotChecked { get; set; }

    /// <summary>The paths the end-of-attempt check reverted, or null when it reverted nothing.</summary>
    public IReadOnlyList<string>? RevertedPaths { get; set; }

    /// <summary>
    /// #816 fifth review: links (workspace-relative directories) a worktree-mode revert REMOVED because an offending
    /// path lay under them. Named in the attempt summary and log; never populated in serial mode, which refuses.
    /// </summary>
    public List<string> RemovedLinks { get; } = [];

    /// <summary>
    /// #827: untracked nested git repositories with no commit that every harness <c>git add</c> this attempt LEFT OUT
    /// (git cannot stage one — it fails the whole add). Named in the attempt summary and <c>write-scope-check.log</c>,
    /// and invisible to the write-scope check, so they are neither offenses nor part of the plan's changes.
    /// </summary>
    public List<string> NestedReposSkipped { get; } = [];

    /// <summary>Fold in the repositories one staging pass skipped (deduplicated, order kept).</summary>
    public void AddNestedReposSkipped(IEnumerable<string> repos)
    {
        foreach (string repo in repos)
        {
            if (!NestedReposSkipped.Contains(repo, StringComparer.Ordinal))
            {
                NestedReposSkipped.Add(repo);
            }
        }
    }
}
