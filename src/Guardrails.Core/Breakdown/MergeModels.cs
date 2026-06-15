namespace Guardrails.Core.Breakdown;

/// <summary>
/// The resolution for one guardrail file in a regeneration merge (SSOT §11.3). The merge
/// preserves human guardrail edits while re-deriving everything else from the plan; each
/// guardrail lands in exactly one of these buckets.
/// </summary>
public enum GuardrailMergeAction
{
    /// <summary>The machine version wins — the human never touched it (or both converged).</summary>
    TakeRemote,

    /// <summary>The human version is preserved — an edit the regeneration didn't also change, or a human-added guardrail.</summary>
    KeepLocal,

    /// <summary>Removed — the regeneration no longer emits it and the human hadn't edited it.</summary>
    Drop,

    /// <summary>Both the human and the regeneration changed it (or removed-vs-edited): a human must apply or discard. Blocks the run.</summary>
    Conflict
}

/// <summary>
/// One guardrail's place in the merge: which task owns it (by <c>stableId</c>), the guardrail
/// filename, the resolved <see cref="GuardrailMergeAction"/> and a human-readable reason, plus
/// the source/target relative paths the apply step needs.
/// </summary>
public sealed record GuardrailMergeItem
{
    /// <summary>The owning task's identity — its <c>stableId</c>, or <c>folder:&lt;name&gt;</c> when it declares none.</summary>
    public required string TaskIdentity { get; init; }

    /// <summary>The guardrail filename within <c>guardrails/</c> (e.g. <c>02-tests-pass.ps1</c>).</summary>
    public required string GuardrailFile { get; init; }

    /// <summary>How this guardrail resolves.</summary>
    public required GuardrailMergeAction Action { get; init; }

    /// <summary>One-line explanation, surfaced in the report.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Plan-relative path of the LOCAL file to preserve (forward-slash), set only for
    /// <see cref="GuardrailMergeAction.KeepLocal"/> — the apply step reads its bytes from here.
    /// </summary>
    public string? LocalRelPath { get; init; }

    /// <summary>
    /// Plan-relative path the file occupies in the merged result (forward-slash), using the
    /// REMOTE task's folder name. Null for <see cref="GuardrailMergeAction.Drop"/> (absent from
    /// the result). For <see cref="GuardrailMergeAction.KeepLocal"/> the apply step writes the
    /// preserved bytes here.
    /// </summary>
    public string? ResultRelPath { get; init; }
}

/// <summary>
/// The full outcome of a regeneration merge (SSOT §11.3): every guardrail's resolution plus
/// human-visible warnings (e.g. a human-edited guardrail dropped because its task was removed
/// from the plan). The merge is keyed on task <c>stableId</c>, so a renumbered/reordered task
/// still carries its human guardrails forward.
/// </summary>
public sealed record MergePlan
{
    /// <summary>Every guardrail's resolution, ordinal-ordered by result path then local path.</summary>
    public required IReadOnlyList<GuardrailMergeItem> Items { get; init; }

    /// <summary>
    /// Non-blocking notices the human should see — chiefly human-authored/edited guardrails
    /// dropped because the plan removed their task. The plan is the source of truth, so these
    /// proceed, but never silently.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Items the human must resolve before the merge can be applied.</summary>
    public IEnumerable<GuardrailMergeItem> Conflicts =>
        Items.Where(i => i.Action == GuardrailMergeAction.Conflict);

    /// <summary>True when at least one guardrail is a <see cref="GuardrailMergeAction.Conflict"/> — the merge must block.</summary>
    public bool HasConflicts => Items.Any(i => i.Action == GuardrailMergeAction.Conflict);

    /// <summary>Count of guardrails whose human version is preserved.</summary>
    public int PreservedCount => Items.Count(i => i.Action == GuardrailMergeAction.KeepLocal);

    /// <summary>Count of guardrails dropped (regeneration no longer emits them).</summary>
    public int DroppedCount => Items.Count(i => i.Action == GuardrailMergeAction.Drop);
}
