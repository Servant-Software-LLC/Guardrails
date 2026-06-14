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

    [Fact]
    public async Task Graph_RerunOnUnchangedPlan_ProducesByteIdenticalDiagram()
    {
        // Deterministic projection: with no timestamp in the provenance comment, a second run
        // on an unchanged plan must yield a byte-identical diagram.md (no git churn).
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);
        byte[] first = await File.ReadAllBytesAsync(DiagramPath(plan.PlanDir), TestContext.Current.CancellationToken);

        await InvokeCapturingAsync("graph", plan.PlanDir);
        byte[] second = await File.ReadAllBytesAsync(DiagramPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Graph_CheckAfterEditingGuardrailDescription_ExitsOne()
    {
        // The realignment: the renderer draws the guardrail Description ?? Name. Adding a
        // sidecar description changes the DRAWN label, so the diagram is now stale.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        SetGuardrailDescription(plan.PlanDir, "01-first", "Build passes with zero warnings");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("stale", output);
    }

    [Fact]
    public async Task Graph_CheckAfterChangingActionKind_ExitsZero()
    {
        // action.Kind is NOT drawn, so switching a task's action from script to prompt does not
        // change the diagram — the staleness key is unaffected and --check reports FRESH.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        SwitchActionToPrompt(plan.PlanDir, "01-first");

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.Success, exit);
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

    /// <summary>
    /// Write a metadata sidecar carrying a <c>description</c> next to the task's single
    /// <c>ScriptPlanBuilder</c>-emitted guardrail (<c>01-check.{ps1,sh}</c>), so the renderer
    /// draws that description as the guardrail label.
    /// </summary>
    private static void SetGuardrailDescription(string planDir, string taskId, string description)
    {
        string guardrailsDir = Path.Combine(planDir, "tasks", taskId, "guardrails");
        string sidecarPath = Path.Combine(guardrailsDir, "01-check.json");
        File.WriteAllText(sidecarPath, $$"""{ "description": "{{description}}" }""");
    }

    /// <summary>
    /// Replace the task's script action (<c>action.{ps1,sh}</c>) with a single prompt action
    /// (<c>action.prompt.md</c>), flipping its <see cref="Guardrails.Core.Model.ActionKind"/>
    /// from Script to Prompt while leaving every DRAWN element of the diagram unchanged. Also
    /// declares a <c>promptRunners</c> block so the plan still validates (a prompt with no
    /// runners is a GR2008 error; a declared runner whose command is off PATH is only a GR2009
    /// warning, which does not block <c>graph</c>).
    /// </summary>
    private static void SwitchActionToPrompt(string planDir, string taskId)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        foreach (string existing in Directory.EnumerateFiles(taskDir, "action.*"))
        {
            File.Delete(existing);
        }

        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,
              "maxParallelism": 1,
              "promptRunners": {
                "default": "fake-runner",
                "fake-runner": { "command": "guardrails-no-such-runner" }
              }
            }
            """);
    }
}
