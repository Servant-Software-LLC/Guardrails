using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Design 41 §2.4 — the <c>resource-supply</c> fix op <see cref="OverwatchProposal.TryParse"/> gains, and
/// the one declared census exemption that proves adding it opens no new auto-apply route.
/// <see cref="OverwatchProposal.ParseFix"/> is private, so these go through the public
/// <see cref="OverwatchProposal.TryParse"/> entry point.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchProposalResourceSupplyTests
{
    private const string BodyWithValidOp =
        """{"classification":"retryable","diagnosis":"needs a vendored resource","fixes":[{"kind":"resource-supply","path":"vendor/resource.js"}]}""";

    private const string BodyWithValidAndBlankOp =
        """{"classification":"retryable","diagnosis":"needs a vendored resource","fixes":[{"kind":"resource-supply","path":"vendor/resource.js"},{"kind":"resource-supply","path":""}]}""";

    [Fact]
    public void TryParse_ParsesAResourceSupplyOpWithItsPath()
    {
        OverwatchProposal? parsed = OverwatchProposal.TryParse(BodyWithValidOp);

        Assert.NotNull(parsed);
        OverwatchFixOp op = Assert.Single(parsed.Fixes);
        Assert.Equal(OverwatchFixKind.ResourceSupply, op.Kind);
        Assert.Equal("vendor/resource.js", op.TargetPath);
    }

    [Fact]
    public void TryParse_KeepsTheValidResourceSupplyOp_AndDropsTheOneWithNoPath()
    {
        // A test that only asserts the blank one is dropped passes on today's code, which drops EVERY
        // resource-supply op (there is no case for the kind yet) — so both halves are asserted here.
        OverwatchProposal? parsed = OverwatchProposal.TryParse(BodyWithValidAndBlankOp);

        Assert.NotNull(parsed);
        OverwatchFixOp op = Assert.Single(parsed.Fixes);
        Assert.Equal(OverwatchFixKind.ResourceSupply, op.Kind);
        Assert.Equal("vendor/resource.js", op.TargetPath);
    }

    /// <summary>
    /// DECLARED CENSUS EXEMPTION — green on arrival. <see cref="OverwatchFixClassifier.Classify"/> has no
    /// case for <see cref="OverwatchFixKind.ResourceSupply"/> and already falls through to
    /// <see cref="OverwatchAuthorityClass.Default"/>, which is exactly the property design 41 §2.4
    /// requires: the classifier does not change, so adding the parser case above must not open a route by
    /// which a resource-supply op could be auto-applied.
    /// </summary>
    [Fact]
    public void Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist()
    {
        TaskNode task = PlanFixtures.Task("03-impl");
        PlanDefinition plan = PlanFixtures.Plan(task);

        OverwatchAuthorityClass result = OverwatchFixClassifier.Classify(
            new OverwatchFixOp { Kind = OverwatchFixKind.ResourceSupply, TargetPath = "vendor/resource.js" },
            task, plan);

        Assert.Equal(OverwatchAuthorityClass.Default, result);
    }
}
