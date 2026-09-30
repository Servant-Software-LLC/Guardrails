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
}
