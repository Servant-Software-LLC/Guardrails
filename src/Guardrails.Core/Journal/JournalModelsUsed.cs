namespace Guardrails.Core.Journal;

/// <summary>
/// MODELS-USED aggregation (#349, surface 5 of 5) — the sibling of <see cref="JournalTierSpend"/>: the same
/// read over the same records, grouped by the model each attempt actually ran on
/// (<see cref="AttemptProvenance.Model"/>) rather than by the rung it resolved on.
///
/// <para>This is the line that answers "what actually served this run?". It is not derivable from the
/// per-tier line: one rung can be served by several models over a run's lifetime (a route edited mid-plan, a
/// provider substituting), and a pinned or legacy-fallback attempt names a model while resolving no rung at
/// all — so it would be invisible to the tier split and is counted here.</para>
///
/// <para><b>Nothing here re-derives anything.</b> Both fields it reads were folded onto the attempt ONCE, by
/// <c>TaskExecutor</c>, at attempt launch: <see cref="AttemptProvenance.Model"/> is best-known-actual (the
/// model the runner reported itself running on, else the resolved route's, else the <c>"(cli default)"</c>
/// sentinel) and <see cref="AttemptProvenance.RequestedModel"/> is what the route ASKED for, written ONLY
/// when the two disagree. No stream is re-parsed and no <c>--model</c> is forced; a second owner of that rule
/// would drift from the <c>run.json</c> it is reporting (D22a).</para>
///
/// <para><b><c>requestedModel</c>'s PRESENCE is the mismatch signal</b>, so there is no flag beside it and no
/// key at all in the agreeing case — which is the overwhelmingly common one. An aggregation that assumes both
/// keys always exist is therefore wrong, and a <see cref="ModelUsage.RequestedModels"/> that is EMPTY is the
/// normal reading, not a missing one.</para>
///
/// <para><b>THE SUPPRESSION RULE, inherited verbatim from <see cref="JournalTierSpend"/>'s Invariant 7.</b>
/// When NO attempt recorded a model, the summary is NOTHING AT ALL: <see cref="Summarize"/> returns
/// <c>null</c> and <see cref="Render"/> returns <c>null</c>. Not an empty list, not an empty string, not a
/// label with no segments, and — the failure mode that would land on every deterministic-only plan — not a
/// bucket of its own for the attempts that recorded no model. A script-only run keeps printing EXACTLY
/// today's summary and not one character more. A null return is what lets the caller spell that as
/// <c>if (Render(document) is { } line)</c>, the same shape <see cref="JournalTierSpend.Render"/> already
/// has.</para>
/// </summary>
public static class JournalModelsUsed
{
    /// <summary>
    /// Groups every attempt in <paramref name="document"/> that carries a non-null
    /// <see cref="AttemptProvenance.Model"/> by that model, counting the attempts and collecting the DISTINCT
    /// <see cref="AttemptProvenance.RequestedModel"/> values seen against it.
    ///
    /// <para>Every attempt counts INDEPENDENTLY, retries included — exactly as
    /// <see cref="JournalTierSpend.Summarize"/> counts them, and for the same reason: a retry launched a model
    /// again. Folding a task down to its final attempt would under-report by exactly the retry volume.</para>
    ///
    /// <para>Rows come back ordered by DESCENDING <see cref="ModelUsage.Attempts"/>, then ordinal-ascending
    /// <see cref="ModelUsage.Model"/>, so the line does not shuffle between runs of the same plan.
    /// <see cref="ModelUsage.Attempts"/> is always strictly positive: a row that counted nothing is never
    /// produced.</para>
    ///
    /// <para>Returns <c>null</c> — never an empty list — when NO attempt recorded a model. Attempts with no
    /// <see cref="AttemptRecord.Provenance"/> at all (a script attempt) or a null
    /// <see cref="AttemptProvenance.Model"/> are excluded OUTRIGHT and are NOT collected into a bucket of
    /// their own.</para>
    /// </summary>
    public static IReadOnlyList<ModelUsage>? Summarize(JournalDocument document) =>
        throw new NotImplementedException(
            "JournalModelsUsed.Summarize is the TDD stub for #349 surface 5 — "
            + "wave-04-report-and-cleanup/02-implement-models-used-report implements it.");

    /// <summary>
    /// The operator-facing models-used segments for <paramref name="document"/> — e.g.
    /// <c>"claude-sonnet-5-20260101 ×7 (substituted for claude-opus-5) · claude-opus-5 ×2"</c>.
    ///
    /// <para>Format: one segment per row of <see cref="Summarize"/>, joined with <c>" · "</c>. A segment is
    /// <c>"&lt;model&gt; ×&lt;attempts&gt;"</c>, with <c>" (substituted for &lt;a&gt;, &lt;b&gt;)"</c>
    /// appended when <see cref="ModelUsage.RequestedModels"/> is non-empty — the ids ordinal-ascending and
    /// de-duplicated. That clause is absent on the agreeing case, which is most attempts.</para>
    ///
    /// <para>Returns <c>null</c> when <see cref="Summarize"/> does: there is no line, as opposed to an empty
    /// one. No label or header is included — like <see cref="JournalTierSpend.Render"/>, this computes the
    /// value and the CALLER owns how it is labelled.</para>
    /// </summary>
    public static string? Render(JournalDocument document) =>
        throw new NotImplementedException(
            "JournalModelsUsed.Render is the TDD stub for #349 surface 5 — "
            + "wave-04-report-and-cleanup/02-implement-models-used-report implements it.");
}

/// <summary>
/// One model's slice of a run's attempts (#349): how many attempts ran on <see cref="Model"/>, and — only
/// where the runner served something other than the route asked for — which model(s) were REQUESTED against
/// it.
/// </summary>
public sealed record ModelUsage
{
    /// <summary>
    /// The model the attempts ran on, VERBATIM as the journal recorded it on
    /// <see cref="AttemptProvenance.Model"/> — including the <c>"(cli default)"</c> sentinel when nothing
    /// named one. The operator reads this line next to a <c>model:</c> in the config and a <c>"model"</c> in
    /// <c>run.json</c>; three spellings of one id is how a measurement stops being greppable.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// How many ATTEMPTS ran on <see cref="Model"/> — retries included, each counted independently. Always
    /// strictly positive on a returned row: a row that counted nothing is never produced.
    /// </summary>
    public required int Attempts { get; init; }

    /// <summary>
    /// The DISTINCT <see cref="AttemptProvenance.RequestedModel"/> values seen against <see cref="Model"/>.
    ///
    /// <para>EMPTY on the overwhelmingly common agreeing case: <c>requestedModel</c> is written only when the
    /// runner served something other than the route asked for, so its presence IS the mismatch signal and its
    /// absence is the ordinary reading — not a gap in the data.</para>
    /// </summary>
    public IReadOnlyList<string> RequestedModels { get; init; } = [];
}
