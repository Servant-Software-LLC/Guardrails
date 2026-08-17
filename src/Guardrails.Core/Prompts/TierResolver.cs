using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The STATIC attempt-launch resolver (DoR <c>docs/plans/17-model-tiering.md</c> §6, issue #226-static):
/// the one place a (tier, registry) pair becomes a concrete (block, model, effort) route. It runs
/// immediately before EVERY attempt launch, retries included, and in v1 it is a pure function of its
/// inputs — no probes (§6.4), no ladder (§7), no steering (§8) — so it yields the same block on every
/// attempt of a task. Those dynamic inputs are v2 and slot in here without moving the seam.
///
/// <para><b>STUB — declarations only.</b> Both entry points throw
/// <see cref="NotImplementedException"/>. They are declared together, ahead of either implementation,
/// so the rest of wave 1 compiles and its tests go red against a STABLE signature instead of racing
/// the shape of the API. <c>SelectCandidate</c> is filled by
/// <c>wave-01-resolver-core/02-implement-candidate-selection</c>; <c>Resolve</c> by
/// <c>04-implement-resolution-precedence</c>.</para>
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
    /// <param name="tier">The rung to resolve — one of <see cref="ActionTiers.All"/>.</param>
    /// <returns>The selected route, or a <see cref="TierResolution.NoRoute"/> result.</returns>
    public static TierResolution SelectCandidate(RunConfig config, string tier) =>
        throw new NotImplementedException(
            "TierResolver.SelectCandidate is a stub — DoR §6.2 candidate selection is implemented by " +
            "wave-01-resolver-core/02-implement-candidate-selection.");

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
}
