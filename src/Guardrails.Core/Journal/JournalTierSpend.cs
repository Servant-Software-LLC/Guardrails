namespace Guardrails.Core.Journal;

/// <summary>
/// PER-TIER spend aggregation (DoR §9.3, #230-lite) — the sibling of <see cref="JournalCost"/>: the same
/// journal, the same <c>costUsd</c> field, split by the rung each attempt actually resolved on
/// (<see cref="AttemptProvenance.Tier"/>) and carrying the optional <see cref="AttemptUsage"/> token volume
/// alongside the money.
///
/// <para>This is the measurement the v2 deferrals (probes, ladder, steering) are decided FROM: whether a
/// cheaper rung is carrying real work is a number, not an intuition. Cost alone cannot carry it — a costless
/// local provider or a flat-rate subscription honestly reports <c>$0</c> for a run that did enormous work,
/// which is exactly why <see cref="AttemptRecord.Usage"/> exists and why a rung degrades to TOKENS-ONLY
/// rather than claiming <c>$0.00</c> the runner never reported.</para>
///
/// <para><b>INVARIANT 7 — the suppression rule, and the reason this lives in its own class.</b> On a
/// tiering-INACTIVE run (no attempt resolved through routing, so no attempt carries
/// <c>provenance.tier</c>) the summary is NOTHING AT ALL: <see cref="Summarize"/> returns <c>null</c> and
/// <see cref="Render"/> returns <c>null</c>. Not an empty list, not an empty string, not a header with no
/// rows, and — the failure mode that would land on every existing user's run — not an <c>untiered:</c>
/// bucket. Every run that predates model tiering, and every run of a plan that tags nothing, keeps printing
/// EXACTLY today's <c>Total prompt cost</c> line and not one character more. A null return is what lets the
/// caller spell that as <c>if (Render(document) is { } line)</c>, the same shape
/// <c>JournalCost.Total(document) is { } total</c> already has.</para>
///
/// <para><b>Overhead spend is not a rung.</b> <see cref="JournalDocument.OverheadCostUsd"/> — the
/// overwatcher's diagnose prompts, the AI-merge worker, the terminal needs-human triage (SSOT §9.2,
/// #269/#314) — belongs to NO tier: it is not a task attempt and it resolved no rung. It is deliberately
/// absent from every bucket here, and <see cref="JournalCost.Total"/> keeps folding it into the run total
/// exactly as it does today. The two aggregations answer different questions and must not be made to agree.
/// </para>
/// </summary>
public static class JournalTierSpend
{
    /// <summary>
    /// Groups every attempt in <paramref name="document"/> that carries a
    /// <see cref="AttemptProvenance.Tier"/> by that rung, summing <see cref="AttemptRecord.CostUsd"/> and,
    /// where present, <see cref="AttemptUsage.InputTokens"/>/<see cref="AttemptUsage.OutputTokens"/>.
    ///
    /// <para>Rungs come back in ASCENDING difficulty — <c>Model.ActionTiers.All</c>'s order — so the report
    /// does not shuffle between runs of the same plan; a rung with no attempt is simply absent, never an
    /// empty row. Every attempt counts independently, retries included: resolution runs per attempt, so two
    /// attempts of the same task on the same rung are two contributions to it.</para>
    ///
    /// <para>Returns <c>null</c> — never an empty list — when NO attempt carries a tier (Invariant 7).
    /// Attempts without one (a legacy-fallback route, a pin, a script action, any older journal) are
    /// excluded from the aggregation entirely and are NOT collected into a bucket of their own.</para>
    /// </summary>
    public static IReadOnlyList<TierSpend>? Summarize(JournalDocument document) =>
        throw new NotImplementedException(
            "JournalTierSpend.Summarize is the #230-lite per-tier aggregation stub — implemented by " +
            "wave-02-attempt-launch-wiring/11.");

    /// <summary>
    /// The operator-facing per-tier spend line for <paramref name="document"/> — DoR §9.3's worked example
    /// is <c>"hard: 42k tok / $3.12 · easy: 180k tok / $0"</c> (this renders it ascending:
    /// <c>easy</c> first).
    ///
    /// <para>Format: one segment per rung of <see cref="Summarize"/>, joined with <c>" · "</c>. A segment is
    /// <c>"&lt;tier&gt;: &lt;tokens&gt; tok / $&lt;cost&gt;"</c>, DROPPING whichever half was never
    /// reported — a rung with tokens and no cost renders the volume alone (never <c>$0.00</c>, which would
    /// assert a fact the runner never reported), a rung with cost and no tokens renders the money alone, and
    /// a rung that routed but reported neither reads <c>"&lt;tier&gt;: no spend reported"</c> rather than
    /// inventing a zero. A RECORDED <c>$0</c> is a reported fact and does print (the worked example's
    /// <c>easy: … / $0</c>), the same distinction <see cref="JournalCost.Total"/> already draws between a
    /// null cost and a zero one.</para>
    ///
    /// <para>Token volume is <c>inputTokens + outputTokens</c>, printed as the exact count below 1000
    /// (<c>"640 tok"</c>) and in whole thousands with a <c>k</c> suffix at or above it (<c>"42k tok"</c>),
    /// invariant culture. Cost is <c>$</c> + <c>F4</c>, matching the shipped
    /// <c>Total prompt cost: ${total:F4}</c> line it sits under.</para>
    ///
    /// <para>Returns <c>null</c> when <see cref="Summarize"/> does — there is no line, as opposed to an
    /// empty one. No prefix or header is included: like <see cref="JournalCost.Total"/>, this computes the
    /// value and the caller owns how it is labelled.</para>
    /// </summary>
    public static string? Render(JournalDocument document) =>
        throw new NotImplementedException(
            "JournalTierSpend.Render is the #230-lite per-tier spend line stub — implemented by " +
            "wave-02-attempt-launch-wiring/11.");
}

/// <summary>
/// One rung's slice of a run's prompt spend (DoR §9.3, #230-lite): what the attempts that resolved on
/// <see cref="Tier"/> cost, and how many tokens they moved.
///
/// <para>Cost and tokens are INDEPENDENTLY nullable because they are independently REPORTED: a costless
/// provider reports volume and no money, a runner that reports no usage reports money and no volume. Null
/// means "never reported", which is not the same claim as zero — see <see cref="JournalTierSpend.Render"/>.
/// </para>
/// </summary>
public sealed record TierSpend
{
    /// <summary>The rung — one of <c>Model.ActionTiers.All</c>, verbatim as the journal recorded it.</summary>
    public required string Tier { get; init; }

    /// <summary>
    /// The summed <see cref="AttemptRecord.CostUsd"/> of this rung's attempts, or null when NOT ONE of them
    /// recorded a cost. A recorded <c>$0</c> yields <c>0m</c>, not null.
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// The summed <see cref="AttemptUsage.InputTokens"/> of this rung's attempts, or null when not one of
    /// them reported usage. Null together with <see cref="OutputTokens"/> — they arrive as one block.
    /// </summary>
    public long? InputTokens { get; init; }

    /// <summary>The summed <see cref="AttemptUsage.OutputTokens"/>; null under the same condition.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>
    /// The volume the line prints: <see cref="InputTokens"/> + <see cref="OutputTokens"/>, or null when
    /// neither was reported. Derived here so the renderer and any other reader cannot disagree about what
    /// "42k tok" counts.
    /// </summary>
    public long? TotalTokens =>
        InputTokens is null && OutputTokens is null ? null : (InputTokens ?? 0L) + (OutputTokens ?? 0L);
}
