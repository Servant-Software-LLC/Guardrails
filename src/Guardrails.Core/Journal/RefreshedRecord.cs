namespace Guardrails.Core.Journal;

/// <summary>
/// One entry in <c>run.json</c>'s <c>refreshed[]</c> (design 39 §1c "How a refresh is recorded" / §5) —
/// provenance for a post-delivery merge that pulled the plan branch back onto the user's branch after a
/// non-fast-forward barrier delivery.
/// <para>
/// <b>NOT a supply.</b> A refresh admits content no task authored, exactly as a <see cref="SuppliedRecord"/>
/// does, but it has no caller to put in <c>by</c>, a <c>Supplied-By:</c> trailer on it would be a false
/// statement, and a merge has no honest <c>bytes</c>. So it gets its own top-level section — never a
/// <c>kind</c> field on <see cref="SuppliedRecord"/>, and never an entry in <c>supplied[]</c>. What the two
/// share is ONE reader, <see cref="UnauthoredContentNote"/>, which reads BOTH sections to answer "what is in
/// this tree that no task authored?".
/// </para>
/// <para>
/// Plain auto-implemented properties — <see cref="JournalJson"/> is reflection-based (no source-gen
/// context to register against) and every field here is a primitive or a list of strings, so the default
/// <c>System.Text.Json</c> reflection reader/writer round-trips it in both directions without a custom
/// converter, exactly like <see cref="SuppliedRecord"/>. Appended to <see cref="JournalDocument.Refreshed"/>
/// by <see cref="RunJournal.RecordRefreshed"/>.
/// </para>
/// </summary>
public sealed record RefreshedRecord
{
    /// <summary>UTC time the refresh commit was made (ISO-8601).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The refresh merge commit on the plan branch.</summary>
    public required string Commit { get; init; }

    /// <summary>The user's branch that was merged in — the delivery target pinned at run start.</summary>
    public required string From { get; init; }

    /// <summary>The sha that was merged — the refresh commit's second parent.</summary>
    public required string Upstream { get; init; }

    /// <summary>The wave whose non-fast-forward delivery triggered this refresh.</summary>
    public required string DeliveredWave { get; init; }

    /// <summary>The paths the refresh changed, forward-slash, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Paths { get; init; }
}
