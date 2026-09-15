using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// One wave's delivery record (design 39 §4/§5, <c>run.json</c>'s <c>waves.&lt;dir&gt;.delivered</c>) — did
/// THIS wave's barrier delivery reach the user's branch, and if not, why not. A wave that is not a delivery
/// point, or has not reached its barrier yet, carries no <see cref="WaveJournalEntry.Delivered"/> at all
/// (see that member's remarks).
/// <para>
/// STUB (task 09): every getter throws <see cref="NotImplementedException"/> until task 10 implements the
/// record for real; the <c>init</c> accessors are real so a caller can build one with object-initializer
/// syntax before the type is otherwise usable, matching the pattern <see cref="Model.WaveNode.Delivers"/>
/// used for the same reason.
/// </para>
/// </summary>
public sealed record WaveDeliveredRecord
{
    /// <summary>Where this delivery stands: <c>running</c>, <c>delivered</c>, <c>refused</c> or <c>suppressed</c>.</summary>
    public required WaveDeliveryStatus Status { get => throw new NotImplementedException(); init { } }

    /// <summary>
    /// When the barrier reached this delivery — written the moment the delivery is allowed to proceed,
    /// before the trial merge runs the user's hooks (#625). A <c>suppressed</c> record is written already
    /// settled, so this is still the moment the interlock held it.
    /// </summary>
    public required DateTimeOffset StartedAt { get => throw new NotImplementedException(); init { } }

    /// <summary>When the delivery settled. Absent (no key at all) while <see cref="Status"/> is <c>running</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? At { get => throw new NotImplementedException(); init { } }

    /// <summary>The user's branch tip after promotion. Only set when <see cref="Status"/> is <c>delivered</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get => throw new NotImplementedException(); init { } }

    /// <summary>
    /// <c>fast-forwarded</c> when delivered; a refusal token
    /// (<c>conflict</c>/<c>dirty-working-tree</c>/<c>hook-rejected</c>/<c>branch-moved</c>/<c>trial-gate-failed</c>)
    /// when refused. Absent while <see cref="Status"/> is <c>running</c> or <c>suppressed</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeliveryOutcome? Outcome { get => throw new NotImplementedException(); init { } }

    /// <summary>
    /// The refusal detail; on <c>suppressed</c>, the suppressing decision and its subject; on a delivery
    /// <c>--merge-on-success</c> forced past a held decision, the decision it overrode.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get => throw new NotImplementedException(); init { } }

    /// <summary>Every wave this delivery carries, in order, ending with this one.</summary>
    public required IReadOnlyList<string> Covers { get => throw new NotImplementedException(); init { } }
}

/// <summary>The wave delivery lifecycle (design 39 §4, <c>waves.&lt;dir&gt;.delivered.status</c>).</summary>
public enum WaveDeliveryStatus
{
    /// <summary>The barrier delivery has begun and has not yet settled.</summary>
    Running,

    /// <summary>The wave's work reached the user's branch.</summary>
    Delivered,

    /// <summary>The delivery attempt could not land — see <see cref="WaveDeliveredRecord.Outcome"/>.</summary>
    Refused,

    /// <summary>An interlock (or an earlier <c>hook-rejected</c> refusal) held this delivery without attempting it.</summary>
    Suppressed
}
