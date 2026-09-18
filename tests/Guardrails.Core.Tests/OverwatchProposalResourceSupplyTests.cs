using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Design 41 §2.4 — <see cref="OverwatchProposal.ParseFix"/> gains a <c>resource-supply</c> case: an op
/// without a non-blank <c>path</c> is dropped by the existing advisory-never-gates rule, exactly like every
/// other fix kind. Exercised through the public <see cref="OverwatchProposal.TryParse"/> (<c>ParseFix</c> is
/// private).
///
/// <para>
/// TDD red: today's <c>ParseFix</c> has no <c>resource-supply</c> case, so both parser tests below are
/// expected to FAIL against this tree (every <c>resource-supply</c> op is silently dropped). Do not add
/// <c>Assert.Throws</c> wrappers.
/// </para>
///
/// <para>
/// <see cref="Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist"/> is the one declared census
/// exemption: it is GREEN on arrival, because <see cref="OverwatchFixClassifier"/> has no case for
/// <see cref="OverwatchFixKind.ResourceSupply"/> and already falls through to
/// <see cref="OverwatchAuthorityClass.Default"/>. §2.4 says the classifier does not change — this pins that a
/// resource-supply op can never be laundered onto the allowlist by adding the parser case.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchProposalResourceSupplyTests
{
    [Fact]
    public void TryParse_ParsesAResourceSupplyOpWithItsPath()
    {
        const string body = """
            {"classification":"retryable","diagnosis":"vendor/resource.js resolves the halted task's question.",
             "fixes":[{"kind":"resource-supply","path":"vendor/resource.js"}]}
            """;

        OverwatchProposal? parsed = OverwatchProposal.TryParse(body);

        Assert.NotNull(parsed);
        OverwatchFixOp op = Assert.Single(parsed!.Fixes);
        Assert.Equal(OverwatchFixKind.ResourceSupply, op.Kind);
        Assert.Equal("vendor/resource.js", op.TargetPath);
    }

    [Fact]
    public void TryParse_KeepsTheValidResourceSupplyOp_AndDropsTheOneWithNoPath()
    {
        // Assert BOTH halves: a test that only asserts the blank one is dropped passes on today's code,
        // which drops every resource-supply op, so it would prove nothing.
        const string body = """
            {"classification":"retryable","diagnosis":"one candidate resolves it; the other is unusable.",
             "fixes":[
               {"kind":"resource-supply","path":""},
               {"kind":"resource-supply","path":"vendor/resource.js"}
             ]}
            """;

        OverwatchProposal? parsed = OverwatchProposal.TryParse(body);

        Assert.NotNull(parsed);
        OverwatchFixOp op = Assert.Single(parsed!.Fixes);
        Assert.Equal(OverwatchFixKind.ResourceSupply, op.Kind);
        Assert.Equal("vendor/resource.js", op.TargetPath);
    }

    /// <summary>
    /// The declared census exemption. GREEN on arrival: the classifier has no case for
    /// <see cref="OverwatchFixKind.ResourceSupply"/> and already falls through to
    /// <see cref="OverwatchAuthorityClass.Default"/>. Written correctly, not made to fail to please the
    /// census — §2.4 says the classifier does not change, so adding the parser case must not open a route by
    /// which a resource-supply op could be auto-applied somewhere else.
    /// </summary>
    [Fact]
    public void Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist()
    {
        string planDirectory = Path.Combine(Path.GetTempPath(), "gr-overwatch-resource-supply-classifier-plan");
        string taskDirectory = Path.Combine(planDirectory, "tasks", "02-needs-resource");

        var task = new TaskNode
        {
            Id = "02-needs-resource",
            Directory = taskDirectory,
            Description = "t",
            Action = new ActionDefinition
            {
                Path = Path.Combine(taskDirectory, "action.prompt.md"),
                Kind = ActionKind.Prompt
            },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(taskDirectory, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        };

        var plan = new PlanDefinition
        {
            PlanDirectory = planDirectory,
            Workspace = planDirectory,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        var op = new OverwatchFixOp { Kind = OverwatchFixKind.ResourceSupply, TargetPath = "vendor/resource.js" };

        OverwatchAuthorityClass result = OverwatchFixClassifier.Classify(op, task, plan);

        Assert.Equal(OverwatchAuthorityClass.Default, result);
    }
}
