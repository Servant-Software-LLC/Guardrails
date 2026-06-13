using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails graph</c> through the real CLI pipeline against real temp plan
/// folders (SSOT §10). Covers default write, <c>--check</c> freshness/staleness/missing,
/// <c>--stdout</c> (writes nothing), and the <c>--format</c> guard.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class GraphCliTests
{
    private const string DiagramFileName = "diagram.md";

    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var root = new RootCommand("test root");
        root.Add(GraphCommand.Create());
        root.Add(ValidateCommand.Create());

        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            int exit = await root.Parse(args).InvokeAsync();
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static string DiagramPath(string planDir) => Path.Combine(planDir, DiagramFileName);

    [Fact]
    public async Task Graph_Default_WritesDiagramWithProvenanceAndFence_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Wrote", output);

        string diagramPath = DiagramPath(plan.PlanDir);
        Assert.True(File.Exists(diagramPath), "default run must write diagram.md");

        string content = await File.ReadAllTextAsync(diagramPath, TestContext.Current.CancellationToken);

        // Provenance comment prefix (SSOT §10), with a 64-char lowercase-hex source hash.
        Assert.Contains("<!-- guardrails:graph v1 source-sha256=", content);
        Assert.Matches(@"source-sha256=[0-9a-f]{64}\b", content);

        // A fenced mermaid block carrying the flowchart.
        Assert.Contains("```mermaid", content);
        Assert.Contains("flowchart TD", content);
        Assert.Contains("```", content);
    }

    [Fact]
    public async Task Graph_CheckImmediatelyAfterGenerate_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int writeExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, writeExit);

        (int checkExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, checkExit);
    }

    [Fact]
    public async Task Graph_CheckAfterAddingTask_ExitsOne_WithStaleLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Mutate the DAG: a new task changes the source hash, so the diagram is now stale.
        plan.AddTask("02-second", dependsOn: "01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("stale", output);
        // The actionable line names the regeneration command.
        Assert.Contains("guardrails graph", output);
    }

    [Fact]
    public async Task Graph_CheckAfterAddingGuardrailFile_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Add a second guardrail file directly to the task — changes the DAG-relevant shape.
        AddSecondGuardrail(plan.PlanDir, "01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("stale", output);
    }

    [Fact]
    public async Task Graph_CheckWithoutDiagram_ExitsOne_WithMissingLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // No prior generate → diagram.md does not exist.
        Assert.False(File.Exists(DiagramPath(plan.PlanDir)));

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("missing", output);
        Assert.Contains("guardrails graph", output);
    }

    [Fact]
    public async Task Graph_Stdout_WritesNothingToDisk_PrintsFlowchart()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("flowchart TD", output);

        // --stdout writes nothing: no diagram.md is created.
        Assert.False(File.Exists(DiagramPath(plan.PlanDir)), "--stdout must not write diagram.md");
        // And the provenance comment is NOT printed (stdout is the raw diagram, not the document).
        Assert.DoesNotContain("guardrails:graph v1", output);
    }

    [Fact]
    public async Task Graph_NonMermaidFormat_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--format", "dot");

        // --format only accepts 'mermaid'; anything else fails the parse → exit 1.
        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.False(File.Exists(DiagramPath(plan.PlanDir)), "a rejected format must not write a diagram");
    }

    [Fact]
    public async Task Graph_MissingFolder_ExitsOne()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-plan-" + Guid.NewGuid().ToString("N"));

        (int exit, _) = await InvokeCapturingAsync("graph", missing);

        Assert.Equal(ExitCodes.HarnessError, exit);
    }

    /// <summary>
    /// Add a second guardrail to an existing <see cref="ScriptPlanBuilder"/> task by writing a
    /// sibling guardrail file directly, mirroring the OS-appropriate script the builder emits.
    /// </summary>
    private static void AddSecondGuardrail(string planDir, string taskId)
    {
        string guardrailsDir = Path.Combine(planDir, "tasks", taskId, "guardrails");
        bool usePowerShell = OperatingSystem.IsWindows();
        string fileName = usePowerShell ? "02-extra.ps1" : "02-extra.sh";
        string body = usePowerShell
            ? "Write-Output \"guardrail ok\"\nexit 0\n"
            : "#!/usr/bin/env bash\necho \"guardrail ok\"\nexit 0\n";

        string path = Path.Combine(guardrailsDir, fileName);
        File.WriteAllText(path, body);
        if (!usePowerShell)
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
