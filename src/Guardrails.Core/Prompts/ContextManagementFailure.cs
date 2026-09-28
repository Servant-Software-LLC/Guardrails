namespace Guardrails.Core.Prompts;

/// <summary>
/// How a session's context management failed (#811). One classification, shared across its causes, because the
/// operator and the retry need the same fact from each: the session could not fit its work in the model's context,
/// so the next attempt must carry less.
///
/// <para><b>The #800 seam.</b> #800 (Claude Code's "Autocompact is thrashing") is the second member of this
/// family. It lands as a new value here, detected in the same runner quarantine, and every surface below (the
/// attempt summary, <c>feedback.md</c>, the <c>attempt-stalled</c> row) picks it up with no change. #800's own
/// behavior (ending the attempt at once, escalating on repeat) is a routing decision this type does not make.</para>
/// </summary>
public enum ContextManagementFailureKind
{
    /// <summary>
    /// A context compaction was attempted and failed: Claude Code's
    /// <c>{"type":"system","subtype":"status","compact_result":"failed","compact_error":"…"}</c>.
    /// </summary>
    CompactionFailed
}

/// <summary>
/// A context-management failure a runner observed in its own stream (#811), carried on
/// <see cref="PromptResult.ContextManagement"/>. Runner-agnostic: the vendor line that revealed it is read inside the
/// runner quarantine (SSOT §9), and only this record leaves it.
/// </summary>
/// <param name="Kind">Which failure.</param>
/// <param name="Detail">The runner's own error text (for example <c>Request timed out</c>), or null when it gave none.</param>
/// <param name="Count">How many times it happened in the attempt (at least 1).</param>
public sealed record ContextManagementFailure(ContextManagementFailureKind Kind, string? Detail, int Count)
{
    /// <summary>The wire token (<c>events.jsonl</c>, <c>observer.jsonl</c>).</summary>
    public string Token => Kind switch
    {
        ContextManagementFailureKind.CompactionFailed => "compaction-failed",
        _ => throw new InvalidOperationException($"Unhandled context-management failure '{Kind}'.")
    };

    /// <summary>The one-phrase rendering the attempt summary appends: <c>context compaction failed (Request timed out)</c>.</summary>
    public string Describe()
    {
        string what = Kind switch
        {
            ContextManagementFailureKind.CompactionFailed => "context compaction failed",
            _ => throw new InvalidOperationException($"Unhandled context-management failure '{Kind}'.")
        };

        string times = Count > 1 ? $" {Count} times" : string.Empty;
        return string.IsNullOrWhiteSpace(Detail) ? $"{what}{times}" : $"{what}{times} ({Detail})";
    }
}

/// <summary>
/// What the stall watchdog concluded when it killed a session (#811, #806): the bound it enforced, how long the
/// session had been silent, and how many host suspends it discounted along the way (#517). Carried on
/// <see cref="PromptResult.Stall"/> for the <c>attempt-stalled</c> event row.
/// </summary>
/// <param name="Bound">The silence bound.</param>
/// <param name="SilentFor">How long the session had produced no progress when it was killed.</param>
/// <param name="SuspendsObserved">How many polls found the host had been suspended, each resetting the window.</param>
/// <param name="ProgressBeats">
/// How many progress lines (or, for openai-compat, completed turns and data frames) the session produced before it went
/// silent. ZERO means the runner or its backend never produced anything at all, which is most likely not the model's
/// fault (#815 review W4).
/// </param>
/// <remarks>TODO(#810): carry the wall-clock and awake-clock durations here once the host-sleep monitor lands, so a stall
/// report can state both numbers the way a timeout does.</remarks>
public sealed record StallReport(TimeSpan Bound, TimeSpan SilentFor, int SuspendsObserved, int ProgressBeats = 0)
{
    /// <summary>True when the session produced no progress at all before the stall.</summary>
    public bool NoProgressAtAll => ProgressBeats == 0;
}
