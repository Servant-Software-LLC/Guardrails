using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end coverage of the issue #16 plan-file → task-folder fixup through the real CLI
/// pipeline. For <c>validate</c>, <c>run</c>, and <c>graph</c>: passing the plan SOURCE FILE
/// (<c>foo.md</c>) whose sibling task folder (<c>foo/</c>) exists makes the command proceed
/// against the folder and print the one info line; passing <c>foo.md</c> with NO sibling folder
/// still produces the genuine GR1001 "Plan folder does not exist" error.
/// </summary>
public sealed class PlanFileArgumentCliTests
{
    private const string InfoLine = "info: resolved plan file → task folder";

    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(ValidateCommand.Create(io));
        root.Add(RunCommand.Create(io));
        root.Add(GraphCommand.Create(io));

        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// Create a sibling <c>&lt;PlanDir&gt;.md</c> next to the builder's task folder, so stripping the
    /// <c>.md</c> suffix yields exactly the task folder. The file is created OUTSIDE the builder's
    /// root (which is the folder itself), so the test owns its cleanup.
    /// </summary>
    private static string CreateSiblingMarkdown(string planDir)
    {
        string mdPath = planDir + ".md";
        File.WriteAllText(mdPath, "# plan source\n");
        return mdPath;
    }

    [Fact]
    public async Task Validate_MdArgWithSiblingFolder_ResolvesAndExitsZero_WithInfoLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string mdPath = CreateSiblingMarkdown(plan.PlanDir);
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("validate", mdPath);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains(InfoLine, output);
            Assert.Contains(plan.PlanDir, output);
            Assert.Contains("OK: plan is valid.", output);
            Assert.DoesNotContain("GR1001", output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }

    [Fact]
    public async Task Validate_MdArgWithoutSiblingFolder_StillExitsGR1001()
    {
        // A .md path whose stem has NO sibling folder — a genuinely bad plan-folder path. No
        // resolution happens, so the existing GR1001 error fires unchanged.
        string mdPath = Path.Combine(
            Path.GetTempPath(), "guardrails-no-folder-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(mdPath, "# plan source\n");
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("validate", mdPath);

            Assert.Equal(ExitCodes.HarnessError, exit);
            Assert.Contains("GR1001", output);
            Assert.Contains("Plan folder does not exist.", output);
            Assert.DoesNotContain(InfoLine, output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }

    [Fact]
    public async Task Run_MdArgWithSiblingFolder_ResolvesAndRunsGreen_WithInfoLine()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");
        string mdPath = CreateSiblingMarkdown(plan.PlanDir);
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("run", mdPath, "--no-ui");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains(InfoLine, output);
            Assert.Contains(plan.PlanDir, output);
            Assert.DoesNotContain("GR1001", output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }

    [Fact]
    public async Task Run_MdArgWithoutSiblingFolder_StillExitsGR1001()
    {
        string mdPath = Path.Combine(
            Path.GetTempPath(), "guardrails-no-folder-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(mdPath, "# plan source\n");
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("run", mdPath, "--no-ui");

            Assert.Equal(ExitCodes.HarnessError, exit);
            Assert.Contains("GR1001", output);
            Assert.DoesNotContain(InfoLine, output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }

    [Fact]
    public async Task Graph_MdArgWithSiblingFolder_ResolvesAndWritesDiagram_WithInfoLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string mdPath = CreateSiblingMarkdown(plan.PlanDir);
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("graph", mdPath, "--stdout");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains(InfoLine, output);
            Assert.Contains("flowchart TD", output);
            Assert.DoesNotContain("GR1001", output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }

    [Fact]
    public async Task Graph_MdArgWithoutSiblingFolder_StillExitsGR1001()
    {
        string mdPath = Path.Combine(
            Path.GetTempPath(), "guardrails-no-folder-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(mdPath, "# plan source\n");
        try
        {
            (int exit, string output) = await InvokeCapturingAsync("graph", mdPath);

            Assert.Equal(ExitCodes.HarnessError, exit);
            Assert.Contains("GR1001", output);
            Assert.DoesNotContain(InfoLine, output);
        }
        finally
        {
            File.Delete(mdPath);
        }
    }
}
