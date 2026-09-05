using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Escalation;

/// <summary>
/// The TDD-red tests for the escalation ladder (DoR issue #228): <see cref="EscalationLadder.NextRung"/>
/// (the pure rung-successor step) and <see cref="EscalationLadder.Apply"/> (what the retry loop calls
/// after a guardrail-failed attempt). They compile against the stub in
/// <c>src/Guardrails.Core/Prompts/EscalationLadder.cs</c> and fail against it — both members throw
/// <see cref="NotImplementedException"/> — until <c>02-implement-escalation-ladder</c> fills them in.
///
/// <para><b>The ladder calls <see cref="TierResolver.SelectCandidate"/>; it does not re-derive it.</b>
/// Every registry below is built and resolved exactly as
/// <c>ModelTiering.TierResolverCandidateSelectionTests</c> does, so a route handed to
/// <see cref="EscalationLadder.Apply"/> here is the same kind of value the retry loop will actually
/// pass — never a hand-rolled fake.</para>
/// </summary>
[Trait("Category", "EscalationLadder")]
public sealed class EscalationLadderTests
{
    // ── NextRung — the pure ladder step ──────────────────────────────────────────────────────────

    [Fact]
    public void NextRung_FromEasy_IsMedium() =>
        Assert.Equal(ActionTiers.Medium, EscalationLadder.NextRung(ActionTiers.Easy));

    [Fact]
    public void NextRung_FromMedium_IsHard() =>
        Assert.Equal(ActionTiers.Hard, EscalationLadder.NextRung(ActionTiers.Medium));

    [Fact]
    public void NextRung_FromHard_IsNull() =>
        Assert.Null(EscalationLadder.NextRung(ActionTiers.Hard));

    /// <summary>
    /// A rung not on the ladder — and a null rung — has no successor. Both are covered here because
    /// they are the same defensive residual: neither is a position <see cref="ActionTiers.All"/> holds.
    /// </summary>
    [Fact]
    public void NextRung_FromAnUnrecognizedRung_IsNull()
    {
        Assert.Null(EscalationLadder.NextRung(null));
        Assert.Null(EscalationLadder.NextRung("not-a-real-rung"));
    }

    // ── Apply — the byte-identical case: nothing has failed yet ─────────────────────────────────

    [Fact]
    public void Apply_WithNoGuardrailFailures_ReturnsTheRouteUnchanged()
    {
        RunConfig config = ThreeRungLadder();
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Medium);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 0);

        Assert.Equal(route, result);
        Assert.Null(result.EscalatedFrom);
    }

    // ── Apply — the ordinary one-rung and two-rung climbs ───────────────────────────────────────

    [Fact]
    public void Apply_AfterOneGuardrailFailure_ServesOneRungStronger()
    {
        RunConfig config = ThreeRungLadder();
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Easy);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.Equal(ActionTiers.Medium, result.Tier);
        Assert.Equal("medium-runner", result.RunnerName);
    }

    [Fact]
    public void Apply_AfterOneGuardrailFailure_RecordsTheOriginalRungInEscalatedFrom()
    {
        RunConfig config = ThreeRungLadder();
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Easy);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.Equal(ActionTiers.Easy, result.EscalatedFrom);
    }

    [Fact]
    public void Apply_AcrossTwoGuardrailFailuresClimbsTwoRungsAndKeepsTheOriginalEscalatedFrom()
    {
        RunConfig config = ThreeRungLadder();
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Easy);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 2);

        Assert.Equal(ActionTiers.Hard, result.Tier);
        Assert.Equal("hard-runner", result.RunnerName);
        Assert.Equal(ActionTiers.Easy, result.EscalatedFrom);
    }

    // ── Apply — the cap-and-degrade cases: the silent-failure surface of this feature ──────────

    /// <summary>
    /// A registry whose blocks serve only up to <c>medium</c>, already served at <c>medium</c>: nothing
    /// stronger is configured, so escalating must stay put, report no error, and must NOT claim an
    /// escalation that never landed.
    /// </summary>
    [Fact]
    public void Apply_OnTheStrongestRegisteredRung_StaysPutAndIsNotMarkedEscalated()
    {
        RunConfig config = Registry(
            Block("easy-runner", "model-easy", [ActionTiers.Easy], strength: 1),
            Block("medium-runner", "model-medium", [ActionTiers.Medium], strength: 1));
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Medium);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.Equal(route, result);
        Assert.Null(result.EscalatedFrom);
    }

    /// <summary>
    /// One <c>promptRunners</c> block with NO <c>routing</c> block at all — the LEGACY route
    /// (<see cref="TierResolution.Legacy"/> true, <see cref="TierResolution.Tier"/> null) every plan in
    /// existence today resolves to. There is no rung to climb from, so the result must be byte-equal to
    /// the input route. A regression here breaks everyone.
    /// </summary>
    [Fact]
    public void Apply_OnASingleRunnerLegacyConfig_ReturnsTodaysResolutionUnchanged()
    {
        RunConfig config = Registry(Block("solo", "model-solo", tiers: null));
        ActionDefinition action = new() { Path = "action.prompt.md", Kind = ActionKind.Prompt };
        TierResolution route = TierResolver.Resolve(action, config);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.True(route.Legacy, "the fixture must actually produce the legacy route this test is about");
        Assert.Equal(route, result);
        Assert.Null(result.EscalatedFrom);
    }

    /// <summary>
    /// A registry serving <c>easy</c> and <c>hard</c> but not <c>medium</c>: escalating from <c>easy</c>
    /// must land on <c>hard</c>, because <see cref="TierResolver.SelectCandidate"/> already keeps
    /// climbing past an empty rung — that is its job, not a second loop in the ladder.
    /// </summary>
    [Fact]
    public void Apply_WhenTheNextRungHasNoCandidate_KeepsClimbingToOneThatServes()
    {
        RunConfig config = Registry(
            Block("easy-runner", "model-easy", [ActionTiers.Easy], strength: 1),
            Block("hard-runner", "model-hard", [ActionTiers.Hard], strength: 1));
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Easy);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.Equal(ActionTiers.Hard, result.Tier);
        Assert.Equal("hard-runner", result.RunnerName);
        Assert.Equal(ActionTiers.Easy, result.EscalatedFrom);
    }

    /// <summary>
    /// A registry serving only <c>easy</c>: escalating from <c>easy</c> must stay at <c>easy</c>, with
    /// <see cref="TierResolution.EscalatedFrom"/> null — nothing at or above the next rung routes, so
    /// nothing may claim an escalation happened.
    /// </summary>
    [Fact]
    public void Apply_WhenNoRungAtOrAboveRoutes_StaysPut()
    {
        RunConfig config = Registry(Block("easy-runner", "model-easy", [ActionTiers.Easy], strength: 1));
        TierResolution route = TierResolver.SelectCandidate(config, ActionTiers.Easy);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.Equal(route, result);
        Assert.Null(result.EscalatedFrom);
    }

    /// <summary>
    /// A pin is a human's assignment, not a capability floor: there is no rung to climb from at all, so
    /// a pinned route is never escalated, however many guardrail failures preceded this attempt.
    /// </summary>
    [Fact]
    public void Apply_OnAPinnedRoute_ReturnsItUnchanged()
    {
        RunConfig config = Registry(Block("pinned-block", "model-pinned", [ActionTiers.Easy], strength: 1));
        ActionDefinition action = new()
        {
            Path = "action.prompt.md",
            Kind = ActionKind.Prompt,
            Runner = "pinned-block"
        };
        TierResolution route = TierResolver.Resolve(action, config);

        TierResolution result = EscalationLadder.Apply(config, route, escalations: 1);

        Assert.True(route.Pinned, "the fixture must actually produce the pinned route this test is about");
        Assert.Equal(route, result);
        Assert.Null(result.EscalatedFrom);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A registry with one distinct block per rung — the shared setup for the ordinary climb tests,
    /// where each rung's runner name proves resolution actually moved rather than merely reporting a
    /// different tier string.
    /// </summary>
    private static RunConfig ThreeRungLadder() =>
        Registry(
            Block("easy-runner", "model-easy", [ActionTiers.Easy], strength: 1),
            Block("medium-runner", "model-medium", [ActionTiers.Medium], strength: 1),
            Block("hard-runner", "model-hard", [ActionTiers.Hard], strength: 1));

    /// <summary>
    /// One <c>promptRunners</c> block, built the same way
    /// <c>ModelTiering.TierResolverCandidateSelectionTests.Block</c> does. <paramref name="tiers"/> null
    /// means NO <c>routing</c> key at all.
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
    /// A <see cref="RunConfig"/> whose <c>promptRunners</c> map holds <paramref name="blocks"/> in
    /// declaration order, with <c>promptRunners.default</c> pointed at the FIRST block — the shape a
    /// pinned or legacy resolution needs to find a block by name at all.
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
            PromptRunners = runners,
            DefaultPromptRunner = blocks.Length > 0 ? blocks[0].Name : null
        };
    }
}
