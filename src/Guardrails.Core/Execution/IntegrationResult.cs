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
    Merged
}
