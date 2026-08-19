using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The STATIC attempt-launch resolver (DoR <c>docs/plans/17-model-tiering.md</c> §6, issue #226-static):
/// the one place a (tier, registry) pair becomes a concrete (block, model, effort) route. It runs
/// immediately before EVERY attempt launch, retries included, and in v1 it is a pure function of its
/// inputs — no probes (§6.4), no ladder (§7), no steering (§8) — so it yields the same block on every
/// attempt of a task. Those dynamic inputs are v2 and slot in here without moving the seam.
///
/// <para><b>Three entry points, one of them the front door.</b> <see cref="Resolve"/> is §6.1
/// precedence — the pin / tier / legacy order the attempt launcher calls — and
/// <see cref="SelectCandidate"/> is §6.2 candidate selection, which <see cref="Resolve"/> CALLS for
/// its middle branch and never re-derives. Keeping the two seams distinct is what stops the costly
/// floor from acquiring a second, weaker copy: exactly one method in this file filters candidates.
/// <see cref="ResolveJudge"/> is the §6.5 VERIFIER route — the same registry and the same candidacy
/// predicate, asking a different question ("who may vouch for this?"). It lives HERE rather than
/// beside the guardrail runner for exactly that reason: a judge chosen by a second candidacy rule is
/// the D22a divergence one level down.</para>
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
    /// §6.5 + §6.5.1 — the VERIFIER route: <b>"a prompt may propose, only an equal-or-stronger judge
    /// may vouch"</b>. Resolves the (block, model, effort) one PROMPT-judge guardrail runs on, at
    /// attempt launch, alongside the actor and from the same registry. Deterministic guardrails run no
    /// model and never reach this method.
    ///
    /// <para><b>Order of operations — §6.5.1's restatement, which is the contract:</b></para>
    /// <list type="number">
    ///   <item><b>Explicit wins (rule 1).</b> A <paramref name="runnerPin"/> names a block directly;
    ///     a <see cref="GuardrailDefinition.Tier"/> names a rung resolved like an action's (§6.1). No
    ///     rule below applies — and a <i>runner</i> pin also bypasses the §6.5.1 floor, because it
    ///     leaves no rung for a floor to raise. (The advisory still compares what the pin actually
    ///     got: the floor governs resolution, the advisory governs reality.)</item>
    ///   <item><b>Otherwise the judge's rung = the ACTOR's rung (rule 2)</b> — the rung, never the
    ///     actor's <c>strength</c>: rung is what <c>routing.tiers</c> is expressed in, and there is no
    ///     tier→strength mapping anywhere in this design. A PINNED or LEGACY actor resolved no rung of
    ///     its own (<see cref="TierResolution.Tier"/> is null there), so the rung falls back to the
    ///     plan-wide <c>tiering.defaultTier</c> — the rung this task WOULD have resolved at. That
    ///     fallback is what gives a judge for a hand-pinned actor anything to reason about, and it is
    ///     the case D29 below is written for.</item>
    ///   <item><b>The bump is in STRENGTH, never in TIER (rule 3 / D24a).</b> The candidate set is
    ///     <c>Candidates(rung)</c> filtered to those meeting the required strength — <c>≥</c> the
    ///     actor's, or STRICTLY greater when the actor is weak (see
    ///     <see cref="IsWeakVerifier"/>) — and the WEAKEST of those wins. Bumping the <i>tier</i>
    ///     would mean "pretend the work is harder", which drags the judge into a rung nobody declared
    ///     for this work; the resolved <see cref="JudgeResolution.Tier"/> therefore stays at the
    ///     actor's rung.</item>
    ///   <item><b>Then the floor (§6.5.1).</b> If the rung from (2)–(3) is BELOW
    ///     <c>tiering.verifier.minTier</c>, raise it to <c>minTier</c> and re-select from
    ///     <c>Candidates(minTier)</c>. <b>Never the reverse</b> — a result at or above the floor is
    ///     untouched. That asymmetry is the whole distinction between a floor and a default: a
    ///     plan-wide <c>easy</c> must never drag a <c>hard</c> judge down. With no rung at all (a
    ///     revalidation, or a pinned actor in a plan with no <c>defaultTier</c>) the floor SUPPLIES
    ///     the rung rather than raising one.</item>
    ///   <item><b>Specialization breaks ties, and ONLY ties (rule 6).</b> Among candidates that
    ///     ALREADY meet the required strength, prefer <c>planning-reasoning</c>; otherwise §6.2's
    ///     ascending-strength order (ties by declaration order). It can neither satisfy nor violate
    ///     the strength requirement, so a specialized-but-too-weak block is never chosen.</item>
    /// </list>
    ///
    /// <para><b>It DEGRADES; it never overspends, and it never halts (rule 5).</b> The costly floor
    /// (§6.2) binds every selection above. When the only qualifying block is <c>costly: true</c> the
    /// judge <b>stays at the actor's route</b>, <see cref="JudgeResolution.Degraded"/> is set, the
    /// #229 advisory fires and <b>the run proceeds</b>. The ACTOR in the same situation HALTS
    /// (<see cref="TierResolution.NoRoute"/>) — same input, opposite response, because an actor route
    /// is load-bearing and a verifier opinion is advisory. There is deliberately no <c>NoRoute</c> on
    /// <see cref="JudgeResolution"/>: a judge that cannot be improved is a warning, never an outcome.</para>
    ///
    /// <para><b>D29 — the one carve-out, and it is narrow.</b> When the ACTOR is running on an
    /// explicitly PINNED <c>costly</c> block (<see cref="TierResolution.Pinned"/> ∧ that block's
    /// <see cref="PromptRunnerConfig.Costly"/> is true), a human has already authorized costly spend
    /// for this task, so the judge MAY bump into a <c>costly: true</c> block — no halt, no prompt.
    /// This is consistent with the floor rather than an exception to it: the floor constrains the
    /// HARNESS choosing, never the human assigning. The <c>promptRunners.default</c> pointer does NOT
    /// trigger it — a plan-wide fallback is not a decision about this task, and treating it as
    /// sanction would silently license costly judges across an entire plan.</para>
    ///
    /// <para><b>Candidacy is <see cref="PromptRunnerConfig.ServesTier"/> and nothing else (D22a).</b>
    /// This method CALLS the shared predicate — the same one <see cref="SelectCandidate"/> and
    /// GR2048 call — and never re-spells it. A judge path that counted a block as serving a rung when
    /// the actor path did not is the divergence D22a exists to forbid, and nothing downstream would
    /// notice it.</para>
    /// </summary>
    /// <param name="judge">The judge guardrail. Its <see cref="GuardrailDefinition.Tier"/> is rule 1's
    /// frontmatter tier pin, already parsed onto the definition by <c>PlanLoader</c> — there is
    /// deliberately no second copy of that datum to read.</param>
    /// <param name="runnerPin">The judge prompt's frontmatter <c>runner</c>, if it names one — rule
    /// 1's other spelling. Null = no pin.</param>
    /// <param name="actor">The actor's ALREADY-COMPUTED resolution for this attempt — threaded in,
    /// never re-derived, so the judge is graded against the rung the actor actually ran at. NULL is a
    /// first-class input, not an error: the re-verification path (a human's in-place fix) has no
    /// action attempt and therefore no actor route, and rules 1 and 4 still do real work there.</param>
    /// <param name="config">The plan's run configuration: the registry, <c>tiering.defaultTier</c> and
    /// the <c>tiering.verifier.minTier</c> floor.</param>
    /// <param name="cliDefaultModel">The CLI-level default model — the last rung of the model
    /// fallback, exactly as on the actor side. Null means "let the runner CLI pick its own default".</param>
    /// <returns>The judge's route, and the provenance §12.4's <c>judge {...}</c> object records.</returns>
    public static JudgeResolution ResolveJudge(
        GuardrailDefinition judge,
        string? runnerPin,
        TierResolution? actor,
        RunConfig config,
        string? cliDefaultModel = null) =>
        throw new NotImplementedException(
            "DoR §6.5/§6.5.1 judge resolution — implemented by wave 3 task 02 " +
            "(02-implement-judge-resolution). JudgeResolutionTests pins the contract stated above.");

    /// <summary>
    /// The ONE weakness predicate for the verifier route (§6.5 rule 4 + §4.1's D21a correction),
    /// written once here so the resolver, the #229 advisory and the preflight cannot each grow their
    /// own: <b><c>strength</c> when declared, the provider-kind fallback when not</b> —
    /// <c>kind != "claude"</c> ⇒ weak-UNLESS-declared.
    ///
    /// <para>So a block the operator RANKED is judged by its number and is never guessed at; a block
    /// nobody ranked is guessed weak unless it is a Claude block. The guess is allowed here and
    /// NOWHERE else (never for actor ordering) because on this side being wrong costs one spare
    /// advisory on a rule that is advisory anyway, while on the actor side the same guess would
    /// misroute real spend. A user who dislikes the guess declares <c>strength</c> — which is the
    /// entire point of the axis existing.</para>
    ///
    /// <para>A null block — nothing resolved — counts WEAK, for the same over-warn-at-worst reason:
    /// an unknown verifier is not a vouched-for one.</para>
    /// </summary>
    public static bool IsWeakVerifier(PromptRunnerConfig? block) =>
        block is null || (block.Strength is null && block.Kind != PromptRunnerKind.Claude);

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
