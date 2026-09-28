namespace Guardrails.Core.Execution;

/// <summary>
/// The outcome of <see cref="IWorktreeProvider.Integrate"/> — how the segment was merged
/// into the integration branch (plan 08 SSOT §5.3).
/// </summary>
public enum IntegrationResult
{
    /// <summary>The plan branch was fast-forwarded to the segment tip (no merge commit needed).</summary>
    FastForward,

    /// <summary>A merge commit was created to combine the segment with the integration branch.</summary>
    Merged,

    /// <summary>
    /// The non-FF merge produced git conflicts; the integration worktree has conflict markers
    /// and MERGE_HEAD set. The AI merge worker (if wired) will attempt to resolve them.
    /// </summary>
    Conflict
}

/// <summary>
/// The non-fast-forward merge of a segment failed WITHOUT a conflict, twice (#750): git refused it, left no
/// <c>MERGE_HEAD</c>, and refused it again after the integration worktree was reset to its pre-merge head. The
/// provider has left the integration worktree reset to that head. The Scheduler settles the task
/// <c>needs-human</c> with <see cref="Exception.Message"/> (git's exit codes and output) instead of aborting the
/// run. An <see cref="InvalidOperationException"/>, so a caller that does not know this type still sees a fault.
/// </summary>
public sealed class IntegrationMergeFailedException(string message) : InvalidOperationException(message);
