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
    /// The user's working tree had uncommitted changes to TRACKED files THIS MERGE WOULD UPDATE at
    /// merge-back time, so the harness refused to run git over uncommitted user work and halted to
    /// needs-human (plan 08 §5.3 / F4). The user's branch was not modified.
    /// <para>
    /// <b>Narrowed in issue #448:</b> dirt DISJOINT from the merge's path set no longer refuses —
    /// git itself merges that case cleanly and preserves the user's WIP, and the old any-dirt-anywhere
    /// rule let a run's own generated artifacts (regenerated per-wave diagrams) refuse a wholly-green
    /// run's delivery. The blocking paths are exposed via
    /// <see cref="IWorktreeProvider.LastMergeOnSuccessDetail"/> → <see cref="RunReport.MergeOnSuccessDetail"/>
    /// so the CLI can name them. The gate FAILS CLOSED: if the merge's path set cannot be computed,
    /// any tracked dirt still refuses.
    /// </para>
    /// </summary>
    DirtyWorkingTree,

    /// <summary>
    /// The user-facing merge commit was REJECTED by one of the user's git hooks (e.g. GitGuardian's
    /// <c>pre-commit</c> found a secret, or — as in issues #149/#150 — the hook ran offline and
    /// failed). The harness ran <c>git merge --abort</c> so the user's branch is left CLEAN at its
    /// original HEAD, and the verified plan branch is left intact for a manual merge. This is a
    /// graceful halt, NOT a failure of the work: every task passed and is durable on the plan branch.
    /// The hook's stderr is surfaced to the user via <see cref="RunReport.MergeOnSuccessDetail"/>.
    /// </summary>
    HookRejected,

    /// <summary>
    /// The user's checkout is no longer on the branch the run started on (issue #588), so NOTHING was
    /// merged. The delivery TARGET is pinned at run start (<see cref="IntegrationHandle.OriginalBranch"/>,
    /// read once by <c>CreateIntegration</c>) but the merge itself is a bare <c>git merge</c> that lands on
    /// whatever <c>HEAD</c> currently is — so a branch created or checked out WHILE the run was in flight
    /// silently redirected the delivery, and the report still named the pinned branch. Detached HEAD
    /// (<c>rev-parse --abbrev-ref HEAD</c> → the literal <c>HEAD</c>) and an unreadable HEAD take this same
    /// path: neither is provably the original branch, and the gate FAILS CLOSED.
    /// <para>
    /// <b>Compare and refuse — never check out.</b> The harness does not restore the user's branch: that
    /// would mutate a working tree the user moved deliberately, a worse failure than declining. Both branch
    /// names travel to the CLI via <see cref="IWorktreeProvider.LastMergeOnSuccessDetail"/> →
    /// <see cref="RunReport.MergeOnSuccessDetail"/>, exactly as <see cref="HookRejected"/> and
    /// <see cref="DirtyWorkingTree"/> do. The user's checkout is untouched and the verified work is left on
    /// the plan branch for a manual merge — the SAFE failure direction.
    /// </para>
    /// </summary>
    BranchMoved
}
