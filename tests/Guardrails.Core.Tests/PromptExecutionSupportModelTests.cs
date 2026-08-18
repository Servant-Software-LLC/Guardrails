using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit-level pin of the model precedence a prompt attempt runs under — <c>action.model</c> &gt; the
/// prompt-runner block's own <c>model</c> &gt; the display-only <c>"(cli default)"</c> sentinel (issue
/// #200) — asserted at the seam that now OWNS it.
///
/// <para><b>The coverage MOVED; it did not change.</b> Stage 2 (issue #201, DoR
/// <c>docs/plans/17-model-tiering.md</c> §6.1) replaced the two-level
/// <c>ResolveModelForDisplay(action.model, runnerModel)</c> fallback with ONE attempt-launch
/// resolution: <see cref="TierResolver.Resolve(ActionDefinition, RunConfig, string?)"/> decides the
/// route, and D30 makes its LEGACY branch exactly that fallback —
/// <c>promptRunners.&lt;name&gt;.model</c> else the CLI's own default. Keeping a second copy of the
/// precedence in <see cref="PromptExecutionSupport"/> would have re-created the very drift the wave
/// exists to delete (provenance derived one way, argv another, agreeing only by construction), so the
/// two configured rungs are now asserted THROUGH the resolver and only the SENTINEL — a display
/// concern — is still asserted against
/// <see cref="PromptExecutionSupport.ResolvedModelForDisplay"/>.</para>
///
/// <para><b>Why the file survives at all.</b> It is Invariant 7's shipped guard: the exact behaviour a
/// single-model user who never opted into tiering still gets. Every fixture below is one of those users
/// — no <c>routing</c> and no <c>tiering</c> anywhere except in the two clauses that say otherwise — so
/// a regression here is a regression in "tiering changes nothing for a plan that never asked for it".
/// The end-to-end proof (real invocation argv + <c>run.json</c> provenance) lives in
/// <c>Guardrails.Integration.Tests.ActionModelResolutionTests</c>, and the real-seam routing proof in
/// <c>Guardrails.Integration.Tests.ModelTiering.Stage2ConformanceTests</c>.</para>
/// </summary>
public sealed class PromptExecutionSupportModelTests
{
    /// <summary>
    /// The three precedence rungs, as PROVENANCE records them: the task's own <c>action.model</c> pin
    /// wins, else the runner block's <c>model</c>, else the display-only sentinel. The same three rows
    /// this file pinned before Stage 2 — only the seam that produces the answer moved.
    /// </summary>
    [Theory]
    [InlineData("claude-haiku-4-5", "claude-sonnet-5", "claude-haiku-4-5")]  // the task's own pin wins
    [InlineData(null, "claude-sonnet-5", "claude-sonnet-5")]                 // the runner block's model wins
    [InlineData(null, null, "(cli default)")]                                // neither set → the sentinel
    public void ResolvedModelForDisplay_MatchesTheDocumentedPrecedence(
        string? taskModelPin, string? runnerModel, string expected)
    {
        TierResolution route = TierResolver.Resolve(Action(model: taskModelPin), SingleModelPlan(runnerModel));

        Assert.Equal(expected, PromptExecutionSupport.ResolvedModelForDisplay(route.Model));
    }

    /// <summary>
    /// The same precedence on the INVOCATION side: an <c>action.model</c> pin reaches the settings the
    /// runner is launched with. §6.1 item 1 folds the pin into the resolution itself, so the pinned model
    /// arrives here as the ROUTE's model rather than as a second read of <c>task.Action.Model</c>.
    /// </summary>
    [Fact]
    public void ApplyModelOverride_TaskPin_WinsOverTheRunnerBlocksModel()
    {
        TierResolution route = TierResolver.Resolve(
            Action(model: "claude-haiku-4-5"), SingleModelPlan("claude-sonnet-5"));

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(
            new PromptRunnerSettings { Model = "claude-sonnet-5" }, route);

        Assert.True(route.Pinned, "action.model is a full pin — §6.1 item 1 decided this resolution");
        Assert.Equal("claude-haiku-4-5", result.Model);
    }

    /// <summary>
    /// <b>Invariant 7's core case.</b> A task that pins nothing and carries no rung takes the LEGACY
    /// branch, and the runner block's own configured model passes through untouched — byte-identical to
    /// the behaviour before tiering existed.
    /// </summary>
    [Fact]
    public void ApplyModelOverride_LegacyRoute_LeavesTheRunnerBlocksModelUnchanged()
    {
        TierResolution route = TierResolver.Resolve(Action(), SingleModelPlan("claude-sonnet-5"));

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(
            new PromptRunnerSettings { Model = "claude-sonnet-5" }, route);

        Assert.True(route.Legacy, "no pin and no rung anywhere — §6.1 item 3 (D30) decided this resolution");
        Assert.Equal("claude-sonnet-5", result.Model);
    }

    /// <summary>
    /// Nothing configures a model anywhere: the invocation carries NO model (so the runner omits
    /// <c>--model</c> entirely and the CLI picks its own), while provenance still records the sentinel
    /// rather than a silent gap. The sentinel is DISPLAY-only and must never reach the settings.
    /// </summary>
    [Fact]
    public void ApplyModelOverride_NothingConfiguresAModel_PassesNoModel_ButProvenanceShowsTheSentinel()
    {
        TierResolution route = TierResolver.Resolve(Action(), SingleModelPlan(runnerModel: null));

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(
            new PromptRunnerSettings { Model = null }, route);

        Assert.Null(result.Model);
        Assert.Equal("(cli default)", PromptExecutionSupport.ResolvedModelForDisplay(route.Model));
    }

    /// <summary>
    /// The Stage 2 addition to the same seam: on a TIER-RESOLVED route the SELECTED block's model reaches
    /// the invocation, replacing whatever the settings were built from.
    /// </summary>
    [Fact]
    public void ApplyModelOverride_TierResolvedRoute_CarriesTheSelectedBlocksModel()
    {
        TierResolution route = TierResolver.Resolve(
            Action(tier: ActionTiers.Easy), RoutedPlan(routedModel: "claude-haiku-4-5"));

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(
            new PromptRunnerSettings { Model = "claude-sonnet-5" }, route);

        Assert.Equal("claude-haiku-4-5", route.Model);
        Assert.Equal("claude-haiku-4-5", result.Model);
    }

    /// <summary>
    /// A tier-resolved route that names NO model means "pass no <c>--model</c>, let the runner CLI pick"
    /// — it must CLEAR the model of the block the settings happened to be built from, never fall back to
    /// it. That fallback is the drift this seam exists to remove: provenance would record
    /// <c>"(cli default)"</c> while the invocation carried a real model nobody selected.
    /// </summary>
    [Fact]
    public void ApplyModelOverride_TierResolvedRouteNamingNoModel_ClearsTheModelItDidNotSelect()
    {
        TierResolution route = TierResolver.Resolve(
            Action(tier: ActionTiers.Easy), RoutedPlan(routedModel: null));

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(
            new PromptRunnerSettings { Model = "claude-sonnet-5" }, route);

        Assert.Null(route.Model);
        Assert.Null(result.Model);
        Assert.Equal("(cli default)", PromptExecutionSupport.ResolvedModelForDisplay(route.Model));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One prompt action. <paramref name="tier"/> non-null carries <see cref="TierOrigin.Task"/> beside
    /// it, which is what the loader records when the task writes its own <c>action.tier</c>.
    /// </summary>
    private static ActionDefinition Action(string? model = null, string? tier = null) => new()
    {
        Path = "/plan/tasks/01-task/action.prompt.md",
        Kind = ActionKind.Prompt,
        Model = model,
        Tier = tier,
        TierOrigin = tier is null ? TierOrigin.None : TierOrigin.Task
    };

    /// <summary>
    /// A SINGLE-MODEL user's plan: one <c>promptRunners</c> block, named as the <c>default</c> pointer,
    /// carrying no <c>routing</c> key and no <c>tiering</c> block at all. This is the config Invariant 7
    /// protects, and the one every pin/legacy row above resolves against.
    /// </summary>
    private static RunConfig SingleModelPlan(string? runnerModel) =>
        Config([Block("claude", runnerModel)], defaultRunner: "claude");

    /// <summary>
    /// A ROUTING plan: the same default pointer (with a distinct model, so a fallback to it is
    /// unmistakable) plus one block serving the easy rung with <paramref name="routedModel"/> — null
    /// meaning the selected block names no model of its own.
    /// </summary>
    private static RunConfig RoutedPlan(string? routedModel) =>
        Config(
            [
                Block("claude", "claude-sonnet-5"),
                Block("routed", routedModel, tiers: [ActionTiers.Easy], strength: 1)
            ],
            defaultRunner: "claude");

    private static PromptRunnerConfig Block(
        string name,
        string? model,
        IReadOnlyList<string>? tiers = null,
        int? strength = null) => new()
        {
            Name = name,
            Command = "claude",
            Settings = new PromptRunnerSettings { Model = model },
            Strength = strength,
            Routing = tiers is null ? null : new PromptRunnerRouting { Tiers = tiers }
        };

    private static RunConfig Config(IReadOnlyList<PromptRunnerConfig> blocks, string defaultRunner)
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
            DefaultPromptRunner = defaultRunner
        };
    }
}
