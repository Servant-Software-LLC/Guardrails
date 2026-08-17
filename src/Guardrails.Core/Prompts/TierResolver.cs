using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The STATIC attempt-launch resolver (DoR <c>docs/plans/17-model-tiering.md</c> §6, issue #226-static):
/// the one place a (tier, registry) pair becomes a concrete (block, model, effort) route. It runs
/// immediately before EVERY attempt launch, retries included, and in v1 it is a pure function of its
/// inputs — no probes (§6.4), no ladder (§7), no steering (§8) — so it yields the same block on every
/// attempt of a task. Those dynamic inputs are v2 and slot in here without moving the seam.
///
/// <para><b>Two entry points, one of them the front door.</b> <see cref="Resolve"/> is §6.1
/// precedence — the pin / tier / legacy order the attempt launcher calls — and
/// <see cref="SelectCandidate"/> is §6.2 candidate selection, which <see cref="Resolve"/> CALLS for
/// its middle branch and never re-derives. Keeping the two seams distinct is what stops the costly
/// floor from acquiring a second, weaker copy: exactly one method in this file filters candidates.</para>
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
    public static TierResolution Resolve(ActionDefinition action, RunConfig config, string? cliDefaultModel = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(config);

        // ── 1. Full pin ──────────────────────────────────────────────────────────────────────────
        // Explicit always wins, and it wins BEFORE any rung is read: the pinned block is never put to
        // the candidacy predicate at all. That is what makes a pin the sanctioned route to a
        // costly: true block (D22 / charter Decision 3) — the floor constrains what the HARNESS may
        // choose, never what a human may assign — and it is why this branch sits ABOVE the tier read
        // rather than inside it with a costly exemption, which would be a second candidate-filtering
        // path around the floor.
        if (action.Runner is not null || action.Model is not null)
        {
            // action.runner names the block; a raw action.model pin overrides the model STRING but not
            // the block (§6.1 item 1's parenthetical), so the block stays the one today's two-level
            // fallback already uses — action.runner ?? the promptRunners.default pointer — and §6.5's
            // judge rule can still read strength/kind off it for a pinned actor.
            string? pinnedName = action.Runner ?? config.DefaultPromptRunner;
            PromptRunnerConfig? block = NamedBlock(config, pinnedName);

            return new TierResolution
            {
                Pinned = true,
                Runner = block,
                RunnerName = pinnedName,
                Model = action.Model ?? block?.Settings.Model ?? cliDefaultModel,
                Effort = action.Effort ?? block?.Effort
            };
        }

        // ── 2. Tier resolution ───────────────────────────────────────────────────────────────────
        if (EffectiveTier(action, config) is { } tier)
        {
            // The route is SelectCandidate's answer and nothing else — §6.2's ordering, climb, costly
            // floor and no-route are called here, never re-derived, so this method cannot acquire a
            // candidate-filtering path of its own.
            TierResolution resolved = SelectCandidate(config, tier);

            // action.effort ALONE is NOT a bypass (the F4 correction). Selection above has ALREADY
            // chosen the block; the override lands on THAT route's effort — "{ tier: medium, effort:
            // xhigh }" means "route by tier, but think hard". Letting it short-circuit to the default
            // pointer would be treating action.model's SHAPE as action.model's BYPASS, which is the
            // rule most likely to be implemented backwards. A no-route carries no route facts, so
            // there is nothing there to override.
            //
            // Nothing falls through past this branch: once an effective tier exists, resolution OWNS
            // the outcome (D30) — an empty candidate set climbs, and an empty registry at-or-above the
            // rung settles NoRoute. Never the runner's model, which is sitting right there and is
            // exactly the revision-4 reading D30 severed.
            return action.Effort is { } effort && resolved.Runner is not null
                ? resolved with { Effort = effort }
                : resolved;
        }

        // ── 3. Legacy fallback ───────────────────────────────────────────────────────────────────
        // Reached ONLY because the action has no rung to resolve — Invariant 7's untagged case, which
        // holds WHETHER OR NOT routing blocks are configured elsewhere in the registry (§4's activation
        // is PLAN-scoped, not config-scoped: a routing-enabled config with a zero-tag plan does nothing
        // tiering-specific). Nothing above this line consulted the registry for candidacy, so an
        // untagged task runs ZERO tier-resolution activity — CostlyCeilingBound stays false because no
        // sweep ever ran, not because a sweep found nothing. A single-model user never opted into any
        // of this, and routing them through resolution "for uniformity" is what Invariant 7 forbids.
        PromptRunnerConfig? legacyBlock = NamedBlock(config, config.DefaultPromptRunner);

        return new TierResolution
        {
            Legacy = true,
            Runner = legacyBlock,
            RunnerName = config.DefaultPromptRunner,
            // Exactly today's two-level fallback: promptRunners.<name>.model, else the CLI default,
            // else null — "the runner CLI's own default", which is what today's "(cli default)"
            // display sentinel stands for.
            Model = legacyBlock?.Settings.Model ?? cliDefaultModel,
            // The route here IS the default pointer's block, so the override applies to it for the same
            // reason it applies to a resolved route: action.effort adjusts the route that was chosen.
            // Dropping it on this path would make a per-task knob the operator wrote do nothing, which
            // is not what "no rung to resolve" means.
            Effort = action.Effort ?? legacyBlock?.Effort
        };
    }

    /// <summary>
    /// The EFFECTIVE tier for this action — <c>action.tier</c> ?? the plan-wide
    /// <c>tiering.defaultTier</c> (§6.1 item 2) — or null when there is no rung to resolve at all,
    /// which is the ONLY trigger for the legacy path (D30).
    ///
    /// <para>In a loaded plan <c>action.Tier</c> arrives ALREADY collapsed — <c>PlanLoader</c> applies
    /// this same precedence at load, which is what makes the plan-wide default reach a task hand-added
    /// to the folder after breakdown. The default is consulted here anyway because the resolver is also
    /// called on actions that never went through that collapse (and re-reading it costs nothing once it
    /// has: <c>action.Tier</c> wins either way).</para>
    /// </summary>
    private static string? EffectiveTier(ActionDefinition action, RunConfig config) =>
        action.Tier ?? PropagatableDefaultTier(config);

    /// <summary>
    /// The plan-wide default tier as it actually reaches a task: only a RECOGNIZED token propagates —
    /// deliberately the same rule, and the same name, as <c>PlanLoader.PropagatableDefaultTier</c>.
    ///
    /// <para>The filter is not decoration. The loader has already declared an unrecognized
    /// <c>tiering.defaultTier</c> non-propagating (GR2043 reports it at validate time, naming the bad
    /// value), so every untagged task in such a plan arrives with a null tier. Reading the raw default
    /// here would UNDO that decision and turn each of those tasks into a <c>no-route</c> halt on a rung
    /// the loader said does not exist. An unrecognized <c>action.tier</c> is the opposite case: it is
    /// bound VERBATIM, so it reaches <see cref="SelectCandidate"/> and settles
    /// <see cref="TierResolution.NoRoute"/> — that rung was genuinely asked for.</para>
    /// </summary>
    private static string? PropagatableDefaultTier(RunConfig config) =>
        ActionTiers.IsRecognized(config.Tiering?.DefaultTier) ? config.Tiering!.DefaultTier : null;

    /// <summary>
    /// The registry block <paramref name="name"/> points at, or null when it names nothing (or when
    /// there is no name at all) — the residual a plan validation already rejects, since the
    /// <c>default</c> pointer must name a declared runner. Resolved defensively rather than thrown on,
    /// for the same reason <see cref="TierResolution.NoRoute"/> is a result: the model then falls
    /// through to the CLI default exactly as today's <c>ResolveModelForDisplay</c> does for an
    /// unresolvable runner, and the name the caller asked for is still reported.
    /// </summary>
    private static PromptRunnerConfig? NamedBlock(RunConfig config, string? name) =>
        name is not null && config.PromptRunners.TryGetValue(name, out PromptRunnerConfig? block)
            ? block
            : null;

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
