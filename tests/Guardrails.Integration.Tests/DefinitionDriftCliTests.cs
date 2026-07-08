using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives the definition-drift halt (issue #274 Part A, SSOT §7.2) end-to-end through the real CLI on a
/// SERIAL plan (<see cref="StatePlanBuilder"/>, maxParallelism 1 — no git worktrees), so the drift is
/// detected purely from the JOURNAL's recorded <c>TaskDefinitionHash</c>. Asserts the observable
/// contract in exit-code + console-string terms ONLY (no new-type references), which is exactly why the
/// exact-repro test below is a genuine RED-BAR guard: it compiles against pre-Part-A code, where a plain
/// resume after editing a succeeded task's definition SKIPS-as-succeeded (exit 0) instead of halting.
/// Output is captured from a per-invocation <see cref="StringConsoleIo"/> — parallel-safe.
/// </summary>
public sealed class DefinitionDriftCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>Overwrite a task's action script body verbatim (OS-appropriate; changes the bytes → the hash).</summary>
    private static void EditActionBody(string planDir, string taskId, string body)
    {
        string path = Path.Combine(planDir, "tasks", taskId, StatePlanBuilder.ActionFileName);
        string content = StatePlanBuilder.UsePowerShell ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>Overwrite a task's task.json description verbatim (changes the bytes → the hash).</summary>
    private static void EditTaskJson(string planDir, string taskId, string description)
    {
        string path = Path.Combine(planDir, "tasks", taskId, "task.json");
        File.WriteAllText(path,
            $$"""
            {
              "description": "{{description}}",
              "dependsOn": []
            }
            """);
    }

    // ── the exact repro (RED-BAR): edit a succeeded task's action, plain resume must HALT not skip ──

    [Fact]
    public async Task EditingSucceededActionThenPlainResume_HaltsOnDefinitionDrift_ExitsTwo()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        // Phase 1: run to green. Both tasks settle succeeded and journal their definition hash.
        (int firstExit, _) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, firstExit);

        // Edit the already-succeeded task's action definition (a real definition change — the edit still
        // exits 0, so if the harness WRONGLY re-ran it the run would go green; the point is it must NOT
        // silently reuse the stale segment NOR silently re-run — it must HALT and report the drift).
        EditActionBody(plan.PlanDir, "01-first", "# edited after success\nexit 0");

        // Phase 2: a PLAIN resume (no --fresh). Pre-Part-A this printed
        // "01-first  skipped  already succeeded (resumed) - skipped" and exited 0. Part A halts.
        (int resumeExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.TaskFailed, resumeExit); // exit 2 — needs-human/actionable, NOT 1 or 0.
        Assert.Contains("DEFINITION DRIFT", output);
        Assert.Contains("01-first", output);
        // It must NOT have skipped the edited task as already-succeeded.
        Assert.DoesNotContain("already succeeded (resumed)", output);
        // The remediation the halt names.
        Assert.Contains("guardrails reset", output);
    }

    [Fact]
    public async Task EditingSucceededTaskJson_HaltsOnDefinitionDrift()
    {
        using var plan = new StatePlanBuilder().AddTask("01-only");

        (int firstExit, _) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, firstExit);

        EditTaskJson(plan.PlanDir, "01-only", "edited description drives a task.json byte change");

        (int resumeExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.TaskFailed, resumeExit);
        Assert.Contains("DEFINITION DRIFT", output);
        Assert.Contains("01-only", output);
    }

    // ── the regression side: an UNCHANGED plan resumes exactly as today (no false drift) ──

    [Fact]
    public async Task UnchangedPlan_PlainResume_SkipsAsSucceeded_NoDrift()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        (int firstExit, _) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, firstExit);

        // No edit — a plain resume must skip both and exit 0, never a false drift halt.
        (int resumeExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.Success, resumeExit);
        Assert.DoesNotContain("DEFINITION DRIFT", output);
        Assert.Contains("already succeeded (resumed)", output);
    }

    // ── --dry-run previews the halt honestly instead of a stale SKIP ──

    [Fact]
    public async Task DryRun_AfterEditingSucceededTask_PreviewsDriftHalt_NotSkip()
    {
        using var plan = new StatePlanBuilder().AddTask("01-only");

        (int firstExit, _) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, firstExit);

        EditActionBody(plan.PlanDir, "01-only", "# edited\nexit 0");

        (int dryExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--dry-run");

        // A dry run itself never fails — it exits 0 having touched nothing — but it must PREVIEW the halt.
        Assert.Equal(ExitCodes.Success, dryExit);
        Assert.Contains("HALT (definition drift)", output);
        Assert.Contains("would HALT on definition drift", output);
        Assert.DoesNotContain("SKIP (succeeded)", output);
    }

    [Fact]
    public async Task DryRun_Unchanged_PreviewsSkip_NotDrift()
    {
        using var plan = new StatePlanBuilder().AddTask("01-only");

        (int firstExit, _) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, firstExit);

        (int dryExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--dry-run");

        Assert.Equal(ExitCodes.Success, dryExit);
        Assert.Contains("SKIP (succeeded)", output);
        Assert.DoesNotContain("HALT (definition drift)", output);
        Assert.DoesNotContain("would HALT on definition drift", output);
    }
}
