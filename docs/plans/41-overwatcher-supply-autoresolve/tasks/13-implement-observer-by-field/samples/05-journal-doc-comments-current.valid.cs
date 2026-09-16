using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>The run journal document (run.json).</summary>
public sealed record JournalDocument
{
    /// <summary>
    /// OPTIONAL provenance record of every file supplied into this run's base (design 40 §4, SSOT §7
    /// top-level <c>supplied[]</c>) — so the run's own output is never confusable with what was handed
    /// to it. Absent (never <c>null</c> noise) on a run that never supplied anything.
    /// <para>
    /// Written only by <see cref="RunJournal.RecordSupplied"/>, after the commit it names exists: by the
    /// run-start drain in <c>RunCommand</c>, by <c>Scheduler.DrainSuppliedAtTaskBoundary</c>, and by the
    /// Scheduler's missing-resource auto-resolve (design 41, <c>by: "overwatcher"</c>).
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SuppliedRecord>? Supplied { get; init; }

    /// <summary>
    /// OPTIONAL provenance record of every post-delivery refresh merge — the plan branch pulled back onto
    /// the user's branch after a non-fast-forward barrier delivery (design 39 §1c / §5, SSOT §7 top-level
    /// <c>refreshed[]</c>). A sibling of <see cref="Supplied"/>, never a <c>kind</c> on
    /// <see cref="SuppliedRecord"/>: a refresh has no caller for <c>by</c> and no honest <c>bytes</c> for
    /// a merge.
    /// <para>
    /// Written only by <see cref="RunJournal.RecordRefreshed"/>, after the refresh merge commit exists
    /// (design 39 §1c).
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RefreshedRecord>? Refreshed { get; init; }
}

/// <summary>
/// What an operator's <c>--merge-on-success</c> override delivered PAST (SSOT §7
/// <c>delivery.forcedPastDecision</c>).
/// </summary>
public sealed record ForcedDeliveryRecord
{
    /// <summary>
    /// The overridden decision's token — the machine decision that would otherwise have held the work:
    /// <c>proceeded-best-guess</c>, <c>proceeded-unreviewed</c> or <c>auto-supplied</c>. The set is read
    /// from one shared predicate (<c>RunOutcomePolicy</c>), so this comment names the members rather than
    /// asserting how many there are — design 41 §6 adds a third, and a comment that counts its own set
    /// goes stale the moment the set grows.
    /// </summary>
    public required string Decision { get; init; }
}
