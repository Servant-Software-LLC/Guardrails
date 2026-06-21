namespace Guardrails.Core.Execution;

/// <summary>
/// The outcome of <see cref="IWorktreeProvider.MergePlanBranchIntoUserBranch"/> — how the
/// completed plan branch was merged back into the user's original branch (plan 08 SSOT §5.3).
/// </summary>
public enum MergeOnSuccessResult
{
    /// <summary>The user's branch was fast-forwarded to the plan branch tip.</summary>
    FastForwarded,

    /// <summary>A merge commit was created to combine the plan branch into the user's branch.</summary>
    Merged,

    /// <summary>The merge-on-success step encountered a conflict; the user's branch was not modified.</summary>
    Conflict,

    /// <summary>
    /// The user's working tree was dirty (uncommitted changes) at merge-back time, so the harness
    /// refused to run git over uncommitted user work and halted to needs-human (plan 08 §5.3 / F4).
    /// The user's branch was not modified.
    /// </summary>
    DirtyWorkingTree
}
