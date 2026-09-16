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
    /// THE ONE DEFECT THIS SAMPLE CARRIES: Supplied above was corrected — it is the one issue #712 lists —
    /// and its twin below was not. Refreshed still calls a property that shipped long ago "the STUB half"
    /// of its authoring task, three lines under the paragraph that was just fixed (design 41 §8, fact 10).
    ///
    /// OPTIONAL provenance record of every post-delivery refresh merge (design 39 §1c / §5, SSOT §7
    /// top-level <c>refreshed[]</c>).
    /// <para>
    /// This property is the STUB half of task <c>24-author-tests-refresh-provenance</c> — an honest,
    /// working container. Its element type, <see cref="RefreshedRecord"/>, is the half that is NOT yet
    /// implemented; see that type's remarks.
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
    /// THE SECOND DEFECT THIS SAMPLE CARRIES, and it is the reason the invalid half was extended at
    /// review: without it the removal clause below was never exercised by the PAIR at all — it fired only
    /// against the real tree, which is not a two-sided proof of that clause.
    ///
    /// The overridden decision's token: <c>proceeded-best-guess</c> or <c>proceeded-unreviewed</c> — the
    /// two <c>decisions[]</c> tokens that suppress delivery (<c>RunOutcomePolicy.SuppressingDecision</c>).
    /// Design 41 §6 adds a third, so this sentence both enumerates an incomplete set and asserts a
    /// cardinality that is false the moment task 07 lands.
    /// </summary>
    public required string Decision { get; init; }
}
