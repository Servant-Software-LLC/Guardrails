using System.Globalization;

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
    public static IReadOnlyList<TierSpend>? Summarize(JournalDocument document)
    {
        Dictionary<string, RungAccumulator> byRung = new(StringComparer.Ordinal);

        foreach (TaskJournalEntry entry in document.Tasks.Values)
        {
            // Every ATTEMPT counts independently, retries included: resolution runs per attempt, so a
            // retry resolved and spent again. Folding a task down to its final attempt would under-report
            // the rung by exactly the retry spend — the spend this measurement most needs to see.
            foreach (AttemptRecord attempt in entry.Attempts)
            {
                string? tier = attempt.Provenance?.Tier;

                // No rung resolved — a legacy-fallback route (D30), a pin (D31), a script attempt, any
                // journal written before tiering. Excluded from the aggregation OUTRIGHT: it is NOT
                // collected into a bucket of its own, which is the Invariant 7 breakage §9.3 forbids by
                // name and the one that would land on every existing single-model user's run.
                if (string.IsNullOrWhiteSpace(tier))
                {
                    continue;
                }

                if (!byRung.TryGetValue(tier, out RungAccumulator? rung))
                {
                    rung = new RungAccumulator();
                    byRung.Add(tier, rung);
                }

                rung.Add(attempt);
            }
        }

        // NOT an empty list: a tiering-inactive run has nothing to report, and the null is what lets the
        // caller spell the suppression as `is { }` rather than testing a rendered line for emptiness.
        // document.OverheadCostUsd never reaches here at all — it is not a task attempt and resolved no
        // rung, so it belongs to none of these buckets (JournalCost.Total keeps folding it into the run
        // total exactly as it does today).
        if (byRung.Count == 0)
        {
            return null;
        }

        return AscendingRungs(byRung).Select(tier => byRung[tier].ToSpend(tier)).ToList();
    }

    /// <summary>
    /// The present rungs in <c>Model.ActionTiers.All</c>'s ASCENDING order — not dictionary order and not
    /// first-appearance order, so the line does not shuffle between runs of the same plan. A rung nothing
    /// resolved on is simply skipped, never an empty row.
    /// </summary>
    private static IEnumerable<string> AscendingRungs(Dictionary<string, RungAccumulator> byRung)
    {
        foreach (string tier in Model.ActionTiers.All)
        {
            if (byRung.ContainsKey(tier))
            {
                yield return tier;
            }
        }

        // A rung this build does not recognize is still REPORTED rather than silently dropped: the job
        // here is to account for recorded spend, not to re-validate a token the loader already gated.
        // Ordinal, after the known rungs, keeps the order deterministic all the same.
        foreach (string tier in byRung.Keys
                     .Where(t => !Model.ActionTiers.IsRecognized(t))
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            yield return tier;
        }
    }

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
        Summarize(document) is { } summary ? string.Join(" · ", summary.Select(Segment)) : null;

    /// <summary>
    /// One rung's segment: <c>"&lt;tier&gt;: &lt;tokens&gt; tok / $&lt;cost&gt;"</c> minus whichever half
    /// was never reported. The rung is labelled with the wire token VERBATIM as the journal recorded it —
    /// the same spelling the manifest's <c>tier:</c> and the journal's <c>"tier"</c> use, because three
    /// spellings of one concept is how a measurement stops being greppable.
    /// </summary>
    private static string Segment(TierSpend rung)
    {
        // A rung with tokens and no cost prints the volume ALONE — never "$0.00", a number the runner
        // never said. A RECORDED $0 is a reported fact and does print (§9.3's `easy: … / $0`), the same
        // distinction JournalCost.Total already draws between a null cost and a zero one.
        string? tokens = rung.TotalTokens is { } total ? $"{Volume(total)} tok" : null;
        string? money = rung.CostUsd is { } cost
            ? "$" + cost.ToString("F4", CultureInfo.InvariantCulture)
            : null;

        if (tokens is not null && money is not null)
        {
            return $"{rung.Tier}: {tokens} / {money}";
        }

        if (tokens is not null)
        {
            return $"{rung.Tier}: {tokens}";
        }

        if (money is not null)
        {
            return $"{rung.Tier}: {money}";
        }

        // Routing HAPPENED — that is itself evidence, so the rung is still named — but the runner
        // reported neither half, and a zero of either kind would be invented rather than measured.
        return $"{rung.Tier}: no spend reported";
    }

    /// <summary>
    /// Token volume: the exact count below a thousand (<c>"640"</c>), whole thousands with a <c>k</c>
    /// suffix at or above it (<c>"42k"</c>), invariant culture so the line reads the same everywhere.
    /// </summary>
    private static string Volume(long tokens) =>
        tokens >= 1_000
            ? (tokens / 1_000).ToString(CultureInfo.InvariantCulture) + "k"
            : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One rung's running totals while <see cref="Summarize"/> walks the journal. Cost and usage are
    /// tracked with SEPARATE "was any reported" flags because they are separately reported: null means
    /// "never reported", which is not the claim zero makes.
    /// </summary>
    private sealed class RungAccumulator
    {
        private decimal costUsd;
        private bool anyCost;
        private long inputTokens;
        private long outputTokens;
        private bool anyUsage;

        internal void Add(AttemptRecord attempt)
        {
            if (attempt.CostUsd is { } cost)
            {
                costUsd += cost;
                anyCost = true;
            }

            if (attempt.Usage is { } usage)
            {
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;
                anyUsage = true;
            }
        }

        internal TierSpend ToSpend(string tier) => new()
        {
            Tier = tier,
            CostUsd = anyCost ? costUsd : null,
            InputTokens = anyUsage ? inputTokens : null,
            OutputTokens = anyUsage ? outputTokens : null
        };
    }
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
