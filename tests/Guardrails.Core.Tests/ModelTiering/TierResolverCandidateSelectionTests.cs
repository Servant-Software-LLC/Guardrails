using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 2, wave 1 — DoR <c>docs/plans/17-model-tiering.md</c> §6.2 candidate selection (issue #226).
/// These are the TDD-red tests for <see cref="TierResolver.SelectCandidate"/>; they compile against the
/// stub and fail against it, and <c>02-implement-candidate-selection</c> makes them pass.
///
/// <para>What §6.2 asks for, and what each of these pins:</para>
/// <list type="number">
///   <item><b>One candidacy predicate</b> — <c>Candidates(R)</c> = <c>routing</c> present ∧
///     <c>R ∈ routing.tiers</c> ∧ <c>costly</c> is not <c>true</c>, which is exactly
///     <see cref="PromptRunnerConfig.ServesTier"/> (D22a).</item>
///   <item><b>Ascending <c>strength</c></b> — the WEAKEST block declared capable of the rung wins;
///     unspecified strength sorts last; ties break by declaration order. There is no numeric
///     tier→strength mapping anywhere.</item>
///   <item><b>The costly floor</b> — excluded at its own rung AND at a climbed-to stronger rung.</item>
///   <item><b>The climb</b> — an empty candidate set climbs to the nearest STRONGER rung; never
///     weaker.</item>
///   <item><b><c>no-route</c></b> — nothing at-or-above yields a distinguishable RESULT.</item>
///   <item><b>The D28 binding ceiling is REPORTED</b>, not merely obeyed.</item>
/// </list>
///
/// <para><b>Why the D22a proof is a property test and not a grep.</b> Earlier drafts of this plan tried
/// to enforce the single-predicate rule by grepping the implementation for <c>ServesTier</c>. That was
/// measured and abandoned: an inlined copy spelled a different way evades it, and a correct
/// implementation that only mentions the name in a comment false-reds.
/// <see cref="CandidateSet_AgreesWithServesTier_ForEveryBlockAndRung"/> catches drift however it is
/// spelled — an inlined copy that is equivalent today passes, and fails the moment it drifts, which is
/// the only moment at which D22a matters.</para>
///
/// <para><b>Agreement is asserted through the SELECTION path, not through a test-only accessor.</b> A
/// <c>Candidates(...)</c> accessor could itself call <see cref="PromptRunnerConfig.ServesTier"/> while
/// <c>SelectCandidate</c> inlined its own copy — the property would pass and D22a would still be
/// violated. Driving the shipped entry point closes that hole.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class TierResolverCandidateSelectionTests
{
    // ── 1. Ascending strength — the weakest capable model wins ──────────────────────────────────

    /// <summary>
    /// A <c>hard</c> task gets the WEAKEST block the operator declared capable of <c>hard</c>, not the
    /// strongest. The blocks are declared strongest-first precisely so a resolver that returns "the
    /// first block that serves the rung" — or that maps a tier to a strength band — fails here. There
    /// is no numeric tier→strength mapping in this design: eligibility is the operator's statement,
    /// and <c>strength</c> only orders what they already declared eligible.
    /// </summary>
    [Fact]
    public void AscendingStrength_WeakestCapableBlockWins()
    {
        RunConfig registry = Registry(
            Block("opus", "claude-opus", ["hard"], strength: 9),
            Block("sonnet", "claude-sonnet", ["hard"], strength: 5),
            Block("haiku", "claude-haiku", ["hard"], strength: 2));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Hard);

        Assert.Equal("haiku", resolution.RunnerName);
        Assert.Equal("claude-haiku", resolution.Model);
        Assert.Equal(ActionTiers.Hard, resolution.Tier);
        Assert.False(resolution.Climbed, "the rung had its own candidate, so nothing was climbed");
        Assert.False(resolution.NoRoute, "three blocks serve 'hard' — this must not be a no-route");
    }

    /// <summary>
    /// The two remaining halves of the order: an UNSPECIFIED <c>strength</c> sorts LAST (a block nobody
    /// ranked must not outrank one they did), and equal strengths break by DECLARATION order.
    ///
    /// <para>The third registry is the degenerate case that keeps the rule total: when every candidate
    /// is unranked, "unspecified last" cannot decide, so declaration order does — which makes the
    /// ordering a total order rather than one with an undefined region.</para>
    /// </summary>
    [Fact]
    public void UnspecifiedStrength_SortsLast_AndTiesBreakByDeclarationOrder()
    {
        RunConfig unranked = Registry(
            Block("unranked", "model-unranked", ["medium"]),
            Block("ranked", "model-ranked", ["medium"], strength: 7));

        Assert.Equal("ranked", TierResolver.SelectCandidate(unranked, ActionTiers.Medium).RunnerName);

        RunConfig tied = Registry(
            Block("declared-first", "model-first", ["medium"], strength: 4),
            Block("declared-second", "model-second", ["medium"], strength: 4));

        Assert.Equal("declared-first", TierResolver.SelectCandidate(tied, ActionTiers.Medium).RunnerName);

        RunConfig allUnranked = Registry(
            Block("first-unranked", "model-a", ["medium"]),
            Block("second-unranked", "model-b", ["medium"]));

        Assert.Equal("first-unranked", TierResolver.SelectCandidate(allUnranked, ActionTiers.Medium).RunnerName);
    }

    // ── 2. The costly floor — tri-state at the schema, two-state at the predicate ───────────────

    /// <summary>
    /// <c>costly</c> is TRI-STATE in the schema and TWO-state here: ABSENT (<c>null</c>, "not stated")
    /// and an explicit <c>false</c> BOTH serve; only an explicit <c>true</c> excludes.
    ///
    /// <para>The absent case is the load-bearing one — an un-annotated registry (every registry written
    /// before this feature existed) must stay routable. A resolver that treated "not stated" as
    /// "assume costly" would make the whole installed base unservable, and one that treated it as
    /// distinct-from-false would need a third behaviour that does not exist.</para>
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CostlyTriState_OnlyAnExplicitTrueExcludes(bool? costly, bool expectedToServe)
    {
        RunConfig registry = Registry(Block("solo", "model-solo", ["medium"], strength: 3, costly: costly));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Medium);

        Assert.True(
            expectedToServe == !resolution.NoRoute,
            $"costly={(costly is null ? "absent" : costly.ToString())} should {(expectedToServe ? "serve" : "be excluded")}, but NoRoute={resolution.NoRoute}");
        Assert.Equal(expectedToServe ? "solo" : null, resolution.RunnerName);
    }

    /// <summary>
    /// The floor at the block's OWN rung. The costly block here is also the WEAKEST, so ascending
    /// strength alone would select it — which is what isolates the costly exclusion from the ordering
    /// rule. The stronger, non-costly block is served instead.
    /// </summary>
    [Fact]
    public void CostlyBlock_IsExcludedAtItsOwnRung_EvenWhenItIsTheWeakest()
    {
        RunConfig registry = Registry(
            Block("cheap-to-run-but-costly", "model-costly", ["medium"], strength: 1, costly: true),
            Block("ordinary", "model-ordinary", ["medium"], strength: 8));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Medium);

        Assert.Equal("ordinary", resolution.RunnerName);
        Assert.NotEqual("cheap-to-run-but-costly", resolution.RunnerName);
    }

    /// <summary>
    /// The floor at a CLIMBED-TO rung — the half a one-rung test cannot see. Nothing serves
    /// <c>easy</c>, so resolution climbs; the nearest stronger rung (<c>medium</c>) is declared ONLY by
    /// a <c>costly: true</c> block, so the climb must pass straight over it to <c>hard</c>.
    ///
    /// <para>A resolver that filters <c>costly</c> only at the REQUESTED rung passes every test above
    /// and selects the costly block here. That is the exact defect this test exists for: §6.2 excludes
    /// costly blocks at every rung — their own, a climbed-to stronger rung, a v2 ladder escalation and
    /// a §6.5 judge bump — with no override anywhere in the design.</para>
    /// </summary>
    [Fact]
    public void CostlyBlock_IsExcludedAtTheClimbedToStrongerRung()
    {
        RunConfig registry = Registry(
            Block("costly-mid", "model-costly-mid", ["medium"], strength: 1, costly: true),
            Block("plain-hard", "model-plain-hard", ["hard"], strength: 8));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Easy);

        Assert.NotEqual("costly-mid", resolution.RunnerName);
        Assert.Equal("plain-hard", resolution.RunnerName);
        Assert.Equal(ActionTiers.Hard, resolution.Tier);
        Assert.True(resolution.Climbed, "easy had no candidate, so this resolution climbed past medium to hard");
    }

    // ── 3. The climb, and the floor under it ───────────────────────────────────────────────────

    /// <summary>
    /// An empty candidate set at rung R climbs to the NEAREST stronger rung with a non-empty set — and
    /// the result RECORDS that it climbed, so wave 2 can log it loudly instead of absorbing it
    /// silently. "Nearest" matters: <c>medium</c> is served here even though <c>hard</c> also has a
    /// candidate, because overshooting spends more than asked for no reason.
    /// </summary>
    [Fact]
    public void EmptyCandidateSet_ClimbsToNearestStrongerRung_AndRecordsTheClimb()
    {
        RunConfig registry = Registry(
            Block("mid", "model-mid", ["medium"], strength: 3),
            Block("top", "model-top", ["hard"], strength: 9));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Easy);

        Assert.Equal("mid", resolution.RunnerName);
        Assert.Equal(ActionTiers.Medium, resolution.Tier);
        Assert.Equal(ActionTiers.Easy, resolution.RequestedTier);
        Assert.True(resolution.Climbed, "the climb must be recorded, not absorbed");
        Assert.False(resolution.NoRoute, "a stronger rung had a candidate, so this is a climb and not a no-route");
    }

    /// <summary>
    /// The never-weaker-than-asked floor: a <c>hard</c> task with only an <c>easy</c> block available
    /// does NOT get the easy block. Routing DOWN a rung is never automatic — in v1 the only lever below
    /// this floor is halt-and-edit-config.
    /// </summary>
    [Fact]
    public void Resolution_NeverRoutesToAWeakerRung()
    {
        RunConfig registry = Registry(Block("only-easy", "model-only-easy", ["easy"], strength: 1));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Hard);

        Assert.Null(resolution.RunnerName);
        Assert.True(resolution.NoRoute, "'hard' must settle no-route rather than quietly dropping to the easy block");
    }

    // ── 4. no-route — a distinguishable RESULT, not an exception ────────────────────────────────

    /// <summary>
    /// No candidate at the requested rung OR any stronger one yields the <c>no-route</c> condition: a
    /// RESULT the caller can inspect, not a silent fallback and not an exception it cannot tell from a
    /// bug. Wave 2 settles it needs-human with an actionable "register a provider serving tier ≥ R".
    ///
    /// <para>The registry deliberately holds a routable block at a WEAKER rung and a block with no
    /// <c>routing</c> at all: neither is reachable by tier resolution, and a resolver that reached for
    /// either would be inventing a fallback the design does not have.</para>
    /// </summary>
    [Fact]
    public void NoCandidateAtOrAboveRung_YieldsNoRoute_RatherThanThrowing()
    {
        RunConfig registry = Registry(
            Block("pinned-only", "model-pinned-only", tiers: null, strength: 9),
            Block("weak", "model-weak", ["easy"], strength: 1));

        TierResolution? captured = null;
        Exception? failure = Record.Exception(() => captured = TierResolver.SelectCandidate(registry, ActionTiers.Hard));

        Assert.Null(failure);
        TierResolution resolution = Assert.IsType<TierResolution>(captured);
        Assert.True(resolution.NoRoute, "nothing serves 'hard' or stronger, so this is the no-route condition");
        Assert.Null(resolution.Runner);
        Assert.Null(resolution.RunnerName);
        Assert.Null(resolution.Model);
    }

    // ── 5. D28 — the binding ceiling is REPORTED, not just obeyed ───────────────────────────────

    /// <summary>
    /// The D28 datum. When a STRONGER block was excluded ONLY because it is <c>costly: true</c>, the
    /// resolution says so and NAMES it — so wave 2 can emit a loud warning on re-attempt instead of
    /// leaving the operator to tune prompts against a constraint they cannot see.
    ///
    /// <para>The control registry is the same shape with the flag cleared, and it selects the SAME
    /// block: identical selection, different datum. That pairing is the point — a test that only
    /// checked the costly block was not selected would pass on a resolver that obeys the floor
    /// silently, and wave 2 would have nothing to warn from. This changes what is LOGGED, never what is
    /// SELECTED.</para>
    ///
    /// <para>The datum exists nowhere but the candidacy sweep (it is definitionally
    /// <c>DeclaresTier ∧ ¬ServesTier</c>), so wave 2 cannot re-derive it without a second copy of the
    /// predicate — which is precisely the duplication D22a forbids.</para>
    /// </summary>
    [Fact]
    public void CostlyCeiling_IsReported_WhenAStrongerBlockWasExcludedOnlyBecauseCostly()
    {
        RunConfig bound = Registry(
            Block("sonnet", "claude-sonnet", ["hard"], strength: 3),
            Block("opus", "claude-opus", ["hard"], strength: 9, costly: true));

        TierResolution resolution = TierResolver.SelectCandidate(bound, ActionTiers.Hard);

        Assert.Equal("sonnet", resolution.RunnerName);
        Assert.True(resolution.CostlyCeilingBound, "a stronger block was excluded only because it is costly — the ceiling BOUND, and D28 requires that be reported");
        Assert.Contains("opus", resolution.CostlyCeilingBlocks, StringComparer.Ordinal);

        RunConfig unbound = Registry(
            Block("sonnet", "claude-sonnet", ["hard"], strength: 3),
            Block("opus", "claude-opus", ["hard"], strength: 9, costly: false));

        TierResolution control = TierResolver.SelectCandidate(unbound, ActionTiers.Hard);

        Assert.Equal("sonnet", control.RunnerName);
        Assert.False(control.CostlyCeilingBound, "nothing was excluded for cost here, so the ceiling did not bind — the datum must not be always-on");
        Assert.Empty(control.CostlyCeilingBlocks);
    }

    /// <summary>
    /// The other face of the same datum, and the runtime twin of GR2048's second cause: the rung IS
    /// declared, but only by <c>costly: true</c> blocks. That settles <c>no-route</c> — the harness
    /// neither reaches for the costly block nor drops to a weaker rung (D26's deliberate asymmetry) —
    /// and the ceiling datum is what lets the needs-human message say "the blocks are sitting right
    /// there in your config, pin them or clear the flag" instead of "no block serves tier 'hard'",
    /// which would send the operator hunting for a block they already have.
    /// </summary>
    [Fact]
    public void CostlyCeiling_IsReported_WhenTheRungHasNoNonCostlyCandidateAtAll()
    {
        RunConfig registry = Registry(
            Block("opus", "claude-opus", ["hard"], strength: 9, costly: true),
            Block("haiku", "claude-haiku", ["easy"], strength: 1));

        TierResolution resolution = TierResolver.SelectCandidate(registry, ActionTiers.Hard);

        Assert.True(resolution.NoRoute, "the only block declaring 'hard' is costly, so there is no candidate at or above it");
        Assert.Null(resolution.RunnerName);
        Assert.True(resolution.CostlyCeilingBound, "the ceiling is WHY this rung has no route — a no-route that does not say so is unactionable");
        Assert.Contains("opus", resolution.CostlyCeilingBlocks, StringComparer.Ordinal);
    }

    // ── 6. D22a — the agreement property, the most important test in this file ──────────────────

    /// <summary>
    /// <b>The D22a agreement property.</b> <see cref="PromptRunnerConfig.ServesTier"/> is the ONE
    /// candidacy predicate, and <c>validate</c>'s GR2048 check already uses it. If validation and the
    /// runtime resolver ever disagree about which blocks serve a rung, validation passes and every task
    /// at that rung dies at run time on <c>no-route</c> — which is why the DoR calls the single shared
    /// predicate "a correctness requirement, not tidiness".
    ///
    /// <para>The registry spans every shape that can change the answer: <c>routing</c> absent;
    /// <c>routing</c> present and serving the rung; present and NOT serving it; <c>costly</c> true /
    /// false / absent; <c>strength</c> present and absent. For every (block, rung) pair the block must
    /// be served AT that rung exactly when <c>ServesTier</c> says it is a candidate.</para>
    ///
    /// <para>"Served AT the rung" is the observable form of "is a candidate", and the qualifier is
    /// load-bearing: a block that serves only a STRONGER rung is legitimately selected for a weaker
    /// request via the climb, and that is not candidacy at the requested rung. Excluding climbs is what
    /// keeps this an iff rather than an implication.</para>
    /// </summary>
    [Fact]
    public void CandidateSet_AgreesWithServesTier_ForEveryBlockAndRung()
    {
        PromptRunnerConfig[] blocks =
        [
            Block("no-routing", "model-no-routing", tiers: null, strength: 4, costly: false),
            Block("easy-only", "model-easy-only", ["easy"], strength: 1),
            Block("medium-and-hard", "model-medium-and-hard", ["medium", "hard"], strength: 5, costly: false),
            Block("costly-hard", "model-costly-hard", ["hard"], strength: 9, costly: true),
            Block("costly-easy", "model-costly-easy", ["easy"], strength: 2, costly: true),
            Block("unranked-medium", "model-unranked-medium", ["medium"]),
            Block("every-rung", "model-every-rung", ["easy", "medium", "hard"], strength: 3, costly: false)
        ];

        RunConfig whole = Registry(blocks);

        foreach (string rung in ActionTiers.All)
        {
            foreach (PromptRunnerConfig block in blocks)
            {
                TierResolution solo = TierResolver.SelectCandidate(Registry(block), rung);
                bool servedAtRung = !solo.NoRoute
                    && !solo.Climbed
                    && string.Equals(solo.Tier, rung, StringComparison.Ordinal)
                    && string.Equals(solo.RunnerName, block.Name, StringComparison.Ordinal);

                Assert.True(block.ServesTier(rung) == servedAtRung, $"block '{block.Name}' at rung '{rung}': ServesTier={block.ServesTier(rung)} but the resolver served-it-at-that-rung={servedAtRung} — validate and the resolver have drifted apart, which is the D22a failure");
            }

            TierResolution selected = TierResolver.SelectCandidate(whole, rung);
            bool anyServes = blocks.Any(b => b.ServesTier(rung));
            bool resolvedAtRung = !selected.NoRoute
                && !selected.Climbed
                && string.Equals(selected.Tier, rung, StringComparison.Ordinal);

            Assert.True(anyServes == resolvedAtRung, $"rung '{rung}': some block ServesTier={anyServes} but the resolver resolved at that rung={resolvedAtRung}");

            if (anyServes)
            {
                PromptRunnerConfig winner = Assert.Single(blocks, b => string.Equals(b.Name, selected.RunnerName, StringComparison.Ordinal));
                Assert.True(winner.ServesTier(rung), $"rung '{rung}': the resolver selected '{winner.Name}', which ServesTier says is not a candidate");
            }
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>promptRunners</c> block. <paramref name="tiers"/> <c>null</c> means NO <c>routing</c> key
    /// at all — the "never a tier target" shape — which is deliberately different from an empty tier
    /// list. <paramref name="costly"/> stays <c>bool?</c> so the absent/false distinction survives into
    /// the fixture.
    /// </summary>
    private static PromptRunnerConfig Block(
        string name,
        string model,
        IReadOnlyList<string>? tiers = null,
        int? strength = null,
        bool? costly = null,
        string? effort = null) => new()
        {
            Name = name,
            Command = "claude",
            Settings = new PromptRunnerSettings { Model = model },
            Strength = strength,
            Costly = costly,
            Effort = effort,
            Routing = tiers is null ? null : new PromptRunnerRouting { Tiers = tiers }
        };

    /// <summary>
    /// A <see cref="RunConfig"/> whose <c>promptRunners</c> map holds <paramref name="blocks"/> IN THE
    /// ORDER GIVEN — the tie-break key is declaration order, and this is the only representation of it
    /// the model has (it is the same enumeration <c>PlanValidator</c>'s GR2048 check walks).
    /// </summary>
    private static RunConfig Registry(params PromptRunnerConfig[] blocks)
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
            PromptRunners = runners
        };
    }
}
