using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The STATIC attempt-launch resolver (DoR <c>docs/plans/17-model-tiering.md</c> §6, issue #226-static):
/// the one place a (tier, registry) pair becomes a concrete (block, model, effort) route. It runs
/// immediately before EVERY attempt launch, retries included, and in v1 it is a pure function of its
/// inputs — no probes (§6.4), no ladder (§7), no steering (§8) — so it yields the same block on every
/// attempt of a task. Those dynamic inputs are v2 and slot in here without moving the seam.
///
/// <para><b>Half built.</b> <see cref="SelectCandidate"/> (§6.2 candidate selection) is implemented;
/// <see cref="Resolve"/> (§6.1 precedence) is still a stub that throws
/// <see cref="NotImplementedException"/>, and is filled by
/// <c>wave-01-resolver-core/04-implement-resolution-precedence</c>. Both were declared together,
/// ahead of either implementation, so the rest of wave 1 compiles and its tests go red against a
/// STABLE signature instead of racing the shape of the API.</para>
/// </summary>
public static class TierResolver
{
    /// <summary>
    /// §6.2 candidate selection — <b>never weaker than asked, never costly without you</b>. Selects
    /// the route for rung <paramref name="tier"/> from the plan's <c>promptRunners</c> registry.
    ///
    /// <para><b>Candidacy is <see cref="PromptRunnerConfig.ServesTier"/> and nothing else (D22a).</b>
    /// <c>routing</c> present ∧ rung ∈ <c>routing.tiers</c> ∧ <c>costly</c> is not <c>true</c>. That
    /// ONE predicate is shared with <c>validate</c>'s GR2048 check, the <c>no-route</c> outcome and
    /// the §6.5 judge route — a correctness requirement, not tidiness: if validation counted a block
    /// as serving a rung and this resolver did not, validation would pass and every task at that rung
    /// would die at run time.</para>
    ///
    /// <para>Candidates are ordered by ASCENDING <see cref="PromptRunnerConfig.Strength"/> — the
    /// WEAKEST block the operator declared capable of the rung wins — with unspecified strength last
    /// and ties broken by declaration order. An empty candidate set climbs to the nearest STRONGER
    /// rung with a non-empty set, recording the climb; it never routes down. No rung at-or-above with
    /// a candidate yields <see cref="TierResolution.NoRoute"/>, not an exception and not a silent
    /// fallback.</para>
    /// </summary>
    /// <param name="config">The plan's run configuration; its <c>promptRunners</c> map is the registry,
    /// enumerated in declaration order (the tie-break key).</param>
    /// <param name="tier">The rung to resolve — one of <see cref="ActionTiers.All"/>. An UNRECOGNIZED
    /// token has no place on the ladder, so there is no rung "at or above" it and it settles
    /// <see cref="TierResolution.NoRoute"/> — the same defensive residual as an unservable rung, and for
    /// the same reason: GR2043 already errors on it at validate time, and guessing where an unknown
    /// token sits would be inventing a route the design does not have.</param>
    /// <returns>The selected route, or a <see cref="TierResolution.NoRoute"/> result.</returns>
    public static TierResolution SelectCandidate(RunConfig config, string tier)
    {
        ArgumentNullException.ThrowIfNull(config);

        // ActionTiers.All is ordered ASCENDING by difficulty, so "at or above" is the tail from the
        // requested rung — the same walk GR2048 makes at validate time. Never-weaker-than-asked is
        // this list and nothing else: no rung below the requested one is ever considered.
        IReadOnlyList<string> atOrAbove = RungsAtOrAbove(tier);

        // The registry in DECLARATION order — the documented tie-break key.
        PromptRunnerConfig[] registry = [.. config.PromptRunners.Values];

        // D28: a block excluded ONLY for cost is DeclaresTier ∧ ¬ServesTier — exactly the pair GR2048
        // computes. Swept over EVERY rung at or above the requested one, and computed before the
        // selection loop, because the datum is true independently of whether a route was found: it is
        // equally the reason a weaker block was served and the reason the rung no-routes. Ordered
        // ordinal so the wave-2 warning naming these blocks is stable.
        string[] costlyCeiling =
        [
            .. registry
                .Where(runner => atOrAbove.Any(rung => runner.DeclaresTier(rung) && !runner.ServesTier(rung)))
                .Select(runner => runner.Name)
                .Order(StringComparer.Ordinal)
        ];

        TierResolution resolution = new()
        {
            RequestedTier = tier,
            CostlyCeilingBound = costlyCeiling.Length > 0,
            CostlyCeilingBlocks = costlyCeiling
        };

        foreach (string rung in atOrAbove)
        {
            if (BestCandidate(registry, rung) is not { } winner)
            {
                continue; // Candidates(rung) is empty — climb to the next STRONGER rung.
            }

            return resolution with
            {
                Runner = winner,
                RunnerName = winner.Name,
                Model = winner.Settings.Model,
                Effort = winner.Effort,
                Tier = rung,
                Climbed = !string.Equals(rung, tier, StringComparison.Ordinal)
            };
        }

        // Nothing at or above: the no-route RESULT. Not an exception the caller cannot tell from a bug,
        // and not a silent drop to a weaker rung or to the runner's model (D30).
        return resolution with { NoRoute = true };
    }

    /// <summary>
    /// §6.1 precedence — the full pin/config order, and the entry point the attempt launcher calls.
    ///
    /// <list type="number">
    ///   <item><b>Full pin</b> — <c>action.runner</c> / <c>action.model</c> bypasses tier resolution
    ///     entirely and is the sanctioned route to a <c>costly</c> block.</item>
    ///   <item><b>Tier resolution</b> — effective tier = <c>action.tier</c> ??
    ///     <c>tiering.defaultTier</c>, routed through <see cref="SelectCandidate"/>.
    ///     <c>action.effort</c> ALONE is NOT a bypass: it overrides the RESOLVED route's effort.</item>
    ///   <item><b>Legacy fallback — ONLY when there is no effective tier (D30)</b> —
    ///     <c>promptRunners.&lt;name&gt;.model</c> else <paramref name="cliDefaultModel"/>, exactly
    ///     today. Once a rung exists, resolution owns the outcome: climb, else
    ///     <see cref="TierResolution.NoRoute"/>. It never silently drops back to the runner's
    ///     model.</item>
    /// </list>
    /// </summary>
    /// <param name="action">The action being launched — the source of the pin, the tier and the
    /// effort override.</param>
    /// <param name="config">The plan's run configuration: the registry plus the <c>tiering</c> block.</param>
    /// <param name="cliDefaultModel">The CLI-level default model — the last rung of the legacy
    /// fallback. Null means "let the runner CLI pick its own default", today's behaviour.</param>
    /// <returns>The resolution for this attempt, on whichever of the three paths decided it.</returns>
    public static TierResolution Resolve(ActionDefinition action, RunConfig config, string? cliDefaultModel = null) =>
        throw new NotImplementedException(
            "TierResolver.Resolve is a stub — DoR §6.1 precedence is implemented by " +
            "wave-01-resolver-core/04-implement-resolution-precedence.");

    /// <summary>
    /// The single winner of <c>Candidates(rung)</c>, or null when that set is empty.
    ///
    /// <para>Candidacy is <see cref="PromptRunnerConfig.ServesTier"/> — the ONE predicate, CALLED and
    /// never re-spelled (D22a), which is what keeps this resolver and GR2048 from drifting apart.
    /// <see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/> is a
    /// STABLE sort, so equal strengths keep the order <paramref name="registry"/> was enumerated in —
    /// that is the declaration-order tie-break, not an accident of the sort.</para>
    /// </summary>
    private static PromptRunnerConfig? BestCandidate(IReadOnlyList<PromptRunnerConfig> registry, string rung) =>
        registry
            .Where(runner => runner.ServesTier(rung))
            // Ascending strength ⇒ the WEAKEST capable block wins; an unspecified strength sorts LAST,
            // because a block nobody ranked must not outrank one they did.
            .OrderBy(runner => runner.Strength ?? int.MaxValue)
            .FirstOrDefault();

    /// <summary>
    /// The requested rung and every STRONGER one, ascending — empty when <paramref name="tier"/> is not
    /// one of <see cref="ActionTiers.All"/>.
    /// </summary>
    private static IReadOnlyList<string> RungsAtOrAbove(string tier)
    {
        for (int i = 0; i < ActionTiers.All.Count; i++)
        {
            if (string.Equals(ActionTiers.All[i], tier, StringComparison.Ordinal))
            {
                return [.. ActionTiers.All.Skip(i)];
            }
        }

        return [];
    }
}
