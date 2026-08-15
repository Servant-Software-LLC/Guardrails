using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails graph</c> through the real CLI pipeline against real temp plan
/// folders (SSOT §10). Covers default write, the structure-only caption, <c>--check</c>
/// freshness/staleness/missing (exit 2) vs a genuine load/validate error (exit 1),
/// <c>--stdout</c> (writes nothing), and the <c>--format</c> guard.
/// </summary>
public sealed class GraphCliTests
{
    private const string DiagramFileName = "diagram.md";

    /// <summary>
    /// The one-line structure-only caption written after the mermaid fence (SSOT §10). Lives in
    /// the markdown wrapper only — outside the hashed semantic content and absent from <c>--stdout</c>.
    /// </summary>
    private const string DiagramCaption =
        "_Structure only — retry, feedback, and needs-human edges are omitted._";

    /// <summary>Exit code <c>--check</c> returns for a stale or missing diagram (SSOT §7/§10).</summary>
    private const int StaleExitCode = 2;

    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(GraphCommand.Create(io));
        root.Add(ValidateCommand.Create(io));

        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
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

        // The structure-only caption sits AFTER the closing fence, in the markdown wrapper —
        // not inside the mermaid block.
        Assert.Contains(DiagramCaption, content);
        int fenceClose = content.LastIndexOf("```", StringComparison.Ordinal);
        int caption = content.IndexOf(DiagramCaption, StringComparison.Ordinal);
        Assert.True(caption > fenceClose, "caption must follow the closing mermaid fence");
        // The caption is NOT inside the fenced mermaid block.
        int fenceOpen = content.IndexOf("```mermaid", StringComparison.Ordinal);
        string insideFence = content[fenceOpen..(fenceClose + 3)];
        Assert.DoesNotContain(DiagramCaption, insideFence);
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
    public async Task Graph_CheckAfterAddingTask_ExitsStale_WithStaleLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Mutate the DAG: a new task changes the source hash, so the diagram is now stale.
        plan.AddTask("02-second", dependsOn: "01-first");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        // Stale → the "regenerate" signal (exit 2), distinct from a genuine error (exit 1).
        Assert.Equal(StaleExitCode, exit);
        Assert.NotEqual(ExitCodes.HarnessError, exit);
        Assert.Contains("stale", output);
        // The actionable line names the regeneration command.
        Assert.Contains("guardrails graph", output);
    }

    [Fact]
    public async Task Graph_CheckAfterAddingGuardrailFile_ExitsStale()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        // Add a second guardrail file directly to the task — changes the DAG-relevant shape.
        AddSecondGuardrail(Path.Combine(plan.PlanDir, "tasks", "01-first"));

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(StaleExitCode, exit);
        Assert.Contains("stale", output);
    }

    [Fact]
    public async Task Graph_CheckWithoutDiagram_ExitsStale_WithMissingLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // No prior generate → diagram.md does not exist.
        Assert.False(File.Exists(DiagramPath(plan.PlanDir)));

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        // A missing diagram counts as stale — exit 2, the same "regenerate" signal, NOT a
        // genuine harness error (exit 1).
        Assert.Equal(StaleExitCode, exit);
        Assert.NotEqual(ExitCodes.HarnessError, exit);
        Assert.Contains("missing", output);
        Assert.Contains("guardrails graph", output);
    }

    [Fact]
    public async Task Graph_CheckOnLoadError_ExitsHarnessError_NotStale()
    {
        // A genuine load/validate failure (a folder with no guardrails.json) must front-door
        // through load/validate and exit 1 — NOT the stale "regenerate" signal (2). CI relies on
        // this distinction: "the plan is broken" (1) vs "regenerate the diagram" (2).
        string brokenFolder = Path.Combine(
            Path.GetTempPath(), "guardrails-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(brokenFolder);
        try
        {
            (int exit, _) = await InvokeCapturingAsync("graph", brokenFolder, "--check");

            Assert.Equal(ExitCodes.HarnessError, exit);
            Assert.NotEqual(StaleExitCode, exit);
        }
        finally
        {
            Directory.Delete(brokenFolder, recursive: true);
        }
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
        // The structure-only caption belongs to the written document, NOT --stdout.
        Assert.DoesNotContain(DiagramCaption, output);
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
    public async Task Graph_Caption_InWrittenFileButNotStdout()
    {
        // The structure-only caption is part of the written document only. The same plan, two
        // invocations: the file carries the caption; --stdout (the raw diagram) does not.
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        (int writeExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, writeExit);

        string fileContent = await File.ReadAllTextAsync(
            DiagramPath(plan.PlanDir), TestContext.Current.CancellationToken);
        Assert.Contains(DiagramCaption, fileContent);

        (int stdoutExit, string stdout) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");
        Assert.Equal(ExitCodes.Success, stdoutExit);
        Assert.DoesNotContain(DiagramCaption, stdout);
    }

    [Fact]
    public async Task Graph_CheckAfterEditingGuardrailDescription_ExitsZero()
    {
        // Issue #222: the drawn label is ALWAYS the guardrail's Name, never its Description.
        // Adding a sidecar description changes only the click tooltip (diagram.html-only,
        // unhashed) — the diagram stays FRESH.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        SetGuardrailDescription(plan.PlanDir, "01-first", "Build passes with zero warnings");

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Graph_CheckAfterRenamingGuardrail_ExitsStale()
    {
        // Contrast the test above: renaming the guardrail changes its Name — the drawn label
        // (issue #222) — so the diagram IS stale, regardless of whether a description is present.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        RenameGuardrail(plan.PlanDir, "01-first", "01-check", "01-verify-build");

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(StaleExitCode, exit);
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

    [Fact]
    public async Task Graph_Default_WritesLegend_AfterTheCaption_StatingColorAndTiming()
    {
        // SSOT §10: a plain Markdown legend block after the fenced mermaid block, stating both the
        // colour mapping and the before/after timing (not just a bare category name) — the replacement
        // for the removed nested-box visual cue.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        string content = await File.ReadAllTextAsync(DiagramPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Contains("Legend", content);
        Assert.Contains("Preflight", content);
        Assert.Contains("Guardrail", content);
        Assert.Contains("BEFORE", content);
        Assert.Contains("AFTER", content);

        int fenceClose = content.LastIndexOf("```", StringComparison.Ordinal);
        int legend = content.IndexOf("**Legend**", StringComparison.Ordinal);
        Assert.True(legend > fenceClose, "the legend must follow the closing mermaid fence");
    }

    [Fact]
    public async Task Graph_CheckAfterHandEditingOnlyTheLegendText_StaysFresh_ExitsZero()
    {
        // The load-bearing hash-exclusion contract (SSOT §10): the legend lives OUTSIDE the hashed
        // semantic content — same treatment as the classDef color lines. Hand-editing ONLY the
        // legend wording (never the mermaid fence) must NOT make `graph --check` report stale;
        // getting this wrong would make --check flap on every plan whenever legend wording changes.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeCapturingAsync("graph", plan.PlanDir);

        string diagramPath = DiagramPath(plan.PlanDir);
        string content = await File.ReadAllTextAsync(diagramPath, TestContext.Current.CancellationToken);
        Assert.Contains("**Legend**", content);

        string edited = content.Replace(
            "**Legend**", "**Legend (hand-edited wording, totally different)**", StringComparison.Ordinal);
        Assert.NotEqual(content, edited); // sanity: the file DID change
        await File.WriteAllTextAsync(diagramPath, edited, TestContext.Current.CancellationToken);

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.Success, exit);
    }

    /// <summary>
    /// Add a second guardrail to an existing <see cref="ScriptPlanBuilder"/> task by writing a
    /// sibling guardrail file directly into <paramref name="taskDir"/>, mirroring the
    /// OS-appropriate script the builder emits. Takes the TASK FOLDER (not planDir + id) so it
    /// serves a flat plan's <c>tasks/&lt;id&gt;/</c> and a waved plan's
    /// <c>&lt;wave&gt;/tasks/&lt;id&gt;/</c> alike. The new guardrail's Name (<c>02-extra</c>) is a
    /// DRAWN node label, so it must appear in the regenerated diagram of whichever scope owns the
    /// task — which is exactly what the issue #447 tests assert.
    /// </summary>
    private static void AddSecondGuardrail(string taskDir)
    {
        string guardrailsDir = Path.Combine(taskDir, "guardrails");
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
    /// <c>ScriptPlanBuilder</c>-emitted guardrail (<c>01-check.{ps1,sh}</c>). Since issue #222 the
    /// drawn label is always the guardrail's Name, never its description, so this only changes the
    /// click tooltip (diagram.html-only, unhashed) — it does NOT move the staleness key.
    /// </summary>
    private static void SetGuardrailDescription(string planDir, string taskId, string description)
    {
        string guardrailsDir = Path.Combine(planDir, "tasks", taskId, "guardrails");
        string sidecarPath = Path.Combine(guardrailsDir, "01-check.json");
        File.WriteAllText(sidecarPath, $$"""{ "description": "{{description}}" }""");
    }

    /// <summary>
    /// Rename the task's guardrail file (script + optional JSON sidecar, if present) from
    /// <paramref name="oldName"/> to <paramref name="newName"/> — changes the guardrail's Name,
    /// the drawn label (issue #222), so the diagram becomes stale.
    /// </summary>
    private static void RenameGuardrail(string planDir, string taskId, string oldName, string newName)
    {
        string guardrailsDir = Path.Combine(planDir, "tasks", taskId, "guardrails");
        string scriptExt = OperatingSystem.IsWindows() ? ".ps1" : ".sh";
        File.Move(
            Path.Combine(guardrailsDir, oldName + scriptExt),
            Path.Combine(guardrailsDir, newName + scriptExt));

        string oldSidecar = Path.Combine(guardrailsDir, oldName + ".json");
        if (File.Exists(oldSidecar))
        {
            File.Move(oldSidecar, Path.Combine(guardrailsDir, newName + ".json"));
        }
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

    // -----------------------------------------------------------------------------------------
    // Wave-scoped graph (issue #355)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Graph_WaveFolder_WritesDiagramInWaveDir_ExitsZero()
    {
        // guardrails graph <plan>/<wave-NN-slug> must write diagram.md into the WAVE folder
        // (not the plan root) and exit 0. The diagram must contain the wave's tasks and the
        // standard provenance comment + flowchart marker.
        using var plan = new ScriptPlanBuilder();
        ScriptPlanBuilder.WaveBuilder wave = plan.AddWave("wave-01-foundation");
        wave.AddTask("01-write").AddTask("02-verify", dependsOn: "wave-01-foundation/01-write");

        string waveDirPath = wave.WaveDir;
        (int exit, string output) = await InvokeCapturingAsync("graph", waveDirPath);

        Assert.Equal(ExitCodes.Success, exit);

        string diagramPath = Path.Combine(waveDirPath, "diagram.md");
        Assert.True(File.Exists(diagramPath), "diagram.md must be written to the wave folder");

        string content = await File.ReadAllTextAsync(diagramPath, TestContext.Current.CancellationToken);
        Assert.Contains("flowchart TD", content);
        Assert.Contains("source-sha256=", content);

        // Only the wave's tasks should appear in the diagram.
        Assert.Contains("01-write", content);
        Assert.Contains("02-verify", content);
    }

    [Fact]
    public async Task Graph_WaveFolder_CheckAfterGenerate_ExitsZero()
    {
        // A wave-folder generate followed by --check must report FRESH (exit 0) — the source
        // hash embedded in the wave's diagram.md must agree with the recomputed wave-scoped hash.
        using var plan = new ScriptPlanBuilder();
        ScriptPlanBuilder.WaveBuilder wave = plan.AddWave("wave-01-foundation");
        wave.AddTask("01-write");

        string waveDirPath = wave.WaveDir;

        (int generateExit, _) = await InvokeCapturingAsync("graph", waveDirPath);
        Assert.Equal(ExitCodes.Success, generateExit);

        (int checkExit, _) = await InvokeCapturingAsync("graph", waveDirPath, "--check");
        Assert.Equal(ExitCodes.Success, checkExit);
    }

    // -----------------------------------------------------------------------------------------
    // Waved PLAN ROOT: every wave's diagram is written and checked (issue #447)
    //
    // The defect these guard: `guardrails graph <waved-plan>` regenerated ONLY the plan-level
    // diagram while `--check` validated ONLY the plan-level diagram. A waved plan therefore could
    // not be brought fresh by the documented command, and `--check` reported exit 0 over
    // demonstrably stale per-wave files — a false "fresh" that `/guardrails-review` (which
    // mandates `graph --check` and branches on its exit code) recorded as fact, and that the next
    // run then silently repaired by rewriting tracked files mid-flight.
    // -----------------------------------------------------------------------------------------

    private const string Wave1 = "wave-01-foundation";
    private const string Wave2 = "wave-02-build";

    /// <summary>Build a 2-wave plan, each wave holding one task.</summary>
    private static ScriptPlanBuilder TwoWavePlan()
    {
        var plan = new ScriptPlanBuilder();
        plan.AddWave(Wave1).AddTask("01-write");
        plan.AddWave(Wave2).AddTask("01-compile");
        return plan;
    }

    /// <summary>Absolute path to one of <see cref="TwoWavePlan"/>'s wave folders.</summary>
    private static string WaveDir(ScriptPlanBuilder plan, string waveName) =>
        Path.Combine(plan.PlanDir, waveName);

    /// <summary>
    /// Bring EVERY diagram of a <see cref="TwoWavePlan"/> into existence by both routes — the plan
    /// root and each wave folder individually. Tests that then perturb something start from the
    /// incident's actual state (all files present and fresh), so they fail on what the defect got
    /// wrong (the freshness REPORT and the regenerated CONTENT) rather than trivially, on files
    /// that were never written in the first place.
    /// </summary>
    private static async Task GenerateEveryDiagramAsync(ScriptPlanBuilder plan)
    {
        await InvokeCapturingAsync("graph", plan.PlanDir);
        await InvokeCapturingAsync("graph", WaveDir(plan, Wave1));
        await InvokeCapturingAsync("graph", WaveDir(plan, Wave2));
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_WritesPlanLevelAndEveryWaveDiagram_AndNamesThemAll()
    {
        // ONE `guardrails graph <plan>` must produce EVERY diagram the plan owns — the plan-level
        // pair plus each wave's pair — and say so, one line per file. The single "Wrote <plan>/
        // diagram.md" line it replaces is why nobody noticed only one file was ever produced.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave1 = WaveDir(plan, Wave1);
        string wave2 = WaveDir(plan, Wave2);

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);

        foreach (string dir in new[] { plan.PlanDir, wave1, wave2 })
        {
            Assert.True(File.Exists(Path.Combine(dir, "diagram.md")),
                $"diagram.md must be written to {dir}");
            Assert.True(File.Exists(Path.Combine(dir, "diagram.html")),
                $"diagram.html must be written to {dir}");
            Assert.Contains(Path.Combine(dir, "diagram.md"), output, StringComparison.Ordinal);
        }

        // Exactly ONE "Diagram (interactive):" line survives the fan-out — plan-breakdown's
        // SKILL.md Step 7 relays that single line verbatim (#249/#256), so a per-wave link each
        // would leave "the" link ambiguous.
        Assert.Single(output.Split('\n'), l => l.Contains("Diagram (interactive)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_AfterPerturbingAWaveTask_CheckReportsWaveStale_AndRegenerateUpdatesThatWaveDiagram()
    {
        // THE issue #447 reproduction, verbatim: add one guardrail file to a WAVE task, then
        // check → regenerate → check. Before the fix: `--check` flagged the plan-level diagram
        // (its hash folds every wave's tasks), the regenerate rewrote only that file, and the
        // follow-up `--check` returned 0 while the wave's own diagram still had no trace of the
        // new guardrail — from that point the staleness was invisible.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave2 = WaveDir(plan, Wave2);

        await GenerateEveryDiagramAsync(plan);

        (int freshExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, freshExit);

        string wave2Diagram = Path.Combine(wave2, "diagram.md");
        Assert.True(File.Exists(wave2Diagram), "the wave's diagram must exist before it can go stale");
        Assert.DoesNotContain("02-extra",
            await File.ReadAllTextAsync(wave2Diagram, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        // Perturb a WAVE task's guardrails/ folder — the issue's `printf 'exit 0' > .../09-probe.ps1`.
        AddSecondGuardrail(Path.Combine(wave2, "tasks", "01-compile"));

        (int staleExit, string staleOutput) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(StaleExitCode, staleExit);
        Assert.NotEqual(ExitCodes.HarnessError, staleExit);
        // The report must name the WAVE whose diagram drifted, not merely "diagram.md is stale" —
        // on a 4-wave plan that is the difference between an actionable line and a shrug.
        Assert.Contains("wave-02-build/diagram.md is stale", staleOutput, StringComparison.Ordinal);
        Assert.Contains("guardrails graph", staleOutput, StringComparison.Ordinal);

        (int regenExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, regenExit);

        // The heart of it: the regenerate actually updated the PER-WAVE file.
        Assert.Contains("02-extra",
            await File.ReadAllTextAsync(wave2Diagram, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        (int afterExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, afterExit);
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_CheckWithOnlyAWaveDiagramStale_ExitsStale_NotFalselyFresh()
    {
        // The sharpest form of the false-"fresh" claim: leave the plan-level diagram genuinely
        // fresh and stale ONLY one wave's file. Nothing about the plan-level hash can reveal this,
        // so a check that looks only at the plan root MUST return 0 here — which is precisely the
        // under-report that let a review pass record "diagram fresh" over two stale waves.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave2 = WaveDir(plan, Wave2);

        await GenerateEveryDiagramAsync(plan);

        string wave2Diagram = Path.Combine(wave2, "diagram.md");
        string content = await File.ReadAllTextAsync(wave2Diagram, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            wave2Diagram,
            content.Replace("source-sha256=", "source-sha256=dead", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(StaleExitCode, exit);
        Assert.Contains("wave-02-build/diagram.md is stale", output, StringComparison.Ordinal);
        // The plan-level diagram is untouched and genuinely fresh — it must NOT be reported.
        Assert.DoesNotContain("\ndiagram.md is stale", "\n" + output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_CheckWithAWaveDiagramMissingEntirely_ExitsStale()
    {
        // The real-incident case: wave-04's diagram.md did not exist AT ALL until a run created it.
        // A missing per-wave diagram.md is staleness (exit 2), exactly as a missing plan-level one is.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave2 = WaveDir(plan, Wave2);

        await GenerateEveryDiagramAsync(plan);

        File.Delete(Path.Combine(wave2, "diagram.md"));
        File.Delete(Path.Combine(wave2, "diagram.html"));

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");

        Assert.Equal(StaleExitCode, exit);
        Assert.NotEqual(ExitCodes.HarnessError, exit);
        Assert.Contains("wave-02-build/diagram.md missing", output, StringComparison.Ordinal);

        // And the documented command restores it.
        await InvokeCapturingAsync("graph", plan.PlanDir);
        (int afterExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, afterExit);
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_NoHtml_SkipsEveryScope_AndMissingWaveHtmlIsNotStale()
    {
        // --no-html is a whole-invocation flag, so it suppresses the companion at EVERY scope; the
        // missing-html rule therefore has to hold per-wave for the same reason it holds at the plan
        // level — otherwise --check would exit 2 forever for anyone who uses --no-html.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave1 = WaveDir(plan, Wave1);
        string wave2 = WaveDir(plan, Wave2);

        (int exit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--no-html");
        Assert.Equal(ExitCodes.Success, exit);

        foreach (string dir in new[] { plan.PlanDir, wave1, wave2 })
        {
            Assert.True(File.Exists(Path.Combine(dir, "diagram.md")));
            Assert.False(File.Exists(Path.Combine(dir, "diagram.html")),
                $"--no-html must not write diagram.html at {dir}");
        }

        (int checkExit, _) = await InvokeCapturingAsync("graph", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, checkExit);
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_AndWaveFolderInvocation_ProduceByteIdenticalWaveDiagrams()
    {
        // The two routes to a wave's diagram — `graph <plan>` (the #447 fan-out) and
        // `graph <plan>/<wave>` (issue #355) — must go through ONE projection and ONE writer. If
        // they ever diverge, running one would leave the other reporting stale, and the run's own
        // wave-boundary regeneration (RenderWaveScoped, the third caller of that writer) would
        // dirty tracked files on every run.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave1 = WaveDir(plan, Wave1);

        await InvokeCapturingAsync("graph", plan.PlanDir);
        byte[] fromPlanRoot = await File.ReadAllBytesAsync(
            Path.Combine(wave1, "diagram.md"), TestContext.Current.CancellationToken);
        byte[] htmlFromPlanRoot = await File.ReadAllBytesAsync(
            Path.Combine(wave1, "diagram.html"), TestContext.Current.CancellationToken);

        File.Delete(Path.Combine(wave1, "diagram.md"));
        File.Delete(Path.Combine(wave1, "diagram.html"));

        (int waveExit, _) = await InvokeCapturingAsync("graph", wave1);
        Assert.Equal(ExitCodes.Success, waveExit);

        Assert.Equal(fromPlanRoot, await File.ReadAllBytesAsync(
            Path.Combine(wave1, "diagram.md"), TestContext.Current.CancellationToken));
        Assert.Equal(htmlFromPlanRoot, await File.ReadAllBytesAsync(
            Path.Combine(wave1, "diagram.html"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Graph_WavedPlanRoot_Stdout_PrintsPlanLevelOnly_AndWritesNothing()
    {
        // --stdout stays a "show me the diagram" affordance, not a regeneration: it prints the
        // PLAN-LEVEL diagram and writes nothing anywhere — including into the wave folders.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string wave1 = WaveDir(plan, Wave1);
        string wave2 = WaveDir(plan, Wave2);

        (int exit, string output) = await InvokeCapturingAsync("graph", plan.PlanDir, "--stdout");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Single(output.Split('\n'), l => l.Contains("flowchart TD", StringComparison.Ordinal));

        foreach (string dir in new[] { plan.PlanDir, wave1, wave2 })
        {
            Assert.False(File.Exists(Path.Combine(dir, "diagram.md")), $"--stdout must write nothing at {dir}");
            Assert.False(File.Exists(Path.Combine(dir, "diagram.html")), $"--stdout must write nothing at {dir}");
        }
    }
}
