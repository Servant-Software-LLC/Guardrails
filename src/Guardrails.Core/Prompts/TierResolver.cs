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
        string? cliDefaultModel = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(config);

        // ── 1. Explicit wins ─────────────────────────────────────────────────────────────────────
        // Rule 1 wins outright and stops every rule below it, the §6.5.1 floor included: the floor is
        // stated over "the rung from (2)–(3)", and a pin never produces one of those. A runner pin is
        // reported exactly as §6.1 item 1 reports an action's — the name that was ASKED for, even when
        // the registry declares no such block (a residual validation already rejects), because a
        // diagnostic able to print the name nobody declared beats one that can only say "null".
        if (runnerPin is not null)
        {
            PromptRunnerConfig? pinned = NamedBlock(config, runnerPin);

            return new JudgeResolution
            {
                Pinned = true,
                Runner = pinned,
                RunnerName = runnerPin,
                Kind = pinned?.Kind,
                Model = pinned?.Settings.Model ?? cliDefaultModel,
                Effort = pinned?.Effort,
                Strength = pinned?.Strength,
                Weak = IsWeakVerifier(pinned)
            };
        }

        // A frontmatter TIER pin names a rung rather than a block, so it resolves through the one
        // candidate selector §6.1 item 2 uses. Pinned or not, exactly one method in this file filters
        // candidates (D22a) — a "pinned rung" path with its own candidacy rule would be the divergence
        // wearing a different hat.
        if (judge.Tier is { } pinnedRung)
        {
            TierResolution byRung = SelectCandidate(config, pinnedRung);

            return byRung.Runner is { } pinnedBlock
                ? Selected(pinnedBlock, byRung.Tier, cliDefaultModel) with { Pinned = true }
                // The judge-side of no-route. The ACTOR halts here; a judge may not (§12.6), so an
                // unservable pinned rung falls back to the default pointer exactly as an unconfigured
                // plan does and carries the advisory instead. CostlyCeilingBound is the resolver's own
                // answer to "was a block refused for cost", which IS rule 5's degrade condition — asking
                // it again here would be a second copy of a datum the sweep already computed.
                : DefaultPointer(config, cliDefaultModel) with
                {
                    Pinned = true,
                    Degraded = byRung.CostlyCeilingBound
                };
        }

        // ── 2. The judge's rung is the ACTOR's rung ──────────────────────────────────────────────
        // The rung, never the actor's strength. A PINNED or LEGACY actor resolved no rung of its own,
        // so the rung falls back to the plan-wide default — the rung this task WOULD have resolved at,
        // filtered exactly as the actor path filters it (an unrecognized default does not propagate,
        // because GR2043 has already declared it non-propagating at validate time).
        string? rung = actor?.Tier ?? PropagatableDefaultTier(config);
        PromptRunnerConfig? actorBlock = actor?.Runner;

        // ── 3. The weak-actor bump — in STRENGTH, at that FIXED rung (D24a) ──────────────────────
        // A weak actor needs a STRICTLY stronger judge (equal-and-weak is one blind spot talking to
        // itself); anything else needs only an equal one (Opus judging Opus is a real check). With no
        // actor there is no bump to want: rule 3 is the weak-ACTOR rule, and the re-verification path
        // has no actor to be weak.
        bool bump = actor is not null && IsWeakVerifier(actorBlock);
        int required = actorBlock?.Strength ?? UnrankedStrength;

        // D29, and its narrowness is the rule. A PINNED costly actor is a human authorizing costly spend
        // for THIS task, which licenses a costly judge bump. `Pinned` is the whole test: the
        // `promptRunners.default` pointer is a plan-wide fallback rather than a decision about this task,
        // and reading it as sanction would silently license costly judges across an entire plan.
        bool licensed = actor is { Pinned: true } && actorBlock?.Costly is true;

        IReadOnlyList<PromptRunnerConfig> registry = [.. config.PromptRunners.Values];

        (PromptRunnerConfig? Winner, bool CostRefused) pick = Pick(registry, rung, required, bump, licensed);
        string? resolvedTier = rung;
        bool floorRaised = false;
        bool degraded = pick.CostRefused;

        // ── 4. The floor — it RAISES, and only raises (§6.5.1) ───────────────────────────────────
        // A result at or above minTier is UNTOUCHED, which is the entire difference between a floor and
        // a default: a plan-wide `easy` default would drag every judge in the plan down, while a
        // plan-wide `easy` floor does nothing at all. "No rung at all" is below every rung, so there the
        // floor SUPPLIES one rather than raising one — the same mechanism stated from the other end.
        if (VerifierFloor(config) is { } floor && IsBelowRung(rung, floor))
        {
            (PromptRunnerConfig? Winner, bool CostRefused) raised = Pick(registry, floor, required, bump, licensed);

            if (raised.Winner is not null)
            {
                pick = raised;
                resolvedTier = floor;
                floorRaised = true;
                degraded = false;
            }
            else
            {
                // §6.5.1: an unmeetable floor DEGRADES to the best result from steps 2–3 and fires the
                // advisory. It does NOT climb to a stronger rung (that spends more to satisfy a
                // preference), it does not reach the costly block, and it is not an error — an
                // unsatisfiable ACTOR tier is GR2048, an unsatisfiable VERIFIER floor is an advisory line.
                degraded |= raised.CostRefused;
            }
        }

        if (pick.Winner is { } winner)
        {
            return Selected(winner, resolvedTier, cliDefaultModel) with
            {
                // Bumped says the bump FIRED, not that one was wanted: every selection above applied the
                // strict comparison when `bump` was set, so reaching a winner IS the bump landing.
                Bumped = bump,
                FloorRaised = floorRaised,
                // A winner AND a degrade is one case and only one: the §6.5.1 floor that could not be met
                // without a costly block, which keeps the steps-2–3 result and fires the same advisory.
                // The route is real, so nothing halts — but dropping the flag here would silently lose the
                // only report of a policy the operator configured and did not get.
                Degraded = degraded
            };
        }

        // ── 5. Nothing qualified: stay at the ACTOR's route, and the run PROCEEDS ────────────────
        // The asymmetry, and the one an implementer gets backwards: the actor HALTS here (NoRoute), the
        // judge DEGRADES — degrade what is advisory, halt what is load-bearing. There is deliberately no
        // halt path to reuse, because §12.6 forbids a verifier condition from ever failing a build.
        //
        // `degraded` stays reserved for the COSTLY refusal, which is a different fact from "there is
        // simply nothing stronger at this rung" and the advisory has to tell them apart: only the first
        // can say "your judge is no stronger than the work BECAUSE the stronger block is reserved".
        if (actor is not null)
        {
            return new JudgeResolution
            {
                Runner = actorBlock,
                RunnerName = actor.RunnerName,
                Kind = actorBlock?.Kind,
                // The actor's ROUTE, not merely its block: an `action.model` pin overrode the block's
                // model string for the actor, and "stays at the actor's route" means the same model.
                Model = actor.Model ?? actorBlock?.Settings.Model ?? cliDefaultModel,
                Effort = actor.Effort ?? actorBlock?.Effort,
                Tier = resolvedTier,
                Strength = actorBlock?.Strength,
                Weak = IsWeakVerifier(actorBlock),
                Degraded = degraded
            };
        }

        // No actor and no rung anywhere: Invariant 7's runtime half at the judge. Nothing tiering-specific
        // to do, so the judge runs on the `default` pointer's block — EXACTLY today's behaviour, which is
        // what makes the verifier route inert for a user who never opted into any of this.
        return DefaultPointer(config, cliDefaultModel) with { Degraded = degraded };
    }

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
    /// The strength an UNRANKED block counts as when the §6.5 rule-3 comparison is made — and the one
    /// place this design reads a null <c>strength</c> as a NUMBER rather than as a sort position.
    ///
    /// <para>§6.2 sorts unspecified strength LAST (<c>int.MaxValue</c>) so a block nobody ranked never
    /// outranks one they did. That is an ORDERING convention and it cannot be reused as a comparison
    /// value here: at <c>MaxValue</c> an unranked block would be "strictly greater" than every ranked
    /// one and equal to another unranked one, which inverts rule 4 exactly — Qwen would be a licensed
    /// judge for Opus, and Qwen judging Qwen would satisfy the bump. Zero gives the two readings the
    /// design actually states: an unranked block can never satisfy a bump, and a ranked one always
    /// out-strengthens an unranked actor.</para>
    /// </summary>
    private const int UnrankedStrength = 0;

    /// <summary>
    /// One §6.5 selection at ONE rung: the qualifying block, plus whether the costly floor is the reason
    /// there is none.
    ///
    /// <para>The second value is what separates rule 5's DEGRADE ("a stronger block exists and the
    /// harness may not have it") from "there is simply nothing stronger at this rung". Both leave the
    /// judge where it was, but only the first is a thing to tell the operator about, so the question is
    /// asked HERE — once, next to the selection that failed — rather than re-derived by an advisory that
    /// would need its own copy of the candidacy predicate to ask it.</para>
    ///
    /// <para>A null <paramref name="rung"/> is not an error: it is "no rung to select at" (a pinned or
    /// legacy actor in a plan with no <c>defaultTier</c>, or no actor at all), and it yields no winner
    /// and no refusal — nothing was refused, because nothing was asked.</para>
    /// </summary>
    private static (PromptRunnerConfig? Winner, bool CostRefused) Pick(
        IReadOnlyList<PromptRunnerConfig> registry,
        string? rung,
        int required,
        bool strict,
        bool licensed)
    {
        if (rung is null)
        {
            return (null, false);
        }

        if (BestJudge(registry, rung, required, strict, licensed) is { } winner)
        {
            return (winner, false);
        }

        // Would a block have qualified if the costly floor were lifted? Asked ONLY when the licence is
        // absent — with it the pass above already looked at those blocks, so a second look could only
        // report a refusal that never happened.
        return (null, !licensed && BestJudge(registry, rung, required, strict, licensed: true) is not null);
    }

    /// <summary>
    /// The §6.5 winner at <paramref name="rung"/>: the weakest block that CAN judge this work, or null
    /// when none can.
    ///
    /// <para><b>Candidacy is <see cref="PromptRunnerConfig.ServesTier"/> and nothing else (D22a)</b> —
    /// CALLED, never re-spelled, so this path and GR2048 cannot drift. <paramref name="licensed"/> is
    /// D29 and widens candidacy by exactly one step, to
    /// <see cref="PromptRunnerConfig.DeclaresTier"/> — the OTHER shared predicate, the same pair GR2048
    /// computes. Neither branch re-reads <c>routing</c> or <c>costly</c> here, which is the property that
    /// keeps a costly block unreachable to the harness by construction rather than by care.</para>
    ///
    /// <para>Rule 6 leads the ORDER but never the FILTER: the strength test runs first, so specialization
    /// can neither satisfy nor violate ≥ and a specialized-but-too-weak block is never chosen. Among the
    /// blocks that already qualify, <c>planning-reasoning</c> is preferred and §6.2's ascending strength
    /// decides the rest; both sorts are STABLE, so full ties keep declaration order.</para>
    /// </summary>
    private static PromptRunnerConfig? BestJudge(
        IReadOnlyList<PromptRunnerConfig> registry,
        string rung,
        int required,
        bool strict,
        bool licensed) =>
        registry
            .Where(runner =>
                (runner.ServesTier(rung) || (licensed && runner.DeclaresTier(rung)))
                && Qualifies(runner.Strength ?? UnrankedStrength, required, strict))
            .OrderBy(runner => runner.Specialization == PromptRunnerSpecialization.PlanningReasoning ? 0 : 1)
            .ThenBy(runner => runner.Strength ?? int.MaxValue)
            .FirstOrDefault();

    /// <summary>
    /// The §6.5 rule 3/4 strength test: <b>strictly</b> greater when the actor is weak, equal-or-greater
    /// otherwise. The whole of rule 4 is this one bool — equal-and-strong needs no bump, equal-and-weak
    /// does.
    /// </summary>
    private static bool Qualifies(int strength, int required, bool strict) =>
        strict ? strength > required : strength >= required;

    /// <summary>
    /// A <see cref="JudgeResolution"/> over a block this resolver SELECTED — the §12.4 <c>judge {...}</c>
    /// object, assembled in one place so no caller and no later rule assembles a second, partial copy.
    /// </summary>
    private static JudgeResolution Selected(PromptRunnerConfig block, string? tier, string? cliDefaultModel) =>
        new()
        {
            Runner = block,
            RunnerName = block.Name,
            Kind = block.Kind,
            Model = block.Settings.Model ?? cliDefaultModel,
            Effort = block.Effort,
            Tier = tier,
            Strength = block.Strength,
            Weak = IsWeakVerifier(block)
        };

    /// <summary>
    /// The <c>promptRunners.default</c> pointer's block as a judge route — today's behaviour, which is
    /// what an unconfigured plan (Invariant 7) and an unservable pinned rung both fall back to. No rung,
    /// so no <see cref="JudgeResolution.Tier"/>.
    /// </summary>
    private static JudgeResolution DefaultPointer(RunConfig config, string? cliDefaultModel)
    {
        PromptRunnerConfig? block = NamedBlock(config, config.DefaultPromptRunner);

        return new JudgeResolution
        {
            Runner = block,
            RunnerName = config.DefaultPromptRunner,
            Kind = block?.Kind,
            Model = block?.Settings.Model ?? cliDefaultModel,
            Effort = block?.Effort,
            Strength = block?.Strength,
            Weak = IsWeakVerifier(block)
        };
    }

    /// <summary>
    /// The §6.5.1 floor as it actually reaches a judge: only a RECOGNIZED token is a floor, deliberately
    /// the same filter <see cref="PropagatableDefaultTier"/> applies to the plan-wide default and for the
    /// same reason — GR2043 reports an unrecognized token at validate time, and fabricating a rung out of
    /// one here would raise every judge in the plan onto a rung the validator said does not exist. Null =
    /// no <c>tiering</c> block, no <c>verifier</c> sub-block, no <c>minTier</c>, or a token off the ladder.
    /// </summary>
    private static string? VerifierFloor(RunConfig config) =>
        ActionTiers.IsRecognized(config.Tiering?.Verifier?.MinTier) ? config.Tiering!.Verifier!.MinTier : null;

    /// <summary>
    /// True when <paramref name="rung"/> sits BELOW <paramref name="floor"/> on the difficulty ladder —
    /// the one comparison §6.5.1 turns on. A null (or off-ladder) rung is below every rung, which is how
    /// "the floor SUPPLIES a rung where resolution had none" falls out of the same test that raises one.
    /// </summary>
    private static bool IsBelowRung(string? rung, string floor) => RungIndex(rung) < RungIndex(floor);

    /// <summary>
    /// The rung's position on <see cref="ActionTiers.All"/> (ascending difficulty), or -1 when it is not
    /// on the ladder at all — null, or a token bound verbatim from a manifest that GR2043 rejects.
    /// </summary>
    private static int RungIndex(string? rung)
    {
        for (int i = 0; i < ActionTiers.All.Count; i++)
        {
            if (string.Equals(ActionTiers.All[i], rung, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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
        int index = RungIndex(tier);

        return index < 0 ? [] : [.. ActionTiers.All.Skip(index)];
    }
}
