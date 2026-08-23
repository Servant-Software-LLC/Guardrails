using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Validate-time coverage for <see cref="DiagnosticCodes.PinAndTierCoexist"/> (GR2053, DoR §6.1 /
/// §12.6, devil's-advocate findings F3 and F4): one action carries BOTH a full pin and
/// <c>action.tier</c>, so the tier is dead weight the pin overrides — it selects no block and decides
/// nothing. A WARNING, not an error: the action is unambiguous and the plan still runs; the pin simply
/// wins. Silence would be wrong too, because an author who wrote <c>tier</c> believes the task is being
/// ROUTED and has no other way to learn a pin is still deciding for it.
///
/// <para><b>A pin is <c>action.runner</c> OR <c>action.model</c> — not both.</b> DoR §13.2's
/// "<c>action.runner</c>/<c>action.model</c>" slash reads like an AND; the shipped resolver settles it —
/// <c>TierResolver.Resolve</c> branches on
/// <c>if (action.Runner is not null || action.Model is not null)</c>, ABOVE the tier read, so EITHER
/// alone bypasses tier resolution entirely. That is why the model-pin case below is its own named test
/// rather than a variation: a reading that demanded both would silently miss a task whose tier is just as
/// dead.</para>
///
/// <para><b>Two of these four are RED on arrival, deliberately.</b> The two warning cases assert a
/// diagnostic the validator does not emit yet (the emission is the implement task's job, in
/// <c>PlanValidator</c>). The two silence cases assert a code is ABSENT, which a not-yet-built feature
/// satisfies trivially — they pass today and must keep passing once GR2053 ships, which is exactly what
/// makes them worth pinning: they bound the new warning to the coexistence case and stop it firing on an
/// ordinary pinned or an ordinary tiered task.</para>
///
/// <para>Every silence assertion is written as "this CODE is absent", never "no diagnostics at all": the
/// in-memory fixture legitimately draws unrelated diagnostics (the fake <c>/fake</c> workspace is not a
/// git root, and a tier declared under a routing-configured registry has its own checks), and a test that
/// demanded an empty collection would fail for reasons that have nothing to do with GR2053.</para>
///
/// <para>Follows <c>CostCapValidatorTests</c>: build an in-memory plan, validate it with a probe that
/// resolves every command, assert on the diagnostic code and its severity. GR2053 is an ACTION-level rule
/// over already-loaded fields, so nothing here needs to touch disk.</para>
/// </summary>
public sealed class PinAndTierCoexistTests
{
    private const string RunnerName = "claude";

    /// <summary>
    /// A registry block that DECLARES routing, which is what makes tiering "configured" for the plan —
    /// the realistic setting for this warning, since a tier is only worth writing where a rung could
    /// have selected a block.
    /// </summary>
    private static readonly PromptRunnerConfig TieredRunner = new()
    {
        Name = RunnerName,
        Command = RunnerName,
        Settings = new PromptRunnerSettings { Model = "claude-sonnet-5" },
        Strength = 3,
        Routing = new PromptRunnerRouting { Tiers = ["easy", "medium", "hard"] }
    };

    /// <summary>
    /// A one-prompt-task plan whose action carries exactly the pin/tier/effort combination under test.
    /// Any of them may be null — null means "the key was absent from task.json", which is the whole
    /// distinction these cases turn on.
    /// </summary>
    private static PlanDefinition PlanWithAction(
        string? runner = null, string? model = null, string? tier = null, string? effort = null)
    {
        TaskNode task = PromptTask("01-only");
        task = task with
        {
            Action = task.Action with { Runner = runner, Model = model, Tier = tier, Effort = effort }
        };

        PlanDefinition plan = Plan(task);
        return plan with
        {
            Config = plan.Config with
            {
                DefaultPromptRunner = RunnerName,
                PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { RunnerName },
                PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
                {
                    [RunnerName] = TieredRunner
                }
            }
        };
    }

    private static IReadOnlyList<Diagnostic> Validate(
        string? runner = null, string? model = null, string? tier = null, string? effort = null) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(PlanWithAction(runner, model, tier, effort));

    // --- the warning fires when a pin and a tier coexist ------------------------------------------

    /// <summary>
    /// <c>action.runner</c> + <c>action.tier</c>: the pin names the block outright, so the rung never
    /// selects anything.
    /// </summary>
    [Trait("Category", "ModelTieringStage3")]
    [Fact]
    public void WarnsWhenRunnerPinAndTierCoexist()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate(runner: RunnerName, tier: "medium");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PinAndTierCoexist);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    /// <summary>
    /// <c>action.model</c> + <c>action.tier</c> with NO <c>action.runner</c> at all. A raw model pin is a
    /// full pin on its own (<c>TierResolver</c>'s <c>Runner is not null || Model is not null</c>), so this
    /// tier is exactly as dead as the runner-pinned one — and it is the case a both-are-required reading
    /// of the rule would silently let through.
    /// </summary>
    [Trait("Category", "ModelTieringStage3")]
    [Fact]
    public void WarnsWhenModelPinAndTierCoexist()
    {
        // Guard the premise: if this fixture ever acquired a runner, the test would no longer be about
        // the model-alone pin it is named for.
        Assert.Null(PlanWithAction(model: "claude-opus-5", tier: "hard").Tasks[0].Action.Runner);

        IReadOnlyList<Diagnostic> diagnostics = Validate(model: "claude-opus-5", tier: "hard");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PinAndTierCoexist);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    // --- …and stays silent on either half alone ---------------------------------------------------

    /// <summary>
    /// A pin with NO tier — the ordinary pinned task, in all three of its shapes. Nothing is dead weight:
    /// the pin is the only thing that was ever going to decide. The third shape carries
    /// <c>action.effort</c> alongside the pin, which is the narrowness DA F4 insists on — effort
    /// legitimately overrides the pinned route's effort (§6.1 item 2) and is never this warning.
    /// </summary>
    [Trait("Category", "ModelTieringStage3")]
    [Fact]
    public void SilentWhenPinWithoutTier()
    {
        Assert.DoesNotContain(
            Validate(runner: RunnerName), d => d.Code == DiagnosticCodes.PinAndTierCoexist);

        Assert.DoesNotContain(
            Validate(model: "claude-opus-5"), d => d.Code == DiagnosticCodes.PinAndTierCoexist);

        Assert.DoesNotContain(
            Validate(runner: RunnerName, effort: "xhigh"), d => d.Code == DiagnosticCodes.PinAndTierCoexist);
    }

    /// <summary>
    /// A tier with NO pin — the ordinary tiered task, which is the whole point of the feature. Warning
    /// here would fire on every routed task in a tiered plan.
    /// </summary>
    [Trait("Category", "ModelTieringStage3")]
    [Fact]
    public void SilentWhenTierWithoutPin()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate(tier: "medium");

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PinAndTierCoexist);
    }
}
