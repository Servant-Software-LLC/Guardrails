namespace Guardrails.Core.Execution;

/// <summary>
/// The result of building a wave's trial merge (design 39 §1, review round 4
/// "d39-trial-delivery-primitive"): a delivering wave merges the plan branch onto a scratch ref
/// BEFORE the user's branch is touched, so the wave's exit gate can run against the merged tree and
/// the fast-forward that follows lands only a tree the gate has already seen.
/// <para>
/// A fast-forward creates no commit and runs no git hook, so a promotion alone can never reach the
/// user's <c>pre-commit</c>/<c>commit-msg</c> hooks (issue #149). When a merge commit is needed this
/// primitive builds it NOW, in a harness-owned worktree, WITHOUT <c>--no-verify</c> — the only point
/// in a waved delivery where the user's hooks can still run and reject the work.
/// </para>
/// <para>
/// <see cref="IWorktreeProvider.CreateTrialDelivery"/> produces this record; the data itself is
/// complete and requires no further interpretation by a caller beyond the invariants documented on
/// each member below. <see cref="IWorktreeProvider.PromoteTrialDelivery"/> and
/// <see cref="IWorktreeProvider.DiscardTrialDelivery"/> consume it.
/// </para>
/// </summary>
public sealed record TrialDelivery
{
    /// <summary>The wave directory this trial was built for (the key under which its ref/worktree are scoped).</summary>
    public required string WaveDir { get; init; }

    /// <summary>The ref this trial's commit is reachable from: <c>refs/guardrails/trial/&lt;WaveDir&gt;</c>.</summary>
    public required string TrialRef { get; init; }

    /// <summary>
    /// What <see cref="TrialRef"/> points at. Null exactly when <see cref="Refusal"/> is set — no
    /// trial ref was left behind for a refused build.
    /// </summary>
    public string? Commit { get; init; }

    /// <summary>The user's branch tip (<c>IntegrationHandle.OriginalBranch</c>) this trial was built from.</summary>
    public required string UserTip { get; init; }

    /// <summary>
    /// True when <see cref="UserTip"/> is the plan tip or a strict ancestor of it — the quiet case:
    /// <see cref="TrialRef"/> points straight at the plan tip, no merge commit was created, and no
    /// hook ran. False when a merge commit was built (or the work was already delivered by a prior
    /// merge commit — see <see cref="AlreadyDelivered"/>).
    /// </summary>
    public required bool UserTipWasAncestor { get; init; }

    /// <summary>
    /// True when the user's branch already contains the plan tip — either <see cref="UserTip"/>
    /// equals the plan tip (a resume right after a quiet-case promotion landed), or the plan tip is a
    /// strict ancestor of <see cref="UserTip"/> (a resume after a merge-commit promotion landed, or
    /// the user merged the plan branch themselves). No merge commit, no hook, and no worktree were
    /// created; <see cref="Commit"/> is the commit already on the user's branch.
    /// </summary>
    public bool AlreadyDelivered { get; init; }

    /// <summary>
    /// The harness-owned worktree checked out at <see cref="Commit"/>, so the Scheduler can run the
    /// wave's exit gate there. Set only when a merge commit was built; null in the quiet case, the
    /// already-delivered case, and on a <see cref="Refusal"/>.
    /// </summary>
    public string? WorktreePath { get; init; }

    /// <summary>
    /// <see cref="MergeOnSuccessResult.Conflict"/> or <see cref="MergeOnSuccessResult.HookRejected"/>
    /// when no trial could be built; null otherwise. A refusal leaves no trial ref and no trial
    /// worktree behind, and <see cref="Commit"/> is null.
    /// </summary>
    public MergeOnSuccessResult? Refusal { get; init; }

    /// <summary>
    /// The hook's output for <see cref="MergeOnSuccessResult.HookRejected"/>, or the newline-separated,
    /// ordinal-sorted conflicting paths for <see cref="MergeOnSuccessResult.Conflict"/>. Null when
    /// <see cref="Refusal"/> is null.
    /// </summary>
    public string? RefusalDetail { get; init; }
}
