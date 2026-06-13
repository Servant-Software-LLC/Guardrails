using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Regression lock: the generated <c>diagram.md</c> artifact at a plan-folder root must NOT
/// cause <c>guardrails validate</c> to fail (the loader ignores non-task root files). If a
/// future loader change started treating root markdown as part of the plan, this would catch
/// it before the generated artifact silently broke validation.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class GraphValidateToleranceTests
{
    private static async Task<int> InvokeAsync(params string[] args)
    {
        var root = new RootCommand("test root");
        root.Add(GraphCommand.Create());
        root.Add(ValidateCommand.Create());

        TextWriter original = Console.Out;
        Console.SetOut(new StringWriter()); // graph prints "Wrote ..."; keep test output clean.
        try
        {
            return await root.Parse(args).InvokeAsync();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task Validate_WithGeneratedDiagramAtRoot_StillExitsZero()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        // Sanity: the plan validates clean before any diagram exists.
        Assert.Equal(ExitCodes.Success, await InvokeAsync("validate", plan.PlanDir));

        // Generate the diagram artifact at the plan-folder root.
        Assert.Equal(ExitCodes.Success, await InvokeAsync("graph", plan.PlanDir));
        Assert.True(File.Exists(Path.Combine(plan.PlanDir, "diagram.md")));

        // The loader must still ignore the root diagram.md — validation stays green.
        Assert.Equal(ExitCodes.Success, await InvokeAsync("validate", plan.PlanDir));
    }

    [Fact]
    public async Task Validate_WithHandPlacedDiagram_StillExitsZero()
    {
        // Independent of the graph command: a bare diagram.md (any content) at the root must
        // be tolerated, so the regression holds even if graph's output format changes.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "diagram.md"),
            "<!-- guardrails:graph v1 source-sha256=deadbeef generated=2026-01-01T00:00:00Z -->\n\n```mermaid\nflowchart TD\n```\n");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("validate", plan.PlanDir));
    }
}
