using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 2, wave 3 — DoR <c>docs/plans/17-model-tiering.md</c> §6.5 (the verifier route, rules 1–7)
/// and §6.5.1 (the <c>tiering.verifier.minTier</c> floor). These are the TDD-red tests for
/// <see cref="TierResolver.ResolveJudge"/>; they compile against the stub this task wrote and fail
/// against it, and <c>02-implement-judge-resolution</c> makes them pass.
///
/// <para><b>The failure this whole route exists to prevent</b> is the model-layer analogue of #382: if
/// a task runs on a local 32B and its JUDGE guardrail runs on the same local 32B, a
/// plausible-but-wrong implementation and a plausible-but-wrong "looks good to me" agree, and the run
/// goes green over broken work. "A prompt may propose, only an equal-or-stronger judge may vouch."</para>
///
/// <para><b>Two readings this file settles, because an implementer must pick one and the DoR states
/// them rather than spelling them as code:</b></para>
/// <list type="number">
///   <item><b>"Weak" is an absolute property of a BLOCK, not a threshold on a number.</b> §6.5 rule 4
///     + §4.1's D21a: <c>strength</c> when declared, the provider-kind fallback when not
///     (<c>kind != "claude"</c> ⇒ weak-UNLESS-declared). There is deliberately no numeric threshold
///     anywhere in this design — a harness that decided "strength ≤ 3 is weak" would be inventing
///     capability judgement that belongs to the operator. That predicate is written ONCE, as
///     <see cref="TierResolver.IsWeakVerifier"/>, and <see cref="JudgeResolutionTests"/> uses it as
///     the oracle rather than restating it.</item>
///   <item><b>The judge's rung for a rung-LESS actor is <c>tiering.defaultTier</c>.</b> A pinned or
///     legacy actor resolved no rung of its own (§6.1 items 1/3 leave <see cref="TierResolution.Tier"/>
///     null), so rule 2's "the actor's effective rung" has to mean the rung the task WOULD have
///     resolved at. Without that reading D29 has nothing to be a carve-out of.</item>
/// </list>
///
/// <para><b>Why several fixtures pin the actor rather than letting it tier-resolve.</b> §6.2 sorts an
/// UNSPECIFIED <c>strength</c> LAST, so a block with no declared strength is only ever tier-selected
/// when nothing declared serves that rung — which means a tier-resolved weak actor can only reach a
/// stronger block if that block was excluded for COST. Every other weak actor got there because a
/// human pinned it (<c>action.runner</c>). That is a real property of the design, not an artefact of
/// these fixtures, and it is precisely why D29 — "the human already sanctioned the spend" — is the
/// edge that needed a ruling.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class JudgeResolutionTests
{
    // ── rule 1 — explicit wins ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// §6.5 rule 1. A judge's frontmatter <c>runner</c> or <c>tier</c> pin resolves like an action's
    /// (§6.1) and NO rule below applies.
    ///
    /// <para>The <c>runner</c> half pins a block carrying no <c>routing</c> at all — tier resolution
    /// could not have selected it under any registry, which is what makes "bypassed" observable rather
    /// than a coincidence — and it does so in a config whose <c>minTier</c> floor WOULD have moved an
    /// unpinned judge. §6.5.1: a <c>runner</c> pin names a block directly, so there is no rung for the
    /// floor to raise. (The safety property does not depend on the floor: the advisory compares the
    /// judge's actual strength against the actor's however the route was reached. The floor governs
    /// resolution; the advisory governs reality.)</para>
    ///
    /// <para>The <c>tier</c> half pins a RUNG that differs from the actor's, so a resolver that read
    /// the actor's rung first — the rule-2 path — resolves <c>easy</c> and fails here.</para>
    /// </summary>
    [Fact]
    public void FrontmatterPin_WinsOutright()
    {
        PromptRunnerConfig[] blocks =
        [
            Block("easy-block", "model-easy", [ActionTiers.Easy], strength: 2),
            Block("mid-block", "model-mid", [ActionTiers.Medium], strength: 5),
            Block("hand-picked", "model-hand-picked", tiers: null, strength: 3, effort: "xhigh")
        ];

        RunConfig floored = Registry(blocks, defaultRunner: "easy-block", minTier: ActionTiers.Medium);
        TierResolution actor = TierResolver.SelectCandidate(floored, ActionTiers.Easy);
        Assert.Equal("easy-block", actor.RunnerName);

        JudgeResolution runnerPinned = TierResolver.ResolveJudge(Judge(), "hand-picked", actor, floored);

        Assert.True(runnerPinned.Pinned, "a frontmatter runner pin is rule 1 — explicit wins, and nothing below it applies");
        Assert.Equal("hand-picked", runnerPinned.RunnerName);
        Assert.Equal("model-hand-picked", runnerPinned.Model);
        Assert.Equal("xhigh", runnerPinned.Effort);
        Assert.False(runnerPinned.FloorRaised, "a runner pin names a BLOCK, so there is no rung for the minTier floor to raise (§6.5.1)");
        Assert.NotEqual("mid-block", runnerPinned.RunnerName);

        RunConfig unfloored = Registry(blocks, defaultRunner: "easy-block");
        TierResolution easyActor = TierResolver.SelectCandidate(unfloored, ActionTiers.Easy);

        JudgeResolution tierPinned = TierResolver.ResolveJudge(Judge(tier: ActionTiers.Medium), null, easyActor, unfloored);

        Assert.True(tierPinned.Pinned, "a frontmatter tier pin is rule 1 too — the judge states its own rung");
        Assert.Equal(ActionTiers.Medium, tierPinned.Tier);
        Assert.Equal("mid-block", tierPinned.RunnerName);
        Assert.NotEqual("easy-block", tierPinned.RunnerName);
    }

    // ── rule 2 — the judge's rung is the ACTOR's rung ───────────────────────────────────────────

    /// <summary>
    /// §6.5 rule 2. The judge's rung is the actor's <b>rung</b>, never the actor's <b>strength</b> —
    /// "rung is what <c>routing.tiers</c> is expressed in", and there is no tier→strength mapping in
    /// this design (§4.1).
    ///
    /// <para>The actor here resolves at <c>easy</c> on a block whose <c>strength</c> is 9, so the two
    /// are told apart: a resolver that mapped the actor's strength onto a rung lands on
    /// <c>hard</c> and selects <c>top</c>. In a registry where every strong block also served a strong
    /// rung the two readings would be indistinguishable and this test would prove nothing.</para>
    /// </summary>
    [Fact]
    public void JudgeRung_IsActorsRung_NotActorsStrength()
    {
        RunConfig config = Registry(
        [
            Block("strong-at-easy", "model-strong-at-easy", [ActionTiers.Easy], strength: 9),
            Block("mid", "model-mid", [ActionTiers.Medium], strength: 5),
            Block("top", "model-top", [ActionTiers.Hard], strength: 9)
        ]);

        TierResolution actor = TierResolver.SelectCandidate(config, ActionTiers.Easy);
        Assert.Equal("strong-at-easy", actor.RunnerName);
        Assert.Equal(9, actor.Runner?.Strength);

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal(ActionTiers.Easy, judge.Tier);
        Assert.Equal("strong-at-easy", judge.RunnerName);
        Assert.NotEqual("top", judge.RunnerName);
        Assert.NotEqual("mid", judge.RunnerName);
        Assert.False(judge.Bumped, "the actor declares a strength and runs on a claude block — it is not weak, so no bump was owed");
    }

    // ── rule 3 / D24a — the bump is in STRENGTH, never in TIER ──────────────────────────────────

    /// <summary>
    /// §6.5 rule 3 / <b>D24a</b>, the ambiguity this DoR resolved: the charter said "bumped one tier
    /// ABOVE" in prose and "one strength rank ABOVE" in its diagram, and only the second is coherent —
    /// bumping the TIER means "pretend the work is harder", which drags the judge into a rung nobody
    /// declared for this work.
    ///
    /// <para>So the assertion that matters is <see cref="JudgeResolution.Tier"/>: the judge stays at
    /// the actor's rung and moves along <c>strength</c> WITHIN it. A test that only checked "the judge
    /// is stronger" would pass a tier bump too — <c>opus-hard</c> is stronger than <c>sonnet</c>, and
    /// selecting it is exactly the error D24a exists to forbid.</para>
    ///
    /// <para>The actor is pinned onto a local block because that is the reachable shape of a weak
    /// actor (see the class summary): a human assigned it, and the plan's <c>defaultTier</c> says what
    /// rung the task is.</para>
    /// </summary>
    [Fact]
    public void WeakActor_StrengthBump_KeepsActorsRung()
    {
        RunConfig config = Registry(
            [
                Block("local-pinned", "qwen-32b", tiers: null, kind: PromptRunnerKind.OpenAiCompat),
                Block("sonnet", "claude-sonnet", [ActionTiers.Medium], strength: 5),
                Block("opus-hard", "claude-opus", [ActionTiers.Hard], strength: 9)
            ],
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.Resolve(Action(runner: "local-pinned"), config);
        Assert.True(actor.Pinned);
        Assert.Equal("local-pinned", actor.RunnerName);
        Assert.Null(actor.Runner?.Strength);
        Assert.True(TierResolver.IsWeakVerifier(actor.Runner), "fixture: an unranked non-claude block is weak by the §6.5 rule-4 fallback");

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.Equal("sonnet", judge.RunnerName);
        Assert.NotEqual("opus-hard", judge.RunnerName);
        Assert.Equal(5, judge.Strength);
        Assert.True(judge.Bumped, "the weak-actor strength bump fired, and §12.4 records that fact per attempt");
        Assert.False(judge.Degraded, "a non-costly stronger block was available, so nothing degraded");
    }

    // ── rule 4 — equal-and-strong vs equal-and-weak ─────────────────────────────────────────────

    /// <summary>
    /// §6.5 rule 4, first half: <b>equal needs no bump when the actor is not weak</b> — "Opus judging
    /// Opus is a real check". The actor declares a <c>strength</c> and runs on a <c>claude</c> block,
    /// so neither the declared-strength reading nor the provider-kind fallback calls it weak.
    ///
    /// <para><c>big</c> exists so the assertion discriminates: a resolver that bumps whenever a
    /// stronger block is available selects it, and rule 4's "equal-and-strong needs NO bump" is
    /// exactly the rule that forbids that. Bumping unconditionally would spend a frontier model on
    /// every verdict in a plan — the waste #201 exists to remove, re-introduced at the judge.</para>
    /// </summary>
    [Fact]
    public void EqualAndStrong_NoBump()
    {
        RunConfig config = Registry(
        [
            Block("peer", "claude-sonnet", [ActionTiers.Medium], strength: 5),
            Block("big", "claude-opus", [ActionTiers.Medium], strength: 9)
        ]);

        TierResolution actor = TierResolver.SelectCandidate(config, ActionTiers.Medium);
        Assert.Equal("peer", actor.RunnerName);

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal("peer", judge.RunnerName);
        Assert.NotEqual("big", judge.RunnerName);
        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.Equal(5, judge.Strength);
        Assert.Equal(PromptRunnerKind.Claude, judge.Kind);
        Assert.False(judge.Bumped, "equal-and-strong is a real check — no bump is owed, and 'bumped' must not be always-on");
        Assert.False(judge.Weak, "a ranked claude block is not weak by either half of rule 4");
    }

    /// <summary>
    /// §6.5 rule 4, second half — the one that distinguishes the rule from "never bump on equal":
    /// <b>equal-and-WEAK does bump</b>, because two instances of the same weak model are one blind
    /// spot talking to itself. Qwen judging Qwen is the case; Opus judging Opus (above) is not.
    ///
    /// <para><c>local-peer</c> is a candidate at the judge's rung, so a resolver that simply handed the
    /// judge the actor's own block would find one waiting. The bump must reject it in favour of the
    /// strictly stronger <c>sonnet</c>, and <see cref="JudgeResolution.Bumped"/> must say so — that
    /// datum is what #230-lite reads to price the policy, and the only thing separating "the judge
    /// happens to be a different block" from "the rule fired".</para>
    /// </summary>
    [Fact]
    public void EqualAndWeak_Bumps()
    {
        RunConfig config = Registry(
            [
                Block("local-peer", "qwen-32b", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat),
                Block("sonnet", "claude-sonnet", [ActionTiers.Medium], strength: 4)
            ],
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.Resolve(Action(runner: "local-peer"), config);
        Assert.Equal("local-peer", actor.RunnerName);
        Assert.True(TierResolver.IsWeakVerifier(actor.Runner));

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.NotEqual("local-peer", judge.RunnerName);
        Assert.Equal("sonnet", judge.RunnerName);
        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.Equal(PromptRunnerKind.Claude, judge.Kind);
        Assert.True(judge.Bumped, "the actor is weak, so an EQUAL judge is not enough — the bump fired and must be recorded");
        Assert.False(judge.Weak, "the resolved judge is a ranked claude block, so the advisory datum is false here");
    }

    // ── rule 6 — specialization breaks ties, and ONLY ties ──────────────────────────────────────

    /// <summary>
    /// §6.5 rule 6. Among candidates that ALREADY meet the required strength, <c>planning-reasoning</c>
    /// wins; otherwise §6.2's ascending-strength order (ties by declaration order).
    ///
    /// <para>Two things are asserted, and the second is the one an implementer skips. <c>coder</c> and
    /// <c>planner</c> tie at strength 5 with <c>coder</c> declared FIRST, so declaration order alone
    /// picks <c>coder</c> and the specialization preference is what moves it. And
    /// <c>tiny-planner</c> is <c>planning-reasoning</c> AND the weakest block at the rung: a resolver
    /// that applied the preference BEFORE the strength filter selects it. Specialization "can neither
    /// satisfy nor violate ≥" — it may only break a tie among candidates that already qualify.</para>
    /// </summary>
    [Fact]
    public void Specialization_BreaksTie_AmongEqualCandidates()
    {
        RunConfig config = Registry(
            [
                Block("actor-mid", "claude-actor", tiers: null, strength: 5),
                Block("tiny-planner", "model-tiny-planner", [ActionTiers.Medium], strength: 2,
                    specialization: PromptRunnerSpecialization.PlanningReasoning),
                Block("coder", "model-coder", [ActionTiers.Medium], strength: 5,
                    specialization: PromptRunnerSpecialization.Coding),
                Block("planner", "model-planner", [ActionTiers.Medium], strength: 5,
                    specialization: PromptRunnerSpecialization.PlanningReasoning)
            ],
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.Resolve(Action(runner: "actor-mid"), config);
        Assert.Equal("actor-mid", actor.RunnerName);
        Assert.Equal(5, actor.Runner?.Strength);

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal("planner", judge.RunnerName);
        Assert.NotEqual("coder", judge.RunnerName);
        Assert.NotEqual("tiny-planner", judge.RunnerName);
        Assert.Equal(5, judge.Strength);
        Assert.Equal(ActionTiers.Medium, judge.Tier);
    }

    // ── rule 5 — it degrades, and the run PROCEEDS ──────────────────────────────────────────────

    /// <summary>
    /// §6.5 rule 5, and the asymmetry an implementer gets backwards. The bump obeys the costly floor:
    /// when the only stronger block is <c>costly: true</c> the judge <b>stays at the actor's route</b>,
    /// the advisory fires, and <b>the run proceeds</b>.
    ///
    /// <para>The second half of this test is what makes the first half mean something. In the SAME
    /// registry, the ACTOR asking for a rung whose only block is that costly one <b>halts</b> —
    /// <see cref="TierResolution.NoRoute"/>. Same config, same costly block, opposite responses:
    /// degrade what is advisory, halt what is load-bearing. A test that only checked "no bump
    /// happened" cannot tell a degrade from a halt, and a judge wired to the actor's halt path would
    /// pass it while turning a model-quality opinion into a run-stopping error — which §12.6 forbids
    /// outright.</para>
    ///
    /// <para>Note the actor here needs no pin: <c>opus</c> is excluded from candidacy for cost, so the
    /// unranked local block is the only candidate at <c>medium</c> and tier resolution lands on it by
    /// itself.</para>
    /// </summary>
    [Fact]
    public void OnlyStrongerBlockCostly_DegradesAndProceeds()
    {
        RunConfig config = Registry(
        [
            Block("local-mid", "qwen-32b", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat),
            Block("opus", "claude-opus", [ActionTiers.Medium, ActionTiers.Hard], strength: 9, costly: true)
        ]);

        TierResolution actor = TierResolver.SelectCandidate(config, ActionTiers.Medium);
        Assert.Equal("local-mid", actor.RunnerName);
        Assert.True(TierResolver.IsWeakVerifier(actor.Runner), "fixture: the actor is weak, so a bump IS owed — and cannot be paid");

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.NotEqual("opus", judge.RunnerName);
        Assert.Equal("local-mid", judge.RunnerName);
        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.False(judge.Bumped, "the bump was wanted but refused by the costly floor — 'bumped' says it FIRED, not that it was owed");
        Assert.True(judge.Degraded, "the degrade must be observable: without it the advisory cannot say WHY the judge is no stronger than the work");
        Assert.True(judge.Weak, "the judge stayed on the weak block — this is exactly the condition the #229 advisory reports");
        Assert.Equal("qwen-32b", judge.Model);

        // The run PROCEEDS: a usable route, no exception, nothing for a caller to halt on.
        Assert.NotNull(judge.Runner);

        // The actor side, same registry: 'hard' is declared ONLY by the costly block, so the actor
        // gets no route at all and settles the halt this judge deliberately does not.
        TierResolution actorAtHard = TierResolver.SelectCandidate(config, ActionTiers.Hard);
        Assert.True(actorAtHard.NoRoute, "the ACTOR halts where the judge degrades — the asymmetry is the whole rule");
        Assert.Null(actorAtHard.RunnerName);
    }

    // ── D29 — a pinned costly ACTOR licenses a costly judge; the default pointer does not ───────

    /// <summary>
    /// <b>D29</b> (maintainer, Stage 2 charter review, 2026-08-16). Rule 5 applies the costly floor to
    /// the judge bump unconditionally, which is too broad at one edge: when the ACTOR runs on an
    /// explicitly PINNED <c>costly</c> model, costly spend for this task has already been authorized by
    /// a human, so the judge MAY bump into a <c>costly: true</c> block — no halt, no further prompt.
    /// Consistent with the floor rather than an exception to it: the floor constrains the harness
    /// CHOOSING, never the human ASSIGNING, and here the human has assigned.
    ///
    /// <para>The shape it produces is the one the verifier route exists for — a human who pins a
    /// frontier actor gets a judge strong enough to vouch for it, instead of a weaker judge
    /// rubber-stamping the strongest actor in the run.</para>
    ///
    /// <para>This is the exact registry as <see cref="OnlyStrongerBlockCostly_DegradesAndProceeds"/>
    /// with ONE difference — the actor is pinned onto a costly block — so the two are a minimal pair
    /// over the licence itself. <c>local-mid</c> is unranked, so it cannot satisfy "strictly greater
    /// than the actor"; the only qualifying candidate is the costly one.</para>
    /// </summary>
    [Fact]
    public void D29_PinnedCostlyActor_MayBumpIntoCostly()
    {
        RunConfig config = Registry(
            [
                Block("cloud-frontier", "frontier-preview", tiers: null, kind: PromptRunnerKind.OpenAiCompat, costly: true),
                Block("local-mid", "qwen-32b", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat),
                Block("opus", "claude-opus", [ActionTiers.Medium], strength: 9, costly: true)
            ],
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.Resolve(Action(runner: "cloud-frontier"), config);
        Assert.True(actor.Pinned, "fixture: a human pinned this block — that is the sanction D29 turns on");
        Assert.True(actor.Runner?.Costly, "fixture: and the pinned block is costly, so costly spend is already authorized for this task");

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal("opus", judge.RunnerName);
        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.Equal(9, judge.Strength);
        Assert.True(judge.Bumped, "the bump fired — D29 licensed it into a costly block");
        Assert.False(judge.Degraded, "nothing degraded: the licence made the stronger block reachable");
    }

    /// <summary>
    /// <b>D29's other half — the one that makes it a rule rather than a permission.</b> The
    /// <c>promptRunners.default</c> pointer does NOT trigger it: a plan-wide fallback is not a decision
    /// about this task, and treating it as sanction would silently license costly judges across an
    /// ENTIRE plan.
    ///
    /// <para>The registry names the costly block as <c>default</c> — a config <c>validate</c> merely
    /// WARNS about (GR2051) — and the actor tier-resolves onto the weak local block. A resolver that
    /// implemented D29 as "the actor is somehow associated with a costly block" passes the test above
    /// and fails here; the licence is a PIN and nothing else.</para>
    /// </summary>
    [Fact]
    public void D29_DefaultPointer_DoesNotLicenseIt()
    {
        RunConfig config = Registry(
            [
                Block("local-mid", "qwen-32b", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat),
                Block("opus", "claude-opus", [ActionTiers.Medium], strength: 9, costly: true)
            ],
            defaultRunner: "opus",
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.SelectCandidate(config, ActionTiers.Medium);
        Assert.Equal("local-mid", actor.RunnerName);
        Assert.False(actor.Pinned, "fixture: nothing pinned this task — the costly block is merely the plan-wide default pointer");

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.NotEqual("opus", judge.RunnerName);
        Assert.Equal("local-mid", judge.RunnerName);
        Assert.False(judge.Bumped, "absent a pin, rule 5 stands exactly as written — the bump is refused");
        Assert.True(judge.Degraded, "so this is the ordinary degrade, advisory and all");
    }

    // ── §6.5.1 — the verifier floor RAISES, and never lowers ────────────────────────────────────

    /// <summary>
    /// §6.5.1 (D27, settled 2026-08-12 — it CHANGED the design). <c>tiering.verifier.minTier</c> is
    /// applied AFTER rules 1–3: a result that came out BELOW it is raised to <c>minTier</c> and
    /// re-selected from <c>Candidates(minTier)</c>.
    ///
    /// <para>The control run — the same registry with no floor configured — resolves the judge at
    /// <c>easy</c>. That pairing is the point: the floor is the only difference, so what it did is
    /// visible rather than inferred. "Never verify anything with less than a <c>medium</c> judge,
    /// however trivial the task looked" is a perfectly reachable policy in a purely static v1, because
    /// the judge's tier varies ACROSS tasks even though it cannot move across attempts of one.</para>
    /// </summary>
    [Fact]
    public void MinTierFloor_RaisesTooLow()
    {
        PromptRunnerConfig[] blocks =
        [
            Block("easy-block", "model-easy", [ActionTiers.Easy], strength: 2),
            Block("mid-block", "model-mid", [ActionTiers.Medium], strength: 5)
        ];

        RunConfig unfloored = Registry(blocks);
        TierResolution actor = TierResolver.SelectCandidate(unfloored, ActionTiers.Easy);
        Assert.Equal("easy-block", actor.RunnerName);

        JudgeResolution control = TierResolver.ResolveJudge(Judge(), null, actor, unfloored);
        Assert.Equal(ActionTiers.Easy, control.Tier);
        Assert.Equal("easy-block", control.RunnerName);
        Assert.False(control.FloorRaised, "no floor is configured, so nothing raised anything");

        RunConfig floored = Registry(blocks, minTier: ActionTiers.Medium);
        JudgeResolution raised = TierResolver.ResolveJudge(
            Judge(), null, TierResolver.SelectCandidate(floored, ActionTiers.Easy), floored);

        Assert.Equal(ActionTiers.Medium, raised.Tier);
        Assert.Equal("mid-block", raised.RunnerName);
        Assert.True(raised.FloorRaised, "the floor decided this rung, and §6.5.1 records that so the cost of the policy is attributable to the policy");
    }

    /// <summary>
    /// §6.5.1's asymmetry, and the whole distinction between a FLOOR and a DEFAULT: it only ever
    /// RAISES. A result at or above <c>minTier</c> is untouched.
    ///
    /// <para>Revision 3 of the DoR read the plan-wide key as a default; the maintainer's answer made it
    /// a floor instead, and the difference is not cosmetic — a plan-wide <c>easy</c> default would drag
    /// every judge in the plan DOWN, which is the opposite of what a verifier policy is for. The
    /// <c>easy-block</c> here is weaker, cheaper and would be selected by any implementation that
    /// treated <c>minTier</c> as "the judge's rung".</para>
    /// </summary>
    [Fact]
    public void MinTierFloor_NeverLowersHigher()
    {
        RunConfig config = Registry(
            [
                Block("easy-block", "model-easy", [ActionTiers.Easy], strength: 1),
                Block("hard-block", "model-hard", [ActionTiers.Hard], strength: 8)
            ],
            minTier: ActionTiers.Easy);

        TierResolution actor = TierResolver.SelectCandidate(config, ActionTiers.Hard);
        Assert.Equal("hard-block", actor.RunnerName);

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal(ActionTiers.Hard, judge.Tier);
        Assert.Equal("hard-block", judge.RunnerName);
        Assert.NotEqual("easy-block", judge.RunnerName);
        Assert.False(judge.FloorRaised, "the result was already above the floor, so the floor did nothing — it never selects, it only refuses a result that came out too low");
    }

    // ── D22a — the judge path uses the SHARED candidacy predicate ───────────────────────────────

    /// <summary>
    /// <b>The D22a agreement property for the JUDGE path.</b> Wave 1 already property-tests that the
    /// ACTOR path only ever selects blocks satisfying <see cref="PromptRunnerConfig.ServesTier"/>.
    /// Nothing said the same of the judge path — so a second, slightly different candidacy rule could
    /// grow here and no other test in this wave would notice. That is precisely the divergence D22a
    /// exists to forbid: if the judge path counted a block as serving a rung when <c>validate</c>'s
    /// GR2048 check did not, validation would pass and the judge would run somewhere the config never
    /// sanctioned.
    ///
    /// <para>It is written as a PROPERTY over a candidate space — every rung crossed with every floor
    /// setting, over a registry spanning the shapes that can change the answer (<c>routing</c> absent;
    /// present and serving; <c>costly</c> true/false/absent; <c>strength</c> declared and not; a
    /// non-claude kind; both specializations) — not as a single example. For EVERY block
    /// <see cref="TierResolver.ResolveJudge"/> can select, <c>ServesTier</c> must hold at the rung it
    /// resolved.</para>
    ///
    /// <para>Two results are exempt, and both are exemptions the DoR states rather than holes: a
    /// PINNED judge named its block directly (rule 1 — nothing is pinned in this sweep, so the
    /// property asserts it never happens), and a DEGRADED judge stayed at the ACTOR's route, which the
    /// actor path already vouched for. The degrade branch is therefore checked against the actor's own
    /// block instead of being skipped — otherwise an implementation could satisfy this property by
    /// declaring everything degraded.</para>
    /// </summary>
    [Fact]
    public void JudgeCandidacy_AgreesWith_ServesTier()
    {
        PromptRunnerConfig[] blocks =
        [
            Block("no-routing", "model-no-routing", tiers: null, strength: 6),
            Block("easy-cheap", "model-easy-cheap", [ActionTiers.Easy], strength: 1),
            Block("easy-strong", "model-easy-strong", [ActionTiers.Easy], strength: 7, costly: false),
            Block("mid-local", "model-mid-local", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat),
            Block("mid-planner", "model-mid-planner", [ActionTiers.Medium], strength: 4,
                specialization: PromptRunnerSpecialization.PlanningReasoning),
            Block("mid-coder", "model-mid-coder", [ActionTiers.Medium], strength: 4,
                specialization: PromptRunnerSpecialization.Coding),
            Block("hard-costly", "model-hard-costly", [ActionTiers.Hard], strength: 9, costly: true),
            Block("every-rung", "model-every-rung", [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard], strength: 5)
        ];

        int observed = 0;

        foreach (string? floor in new string?[] { null, ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard })
        {
            RunConfig config = Registry(blocks, defaultRunner: "no-routing", defaultTier: ActionTiers.Medium, minTier: floor);

            foreach (string rung in ActionTiers.All)
            {
                TierResolution actor = TierResolver.SelectCandidate(config, rung);
                if (actor.NoRoute)
                {
                    continue;
                }

                JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);
                observed++;

                Assert.False(judge.Pinned, $"rung '{rung}', floor '{floor ?? "none"}': nothing pinned this judge, so rule 1 must not have decided it");

                if (judge.Degraded)
                {
                    Assert.Equal(actor.RunnerName, judge.RunnerName);
                    Assert.True(
                        actor.Runner is { } stayed && stayed.ServesTier(actor.Tier!),
                        $"rung '{rung}', floor '{floor ?? "none"}': a degraded judge stays at the ACTOR's route, and that route was itself chosen by the shared predicate");
                    continue;
                }

                PromptRunnerConfig selected = Assert.IsType<PromptRunnerConfig>(judge.Runner);
                string resolvedRung = Assert.IsType<string>(judge.Tier);

                Assert.True(
                    selected.ServesTier(resolvedRung),
                    $"rung '{rung}', floor '{floor ?? "none"}': ResolveJudge selected '{selected.Name}' at rung '{resolvedRung}', which ServesTier says is NOT a candidate — the judge path has grown its own candidacy rule, which is the D22a failure");
                Assert.True(
                    selected.Costly is not true,
                    $"rung '{rung}', floor '{floor ?? "none"}': nothing pinned a costly block here, so the harness must never have chosen '{selected.Name}'");
            }
        }

        Assert.True(observed >= 9, $"the property swept only {observed} resolutions — a vacuous property proves nothing");
    }

    // ── rule 7, and the two inputs the wiring task depends on ───────────────────────────────────

    /// <summary>
    /// §6.5 rule 7 — an implementation ruling, because an implementer will get it backwards otherwise:
    /// <c>guardrailOverrides</c> compose with the <b>resolved JUDGE's</b> block, not the actor's, since
    /// overrides are a per-block verdict profile (permissions / tools / turns).
    ///
    /// <para>This test pins the SHAPE that makes the rule fall out for free: the resolved block is
    /// exposed on the result, so the caller runs <see cref="PromptRunnerConfig.EffectiveSettings"/> on
    /// it. Applying the ACTOR block's overrides to a bumped judge mis-profiles every one of them —
    /// right model, wrong turn budget and wrong permission mode — and nothing else in the system
    /// notices. The actor's block here carries deliberately different overrides so the wrong one is
    /// distinguishable from the right one.</para>
    /// </summary>
    [Fact]
    public void ResolvedJudgeBlock_IsExposed_SoGuardrailOverridesComposeWithIt()
    {
        RunConfig config = Registry(
            [
                Block("local-actor", "qwen-32b", tiers: null, kind: PromptRunnerKind.OpenAiCompat,
                    guardrailOverrides: new PromptRunnerOverrides { MaxTurns = 99, PermissionMode = "acceptEdits" }),
                Block("sonnet", "claude-sonnet", [ActionTiers.Medium], strength: 4,
                    guardrailOverrides: new PromptRunnerOverrides { MaxTurns = 12, PermissionMode = "plan" })
            ],
            defaultTier: ActionTiers.Medium);

        TierResolution actor = TierResolver.Resolve(Action(runner: "local-actor"), config);
        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor, config);

        Assert.Equal("sonnet", judge.RunnerName);

        PromptRunnerConfig block = Assert.IsType<PromptRunnerConfig>(judge.Runner);
        PromptRunnerSettings settings = block.EffectiveSettings(isGuardrail: true);

        Assert.Equal(12, settings.MaxTurns);
        Assert.Equal("plan", settings.PermissionMode);
        Assert.NotEqual(99, settings.MaxTurns);
    }

    /// <summary>
    /// The re-verification path (a human's in-place fix) has no action attempt and therefore <b>no
    /// actor route</b> — but a null route does NOT mean "no judge". With no actor rung to key off,
    /// rule 1's pin, §6.5.1's floor and the <c>default</c> block still decide a real route, and the
    /// attempt that a model graded records which one.
    ///
    /// <para>Here the floor SUPPLIES the rung rather than raising one, which is the same mechanism
    /// stated from the other end: <c>minTier</c> is the rung a judge may never end up below, and "no
    /// rung at all" is below every rung.</para>
    /// </summary>
    [Fact]
    public void NullActorRoute_WithFloor_ResolvesTheFloorsRung()
    {
        RunConfig config = Registry(
            [
                Block("fallback", "model-fallback", tiers: null, strength: 1),
                Block("mid-block", "model-mid", [ActionTiers.Medium], strength: 3)
            ],
            defaultRunner: "fallback",
            minTier: ActionTiers.Medium);

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor: null, config);

        Assert.Equal(ActionTiers.Medium, judge.Tier);
        Assert.Equal("mid-block", judge.RunnerName);
        Assert.True(judge.FloorRaised, "with no rung to raise, the floor SUPPLIED one — a judge may never end up below it");
    }

    /// <summary>
    /// Invariant 7's runtime half, at the judge: with no pin, no actor rung, no <c>defaultTier</c> and
    /// no floor, there is nothing tiering-specific to do and the judge runs on the <c>default</c>
    /// pointer's block — <b>exactly today's behaviour</b>. The verifier route is inert when tiering is
    /// unconfigured, which is what makes this whole epic byte-compatible for a single-model user.
    /// </summary>
    [Fact]
    public void NullActorRoute_WithNoRungAnywhere_FallsBackToTheDefaultBlock()
    {
        RunConfig config = Registry(
            [
                Block("fallback", "model-fallback", tiers: null, strength: 1),
                Block("mid-block", "model-mid", [ActionTiers.Medium], strength: 3)
            ],
            defaultRunner: "fallback");

        JudgeResolution judge = TierResolver.ResolveJudge(Judge(), null, actor: null, config);

        Assert.Equal("fallback", judge.RunnerName);
        Assert.Equal("model-fallback", judge.Model);
        Assert.Null(judge.Tier);
        Assert.False(judge.Bumped);
        Assert.False(judge.FloorRaised);
    }

    /// <summary>
    /// <see cref="JudgeResolution.Weak"/> is the datum the #229 advisory reads, and it must agree with
    /// the ONE shared predicate rather than being computed a second way here. Task 09/10's advisory
    /// asks "is this judge weaker than its actor, or equal-and-weak?", and it can only answer that
    /// without re-deriving anything if the resolution reports the weakness of the block it actually
    /// selected.
    /// </summary>
    [Fact]
    public void Weak_AgreesWithTheSharedWeaknessPredicate_ForTheResolvedBlock()
    {
        RunConfig strongSide = Registry([Block("peer", "claude-sonnet", [ActionTiers.Medium], strength: 5)]);
        JudgeResolution strong = TierResolver.ResolveJudge(
            Judge(), null, TierResolver.SelectCandidate(strongSide, ActionTiers.Medium), strongSide);
        Assert.Equal(TierResolver.IsWeakVerifier(strong.Runner), strong.Weak);
        Assert.False(strong.Weak);

        RunConfig weakSide = Registry(
            [Block("local-only", "qwen-32b", [ActionTiers.Medium], kind: PromptRunnerKind.OpenAiCompat)]);
        JudgeResolution weak = TierResolver.ResolveJudge(
            Judge(), null, TierResolver.SelectCandidate(weakSide, ActionTiers.Medium), weakSide);
        Assert.Equal(TierResolver.IsWeakVerifier(weak.Runner), weak.Weak);
        Assert.True(weak.Weak, "an unranked non-claude block is weak-unless-declared — the case the advisory exists to report");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One PROMPT judge guardrail. <paramref name="tier"/> is rule 1's frontmatter <c>tier</c> pin as
    /// <c>PlanLoader</c> already folds it onto the definition — there is deliberately no second copy of
    /// that datum on the frontmatter record, so this is the only site to read it from. The <c>runner</c>
    /// pin is the other rule-1 spelling and lives in the prompt's own frontmatter, so it is passed to
    /// <see cref="TierResolver.ResolveJudge"/> separately — the same two values
    /// <c>GuardrailRunner</c> already holds at the call site.
    /// </summary>
    private static GuardrailDefinition Judge(string? tier = null) => new()
    {
        Name = "02-verdict",
        Path = "/plan/tasks/01-task/guardrails/02-verdict.prompt.md",
        Kind = ActionKind.Prompt,
        Tier = tier
    };

    /// <summary>
    /// One prompt action — used only to produce a realistic ACTOR resolution through the shipped §6.1
    /// entry point, so no test asserts against an actor state the resolver could not actually return.
    /// </summary>
    private static ActionDefinition Action(string? tier = null, string? runner = null, string? model = null) => new()
    {
        Path = "/plan/tasks/01-task/action.prompt.md",
        Kind = ActionKind.Prompt,
        Tier = tier,
        Runner = runner,
        Model = model
    };

    /// <summary>
    /// One <c>promptRunners</c> block. <paramref name="tiers"/> <c>null</c> means NO <c>routing</c> key
    /// at all — the "never a tier target" shape a pinned block has — which is deliberately different
    /// from an empty tier list. <paramref name="costly"/> stays <c>bool?</c> so the tri-state
    /// absent/false distinction survives into the fixture.
    /// </summary>
    private static PromptRunnerConfig Block(
        string name,
        string? model,
        IReadOnlyList<string>? tiers = null,
        int? strength = null,
        bool? costly = null,
        string? effort = null,
        PromptRunnerKind kind = PromptRunnerKind.Claude,
        PromptRunnerSpecialization specialization = PromptRunnerSpecialization.Unspecified,
        PromptRunnerOverrides? guardrailOverrides = null) => new()
        {
            Name = name,
            Command = "claude",
            Settings = new PromptRunnerSettings { Model = model },
            Kind = kind,
            Strength = strength,
            Costly = costly,
            Effort = effort,
            Specialization = specialization,
            Routing = tiers is null ? null : new PromptRunnerRouting { Tiers = tiers },
            GuardrailOverrides = guardrailOverrides
        };

    /// <summary>
    /// A <see cref="RunConfig"/> holding <paramref name="blocks"/> IN THE ORDER GIVEN (declaration
    /// order is the §6.2 tie-break key, and rule 6's specialization preference has to beat it), with
    /// <paramref name="defaultRunner"/> as the <c>promptRunners.default</c> pointer.
    ///
    /// <para>The <c>tiering</c> block is present only when a <paramref name="defaultTier"/> or a
    /// <paramref name="minTier"/> is asked for: an absent block is a single-model user's config, and
    /// the verifier half must be inert there (Invariant 7).</para>
    /// </summary>
    private static RunConfig Registry(
        IReadOnlyList<PromptRunnerConfig> blocks,
        string? defaultRunner = null,
        string? defaultTier = null,
        string? minTier = null)
    {
        var runners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal);
        foreach (PromptRunnerConfig block in blocks)
        {
            runners[block.Name] = block;
        }

        return new RunConfig
        {
            Version = 1,
            PromptRunnerNames = new HashSet<string>(runners.Keys, StringComparer.Ordinal),
            PromptRunners = runners,
            DefaultPromptRunner = defaultRunner,
            Tiering = defaultTier is null && minTier is null
                ? null
                : new TieringConfig
                {
                    DefaultTier = defaultTier,
                    Verifier = minTier is null ? null : new TieringVerifierConfig { MinTier = minTier }
                }
        };
    }
}
