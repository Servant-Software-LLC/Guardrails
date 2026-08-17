using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 2, wave 1 — DoR <c>docs/plans/17-model-tiering.md</c> §6.1 precedence (issue #226). These are
/// the TDD-red tests for <see cref="TierResolver.Resolve(ActionDefinition, RunConfig, string?)"/>; they
/// compile against the stub task 01 declared and fail against it, and
/// <c>04-implement-resolution-precedence</c> makes them pass.
///
/// <para>§6.1 is a three-way decision, and the whole file exists to keep the three branches
/// DISTINGUISHABLE rather than merely to check that each one can be reached:</para>
/// <list type="number">
///   <item><b>Full pin</b> — <c>action.runner</c> / <c>action.model</c> bypasses tier resolution
///     ENTIRELY, and is the sanctioned route to a <c>costly</c> block (the floor constrains what the
///     HARNESS may choose, never what a human may assign).</item>
///   <item><b>Tier resolution</b> — effective tier = <c>action.tier</c> ?? <c>tiering.defaultTier</c>,
///     routed through <see cref="TierResolver.SelectCandidate"/>. <c>action.effort</c> ALONE is NOT a
///     bypass: it overrides the RESOLVED route's effort.</item>
///   <item><b>Legacy fallback</b> — ONLY when there is no effective tier at all (D30).</item>
/// </list>
///
/// <para><b>D30 is why half of this file is negative.</b> Through revision 4 item 3 read "no effective
/// tier, <i>or no block serves it</i>", while §6.2/D26 says that second half HALTS — one condition with
/// two opposite sanctioned behaviours, and an implementer could satisfy either reading while failing the
/// other's test. Revision 5 severed the disjunction in favour of the halt: <b>legacy is the no-RUNG
/// path, <c>no-route</c> is the no-CANDIDATE path, and nothing is both.</b>
/// <see cref="EffectiveTier_WithNoServingBlock_SettlesNoRoute_AndNeverTheRunnersModel"/> is the test
/// that stops the old reading coming back the first time someone reads item 3 on its own.</para>
///
/// <para><b>What this file deliberately does NOT assert.</b> §12.4's <c>tierSource</c> journal field is
/// assembled in wave 2, and its missing input — whether the rung came from <c>action.tier</c> or
/// <c>tiering.defaultTier</c> — is restored separately as <c>ActionDefinition.TierOrigin</c> by the
/// <c>05/06</c> pair, which owns those tests. What D31 puts HERE is the third enum value: a full pin is
/// the producer of <c>tierSource: "override"</c>, which is a PRECEDENCE rule, so
/// <see cref="PinnedResolution_IsDistinguishableFromTierResolvedAndLegacy"/> asserts its observable half
/// — a pinned attempt is recognisable as pinned, with no rung resolved — rather than the journal string
/// wave 2 writes.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class TierResolverPrecedenceTests
{
    // ── 1. The full pin — §6.1 item 1 ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>action.runner</c> selects a named block and bypasses tier resolution ENTIRELY.
    ///
    /// <para>The pinned block carries NO <c>routing</c> key, so tier resolution could not have selected
    /// it under any registry — which is what makes "bypassed" observable rather than a coincidence. The
    /// config meanwhile has everything resolution needs (a <c>defaultTier</c> and a block serving it), so
    /// a resolver that consulted the tier first would return <c>cheap</c> and fail here.</para>
    ///
    /// <para><see cref="TierResolution.RequestedTier"/> and <see cref="TierResolution.Tier"/> are BOTH
    /// null on a pinned resolution: no rung was resolved, which is the fact §12.4 records as
    /// "<c>provenance.tier</c> absent, <c>tierSource: override</c>".</para>
    /// </summary>
    [Fact]
    public void FullRunnerPin_SelectsTheNamedBlock_AndBypassesTierResolution()
    {
        RunConfig config = Registry(
            [
                Block("cheap", "model-cheap", [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard], strength: 1),
                Block("pinned", "model-pinned", strength: 7)
            ],
            defaultRunner: "cheap",
            defaultTier: ActionTiers.Hard);

        TierResolution resolution = TierResolver.Resolve(Action(runner: "pinned"), config);

        Assert.True(resolution.Pinned, "action.runner is a full pin — §6.1 item 1 decided this resolution");
        Assert.Equal("pinned", resolution.RunnerName);
        Assert.Equal("pinned", resolution.Runner?.Name);
        Assert.Equal("model-pinned", resolution.Model);
        Assert.True(resolution.RequestedTier is null, "a pin resolves no rung, so nothing was ASKED of the ladder");
        Assert.True(resolution.Tier is null, "a pin resolves no rung, so nothing was SERVED by the ladder");
        Assert.False(resolution.Legacy, "the pin decided this, not the no-rung legacy path");
        Assert.False(resolution.NoRoute, "a pin cannot no-route — it named its own block");
        Assert.False(resolution.Climbed, "nothing was resolved, so nothing climbed");
    }

    /// <summary>
    /// <c>action.model</c> is the other half of the pin, and it overrides the model STRING without
    /// changing the block: §6.1 item 1's own parenthetical — "a raw <c>action.model</c> pin overrides the
    /// model string but not the block, so the pinned actor's <c>strength</c>/<c>kind</c> still come from
    /// its block" — is what the §6.5 judge rule depends on even for a pinned actor.
    ///
    /// <para>So the block stays the one today's two-level fallback would have used (<c>action.runner</c>
    /// ?? the <c>promptRunners.default</c> pointer) and is NOT the block tier resolution would have
    /// picked — the registry deliberately holds a stronger-serving <c>tier-target</c> so the two answers
    /// differ.</para>
    /// </summary>
    [Fact]
    public void FullModelPin_OverridesTheModelString_ButNotTheBlock()
    {
        RunConfig config = Registry(
            [
                Block("default-block", "model-default", strength: 4),
                Block("tier-target", "model-tier-target", [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard], strength: 1)
            ],
            defaultRunner: "default-block",
            defaultTier: ActionTiers.Medium);

        TierResolution resolution = TierResolver.Resolve(Action(model: "claude-named-by-hand"), config);

        Assert.True(resolution.Pinned, "action.model is a full pin, exactly as action.runner is");
        Assert.Equal("claude-named-by-hand", resolution.Model);
        Assert.Equal("default-block", resolution.RunnerName);
        Assert.NotEqual("tier-target", resolution.RunnerName);
        Assert.True(resolution.RequestedTier is null, "a model pin resolves no rung either");
        Assert.True(resolution.Tier is null, "a model pin resolves no rung either");
        Assert.False(resolution.Legacy, "the pin decided this — tiering.defaultTier is configured, so the no-rung path was never open");
    }

    /// <summary>
    /// A pin is the SANCTIONED route to a <c>costly</c> block, and this is the test that says so.
    ///
    /// <para>The costly floor (D22) has no override, no <c>--force</c> and no autonomy dial — but it is a
    /// floor on HARNESS autonomy: "the floor constrains the harness's choices, never the human's". A
    /// resolver that ran the candidacy predicate over a pinned block would exclude <c>frontier</c> and
    /// either fall to <c>workhorse</c> or no-route, and both are wrong: the human already named the
    /// model, which is precisely what charter Decision 3 permits.</para>
    /// </summary>
    [Fact]
    public void PinnedCostlyBlock_IsUsed_BecauseTheFloorBindsTheHarnessNotTheHuman()
    {
        RunConfig config = Registry(
            [
                Block("frontier", "model-frontier", [ActionTiers.Hard], strength: 9, costly: true),
                Block("workhorse", "model-workhorse", [ActionTiers.Hard], strength: 3)
            ],
            defaultRunner: "workhorse",
            defaultTier: ActionTiers.Hard);

        TierResolution resolution = TierResolver.Resolve(Action(runner: "frontier"), config);

        Assert.True(resolution.Pinned, "a human naming a costly block is a pin, not a candidacy question");
        Assert.Equal("frontier", resolution.RunnerName);
        Assert.Equal("model-frontier", resolution.Model);
        Assert.NotEqual("workhorse", resolution.RunnerName);
        Assert.False(resolution.NoRoute, "the costly floor must not turn an explicit human assignment into a no-route");
        Assert.False(resolution.CostlyCeilingBound, "nothing was EXCLUDED for cost here — the costly block is the one that ran");
    }

    /// <summary>
    /// A pin does not silently coexist with a tier: where both are present the PIN wins and the tier is
    /// dead weight (which <c>validate</c> warns about, per §6.1's GR-warning — the warning is validation's
    /// job and is asserted there, not here). What matters at the resolver is the precedence itself, and
    /// that no rung is resolved even though one was written.
    ///
    /// <para>Both spellings of the pin are exercised against the same registry, because a resolver that
    /// special-cased only <c>runner</c> would let a tier out-rank an <c>action.model</c> pin.</para>
    /// </summary>
    [Fact]
    public void FullPin_WinsOverACoexistingTier_AndStillResolvesNoRung()
    {
        RunConfig config = Registry(
            [
                Block("cheap", "model-cheap", [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard], strength: 1),
                Block("pinned", "model-pinned", strength: 7)
            ],
            defaultRunner: "cheap");

        TierResolution runnerPin = TierResolver.Resolve(Action(tier: ActionTiers.Easy, runner: "pinned"), config);

        Assert.True(runnerPin.Pinned, "the pin out-ranks the tag the task also carries");
        Assert.Equal("pinned", runnerPin.RunnerName);
        Assert.True(runnerPin.RequestedTier is null, "the tier is dead weight the pin overrode — it must not be recorded as a rung that was asked for");
        Assert.True(runnerPin.Tier is null, "no rung was served");
        Assert.False(runnerPin.Climbed, "nothing resolved, so nothing climbed");

        TierResolution modelPin = TierResolver.Resolve(Action(tier: ActionTiers.Hard, model: "claude-named-by-hand"), config);

        Assert.True(modelPin.Pinned, "an action.model pin out-ranks a coexisting tier too");
        Assert.Equal("claude-named-by-hand", modelPin.Model);
        Assert.True(modelPin.RequestedTier is null, "the coexisting tier resolved nothing here either");
    }

    /// <summary>
    /// <b>D31's observable half.</b> Revision 5 made a full pin the producer of §12.4's
    /// <c>tierSource: "override"</c>, and the input that makes that journal line writable is exactly this:
    /// after resolution, a pinned attempt must be TELLABLE from a tier-resolved one and from a legacy one.
    ///
    /// <para>This asserts the classification, not the journal string — wave 2 writes the string, and
    /// duplicating it here would make two places to fix. All three branches are exercised so the property
    /// is a partition rather than three independent flags: exactly one of pinned / tier-resolved / legacy
    /// describes each resolution, and the two flag fields plus <see cref="TierResolution.RequestedTier"/>
    /// are enough to say which.</para>
    /// </summary>
    [Fact]
    public void PinnedResolution_IsDistinguishableFromTierResolvedAndLegacy()
    {
        RunConfig tiered = Registry(
            [
                Block("workhorse", "model-workhorse", [ActionTiers.Medium], strength: 3),
                Block("named", "model-named", strength: 7)
            ],
            defaultRunner: "workhorse",
            defaultTier: ActionTiers.Medium);

        TierResolution pinned = TierResolver.Resolve(Action(runner: "named"), tiered);
        Assert.True(pinned.Pinned, "pinned: §6.1 item 1 decided it");
        Assert.False(pinned.Legacy, "a pin is not the legacy path");
        Assert.True(pinned.RequestedTier is null, "a pin asks the ladder for nothing");

        TierResolution resolved = TierResolver.Resolve(Action(), tiered);
        Assert.False(resolved.Pinned, "tier-resolved: nothing was pinned, so wave 2 must not journal tierSource 'override'");
        Assert.False(resolved.Legacy, "an effective tier exists, so this is not the legacy path");
        Assert.Equal(ActionTiers.Medium, resolved.RequestedTier);
        Assert.Equal("workhorse", resolved.RunnerName);

        RunConfig untiered = tiered with { Tiering = null };

        TierResolution legacy = TierResolver.Resolve(Action(), untiered);
        Assert.False(legacy.Pinned, "legacy: nothing was pinned here either");
        Assert.True(legacy.Legacy, "no rung anywhere — §6.1 item 3 decided it");
        Assert.True(legacy.RequestedTier is null, "the legacy path asks the ladder for nothing");
    }

    // ── 2. action.effort is NOT a bypass — §6.1 item 2, the F4 correction ───────────────────────

    /// <summary>
    /// <b>The correction the DoR calls out explicitly.</b> <c>action.effort</c> mirrors
    /// <c>action.model</c>'s SHAPE but not its BYPASS: <c>{ "tier": "medium", "effort": "xhigh" }</c> means
    /// "route by tier, but think hard". Tier resolution still SELECTS the block; the override is applied
    /// to the RESOLVED route's effort.
    ///
    /// <para>The registry makes the difference visible from either side. The block resolution must select
    /// carries its own <c>effort</c>, so the override has something to displace; the
    /// <c>promptRunners.default</c> pointer is a DIFFERENT block with no <c>routing</c> at all, so a
    /// resolver that treated <c>effort</c> as a pin (or as a reason to skip resolution) returns
    /// <c>never-a-target</c> and fails.</para>
    ///
    /// <para>The control leg — the same action WITHOUT <c>action.effort</c> — is what makes this a proof
    /// that the override is applied to the resolved route rather than a coincidence that
    /// <see cref="TierResolution.Effort"/> happens to hold "xhigh": same block, same model, different
    /// effort.</para>
    /// </summary>
    [Fact]
    public void ActionEffort_AloneIsNotABypass_TierResolutionStillSelectsTheBlock()
    {
        RunConfig config = Registry(
            [
                Block("workhorse", "model-workhorse", [ActionTiers.Medium], strength: 3, effort: "medium"),
                Block("never-a-target", "model-never-a-target", strength: 1)
            ],
            defaultRunner: "never-a-target");

        TierResolution resolution = TierResolver.Resolve(Action(tier: ActionTiers.Medium, effort: "xhigh"), config);

        Assert.False(resolution.Pinned, "action.effort ALONE is not a full pin — only action.model/action.runner are");
        Assert.False(resolution.Legacy, "an effective tier exists, so resolution owns the outcome");
        Assert.Equal("workhorse", resolution.RunnerName);
        Assert.Equal("model-workhorse", resolution.Model);
        Assert.NotEqual("never-a-target", resolution.RunnerName);
        Assert.Equal(ActionTiers.Medium, resolution.RequestedTier);
        Assert.Equal(ActionTiers.Medium, resolution.Tier);
        Assert.Equal("xhigh", resolution.Effort);

        TierResolution control = TierResolver.Resolve(Action(tier: ActionTiers.Medium), config);

        Assert.Equal("workhorse", control.RunnerName);
        Assert.Equal("medium", control.Effort);
    }

    /// <summary>
    /// The other side of the same rule: an <c>action.effort</c> with NO tier anywhere does not conjure a
    /// pin OR a rung. It is still the untagged case, so §6.1 item 3 decides it — which is what keeps
    /// "effort is not a bypass" from being quietly reinterpreted as "effort is a bypass that skips
    /// straight to the runner's model".
    ///
    /// <para>The override's VALUE is asserted in
    /// <see cref="ActionEffort_AloneIsNotABypass_TierResolutionStillSelectsTheBlock"/>, where the DoR
    /// states it ("applied to the RESOLVED route's effort"). This leg pins the PRECEDENCE branch only.</para>
    /// </summary>
    [Fact]
    public void ActionEffort_WithNoTierAnywhere_IsStillNeitherAPinNorARung()
    {
        RunConfig config = Registry([Block("solo", "model-solo", strength: 2)], defaultRunner: "solo");

        TierResolution resolution = TierResolver.Resolve(Action(effort: "xhigh"), config, cliDefaultModel: "cli-default");

        Assert.False(resolution.Pinned, "effort is not a pin, whether or not a tier is present");
        Assert.True(resolution.Legacy, "no action.tier and no tiering.defaultTier — this is the untagged case");
        Assert.Equal("solo", resolution.RunnerName);
        Assert.Equal("model-solo", resolution.Model);
        Assert.True(resolution.RequestedTier is null, "there was no rung to ask for");
    }

    // ── 3. The effective tier — action.tier ?? tiering.defaultTier ──────────────────────────────

    /// <summary>
    /// Effective tier = <c>action.tier</c> ?? <c>tiering.defaultTier</c>, and the plan-wide default is
    /// consulted ONLY when the action carries no tier of its own.
    ///
    /// <para>Both legs run against the SAME config, so the only variable is the action: a resolver that
    /// preferred the plan default, or that merged the two, disagrees on one leg or the other. The two
    /// rungs are served by different blocks so the selected route reports which tier actually won —
    /// asserting <see cref="TierResolution.RequestedTier"/> alone would pass a resolver that recorded one
    /// rung and routed by another.</para>
    /// </summary>
    [Fact]
    public void EffectiveTier_PrefersTheActionTier_AndConsultsDefaultTierOnlyWhenAbsent()
    {
        RunConfig config = Registry(
            [
                Block("easy-block", "model-easy", [ActionTiers.Easy], strength: 1),
                Block("hard-block", "model-hard", [ActionTiers.Hard], strength: 9)
            ],
            defaultRunner: "easy-block",
            defaultTier: ActionTiers.Easy);

        TierResolution own = TierResolver.Resolve(Action(tier: ActionTiers.Hard), config);

        Assert.Equal(ActionTiers.Hard, own.RequestedTier);
        Assert.Equal(ActionTiers.Hard, own.Tier);
        Assert.Equal("hard-block", own.RunnerName);
        Assert.False(own.Legacy, "the action's own tier is an effective tier");

        TierResolution inherited = TierResolver.Resolve(Action(), config);

        Assert.Equal(ActionTiers.Easy, inherited.RequestedTier);
        Assert.Equal(ActionTiers.Easy, inherited.Tier);
        Assert.Equal("easy-block", inherited.RunnerName);
        Assert.False(inherited.Legacy, "tiering.defaultTier IS an effective tier — it must not fall through to legacy");
    }

    // ── 4. Legacy — §6.1 item 3, and Invariant 7's two fixtures ─────────────────────────────────

    /// <summary>
    /// <b>Invariant 7, fixture (a) — the easy half.</b> No <c>routing</c> block anywhere, no
    /// <c>tiering</c> block, no <c>action.tier</c>: resolution is exactly today's two-level fallback,
    /// <c>promptRunners.&lt;name&gt;.model</c> else the CLI default, with no tier-resolution activity at
    /// all.
    ///
    /// <para>Both rungs of that fallback are exercised, because "else the CLI default" is the half a
    /// single-model user with an un-modelled runner block actually lands on.</para>
    ///
    /// <para>This fixture alone is NOT coverage of Invariant 7 — see
    /// <see cref="RoutingEnabled_ZeroTagPlan_ResolvesViaLegacyPath"/>, which is the case an implementer
    /// gets wrong.</para>
    /// </summary>
    [Fact]
    public void NoRoutingConfiguredAnywhere_ResolvesViaLegacyPath()
    {
        RunConfig config = Registry([Block("claude", "claude-sonnet")], defaultRunner: "claude");

        TierResolution resolution = TierResolver.Resolve(Action(), config, cliDefaultModel: "cli-default");

        Assert.True(resolution.Legacy, "no rung exists anywhere, so §6.1 item 3 decides — exactly today");
        Assert.Equal("claude", resolution.RunnerName);
        Assert.Equal("claude-sonnet", resolution.Model);
        Assert.False(resolution.Pinned, "nothing was pinned");
        Assert.True(resolution.RequestedTier is null, "no rung was asked for");
        Assert.True(resolution.Tier is null, "no rung was served");
        Assert.False(resolution.Climbed, "there was nothing to climb from");
        Assert.False(resolution.NoRoute, "the legacy path always has an answer — no-route is the no-CANDIDATE path (D30)");

        RunConfig noModel = Registry([Block("claude", model: null)], defaultRunner: "claude");

        TierResolution viaCliDefault = TierResolver.Resolve(Action(), noModel, cliDefaultModel: "cli-default");

        Assert.True(viaCliDefault.Legacy, "still the untagged case");
        Assert.Equal("cli-default", viaCliDefault.Model);
    }

    /// <summary>
    /// <b>Invariant 7, fixture (b) — the DoR's own named fixture, and the one that matters.</b> The
    /// registry HAS <c>routing</c> blocks; the action carries NO tier and there is NO
    /// <c>tiering.defaultTier</c> (the <c>tiering</c> block is present but empty, which is the harsher
    /// spelling). It must STILL resolve via the legacy path with ZERO tier-resolution activity, because
    /// §6.1 item 3's trigger is "the action has no rung to resolve" — which "holds whether or not
    /// <c>routing</c> blocks are configured elsewhere in the registry".
    ///
    /// <para>This is the case an implementer gets wrong, because "routing is configured" reads like "so
    /// route". Fixture (a) cannot catch it: with no <c>routing</c> anywhere, a resolver that routes
    /// whenever routing exists is indistinguishable from a correct one. That is exactly why §4's
    /// activation is PLAN-scoped rather than CONFIG-scoped — "a routing-enabled config with a zero-tag
    /// plan does nothing tiering-specific" — and it is what makes Invariant 7 provable.</para>
    ///
    /// <para><b>"Zero tier-resolution activity" is observable here, not merely asserted.</b> The registry
    /// holds a <c>costly: true</c> block declaring <c>hard</c>, so ANY candidacy sweep at any rung would
    /// set <see cref="TierResolution.CostlyCeilingBound"/> and name it. It staying false is the
    /// fingerprint that the sweep never ran — alongside a null requested/served rung and a route that is
    /// the <c>default</c> pointer rather than the cheapest serving block.</para>
    /// </summary>
    [Fact]
    public void RoutingEnabled_ZeroTagPlan_ResolvesViaLegacyPath()
    {
        RunConfig config = Registry(
            [
                Block("legacy-default", "model-legacy-default", strength: 4),
                Block("cheap", "model-cheap", [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard], strength: 1),
                Block("frontier", "model-frontier", [ActionTiers.Hard], strength: 9, costly: true)
            ],
            defaultRunner: "legacy-default") with
        {
            Tiering = new TieringConfig()
        };

        TierResolution resolution = TierResolver.Resolve(Action(), config, cliDefaultModel: "cli-default");

        Assert.True(resolution.Legacy, "routing blocks exist, but this task has no rung — §6.1 item 3 still decides");
        Assert.Equal("legacy-default", resolution.RunnerName);
        Assert.Equal("model-legacy-default", resolution.Model);
        Assert.NotEqual("cheap", resolution.RunnerName);
        Assert.True(resolution.RequestedTier is null, "no rung was asked for, however much routing the config declares");
        Assert.True(resolution.Tier is null, "no rung was served");
        Assert.False(resolution.Pinned, "nothing was pinned");
        Assert.False(resolution.Climbed, "no candidate set was ever built, so nothing climbed");
        Assert.False(resolution.NoRoute, "an untagged task never no-routes — that is the no-CANDIDATE path");
        Assert.False(resolution.CostlyCeilingBound, "a costly block declares 'hard' here, so any candidacy sweep would have set this — it staying false is the proof that zero tier resolution ran");
        Assert.Empty(resolution.CostlyCeilingBlocks);
    }

    // ── 5. D30's other half — an effective tier NEVER falls back to legacy ──────────────────────

    /// <summary>
    /// <b>The test D30 exists for.</b> Once an effective tier EXISTS, resolution owns the outcome: a
    /// registry with nothing serving the rung at-or-above settles <c>no-route</c> (needs-human, §6.2) and
    /// does NOT quietly produce the runner's model.
    ///
    /// <para>Through revision 4, §6.1 item 3 read "no effective tier, <i>or no block serves it</i>",
    /// which contradicted §6.2/D26's halt for the same condition; revision 5 severed it in favour of the
    /// halt. A resolver written from the old reading returns <c>model-legacy-default</c> here and looks
    /// entirely reasonable doing it — which is why the registry deliberately holds a perfectly good
    /// non-routing <c>default</c> block and a CLI default: both wrong answers are RIGHT THERE.</para>
    ///
    /// <para>The control leg is the same registry with the <c>tiering</c> block removed. The ONLY
    /// difference between the two legs is whether a rung exists, and the outcome flips from
    /// <c>no-route</c> to the runner's model — which is D30's severed disjunction stated as a
    /// behaviour.</para>
    /// </summary>
    [Fact]
    public void EffectiveTier_WithNoServingBlock_SettlesNoRoute_AndNeverTheRunnersModel()
    {
        RunConfig tiered = Registry(
            [
                Block("legacy-default", "model-legacy-default", strength: 4),
                Block("easy-only", "model-easy-only", [ActionTiers.Easy], strength: 1)
            ],
            defaultRunner: "legacy-default",
            defaultTier: ActionTiers.Hard);

        TierResolution resolution = TierResolver.Resolve(Action(), tiered, cliDefaultModel: "cli-default");

        Assert.True(resolution.NoRoute, "nothing serves 'hard' or stronger — §6.2's defensive residual, settled needs-human");
        Assert.False(resolution.Legacy, "D30: once an effective tier exists, the legacy path is UNREACHABLE");
        Assert.False(resolution.Pinned, "nothing was pinned");
        Assert.Equal(ActionTiers.Hard, resolution.RequestedTier);
        Assert.True(resolution.Tier is null, "no rung was served");
        Assert.True(resolution.RunnerName is null, "no block was selected");
        Assert.True(
            resolution.Model is null,
            "a no-route must not quietly produce the runner's model ('model-legacy-default') or the CLI default ('cli-default') — that is exactly the revision-4 reading D30 severed");

        RunConfig untiered = tiered with { Tiering = null };

        TierResolution legacy = TierResolver.Resolve(Action(), untiered, cliDefaultModel: "cli-default");

        Assert.True(legacy.Legacy, "remove the rung and the SAME registry resolves via legacy — the rung is the whole difference");
        Assert.Equal("model-legacy-default", legacy.Model);
        Assert.False(legacy.NoRoute, "the untagged case never no-routes");
    }

    /// <summary>
    /// The other way an effective tier stays inside resolution: an EMPTY candidate set at the requested
    /// rung climbs to the nearest stronger rung (§6.2) rather than falling back to the runner's model.
    ///
    /// <para><c>easy</c> has no candidate here, and a non-routing <c>default</c> block is sitting right
    /// there with a model on it — so a resolver that reads item 3 as "no block serves it ⇒ legacy"
    /// returns <c>model-legacy-default</c> instead of climbing. The climb is also RECORDED, because wave 2
    /// logs it loudly rather than absorbing it.</para>
    /// </summary>
    [Fact]
    public void EffectiveTier_WithAnEmptyCandidateSet_Climbs_RatherThanFallingBackToLegacy()
    {
        RunConfig config = Registry(
            [
                Block("legacy-default", "model-legacy-default", strength: 4),
                Block("hard-only", "model-hard-only", [ActionTiers.Hard], strength: 9)
            ],
            defaultRunner: "legacy-default",
            defaultTier: ActionTiers.Easy);

        TierResolution resolution = TierResolver.Resolve(Action(), config, cliDefaultModel: "cli-default");

        Assert.False(resolution.Legacy, "D30: an effective tier climbs — it does not drop back to the runner's model");
        Assert.True(resolution.Climbed, "'easy' had no candidate, so this resolution climbed");
        Assert.Equal(ActionTiers.Easy, resolution.RequestedTier);
        Assert.Equal(ActionTiers.Hard, resolution.Tier);
        Assert.Equal("hard-only", resolution.RunnerName);
        Assert.Equal("model-hard-only", resolution.Model);
        Assert.NotEqual("model-legacy-default", resolution.Model);
        Assert.False(resolution.NoRoute, "a stronger rung had a candidate, so this is a climb");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One prompt action. Every §6.1 input is optional and defaults to ABSENT, so each test states only
    /// the fields its precedence case turns on: <paramref name="tier"/> is the task's own
    /// <c>action.tier</c>, <paramref name="runner"/> / <paramref name="model"/> are the two full-pin
    /// spellings, and <paramref name="effort"/> is the override that is deliberately NOT a pin.
    /// </summary>
    private static ActionDefinition Action(
        string? tier = null,
        string? runner = null,
        string? model = null,
        string? effort = null) => new()
        {
            Path = "/plan/tasks/01-task/action.prompt.md",
            Kind = ActionKind.Prompt,
            Tier = tier,
            Runner = runner,
            Model = model,
            Effort = effort
        };

    /// <summary>
    /// One <c>promptRunners</c> block. <paramref name="tiers"/> <c>null</c> means NO <c>routing</c> key at
    /// all — the "never a tier target" shape, which is what a pinned or <c>default</c>-pointer block looks
    /// like — and <paramref name="model"/> <c>null</c> means the block names no model, which is the case
    /// that falls through to the CLI default on the legacy path.
    /// </summary>
    private static PromptRunnerConfig Block(
        string name,
        string? model,
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
    /// A <see cref="RunConfig"/> holding <paramref name="blocks"/> IN THE ORDER GIVEN (declaration order
    /// is the §6.2 tie-break key), with <paramref name="defaultRunner"/> as the
    /// <c>promptRunners.default</c> pointer — the block the LEGACY path resolves through, exactly as
    /// <c>TaskExecutor</c> does today (<c>action.runner ?? config.DefaultPromptRunner</c>).
    ///
    /// <para><paramref name="defaultTier"/> <c>null</c> leaves the <c>tiering</c> block ABSENT
    /// altogether, which is a single-model user's config. A test that needs the block PRESENT but
    /// carrying no default (Invariant 7 fixture (b)) says so explicitly with
    /// <c>with { Tiering = new TieringConfig() }</c>.</para>
    /// </summary>
    private static RunConfig Registry(
        IReadOnlyList<PromptRunnerConfig> blocks,
        string? defaultRunner = null,
        string? defaultTier = null)
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
            Tiering = defaultTier is null ? null : new TieringConfig { DefaultTier = defaultTier }
        };
    }
}
