using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Breakdown;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails lock</c> through the real CLI pipeline against temp plan folders
/// (SSOT §10): default write, <c>--check</c> clean/stale/missing, the diagram.md/state
/// exclusion end-to-end, and <c>--diff</c> exit codes. Asserts on exit codes and the lock
/// file on disk (no console capture) so it stays parallel-safe; the per-file classification
/// text is covered by <c>BreakdownDiffTests</c>.
/// </summary>
public sealed class LockCliTests
{
    private static async Task<int> InvokeAsync(params string[] args)
    {
        var root = new RootCommand("test root");
        root.Add(LockCommand.Create());
        return await root.Parse(args).InvokeAsync();
    }

    private static string LockPath(string planDir) => Path.Combine(planDir, BreakdownManifest.FileName);

    private static string GuardrailFile(string planDir, string taskId) =>
        Path.Combine(planDir, "tasks", taskId, "guardrails",
            OperatingSystem.IsWindows() ? "01-check.ps1" : "01-check.sh");

    private static void AddGuardrail(string planDir, string taskId)
    {
        bool ps = OperatingSystem.IsWindows();
        string file = Path.Combine(planDir, "tasks", taskId, "guardrails", ps ? "02-extra.ps1" : "02-extra.sh");
        File.WriteAllText(file, ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
    }

    [Fact]
    public async Task Lock_Default_WritesLockFile_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        int exit = await InvokeAsync("lock", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(LockPath(plan.PlanDir)), "default run must write guardrails.lock");
        Assert.NotNull(BreakdownManifest.Read(plan.PlanDir));
    }

    [Fact]
    public async Task Check_ImmediatelyAfterLock_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("lock", plan.PlanDir));
        Assert.Equal(ExitCodes.Success, await InvokeAsync("lock", plan.PlanDir, "--check"));
    }

    [Fact]
    public async Task Check_AfterEditingGuardrail_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        // A human edits a guardrail script — content drift.
        await File.WriteAllTextAsync(GuardrailFile(plan.PlanDir, "01-first"),
            "Test-Path a, b\nexit 0\n", TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.HarnessError, await InvokeAsync("lock", plan.PlanDir, "--check"));
    }

    [Fact]
    public async Task Check_AfterAddingGuardrail_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        AddGuardrail(plan.PlanDir, "01-first");

        Assert.Equal(ExitCodes.HarnessError, await InvokeAsync("lock", plan.PlanDir, "--check"));
    }

    [Fact]
    public async Task Check_WithoutLock_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        Assert.False(File.Exists(LockPath(plan.PlanDir)));
        Assert.Equal(ExitCodes.HarnessError, await InvokeAsync("lock", plan.PlanDir, "--check"));
    }

    [Fact]
    public async Task Check_AfterWritingDiagramAndState_StaysClean()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        // Generated/runtime artifacts must NOT count as drift (SSOT §10 exclusions).
        await File.WriteAllTextAsync(Path.Combine(plan.PlanDir, "diagram.md"), "<!-- generated -->",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(plan.PlanDir, "state"));
        await File.WriteAllTextAsync(Path.Combine(plan.PlanDir, "state", "state.json"), "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, await InvokeAsync("lock", plan.PlanDir, "--check"));
    }

    [Fact]
    public async Task Diff_WithoutLock_ExitsOne()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        Assert.Equal(ExitCodes.HarnessError, await InvokeAsync("lock", plan.PlanDir, "--diff"));
    }

    [Fact]
    public async Task Diff_Clean_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, await InvokeAsync("lock", plan.PlanDir, "--diff"));
    }

    [Fact]
    public async Task Diff_WithDrift_ExitsZero_AndWritesNothing()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);
        string lockBefore = await File.ReadAllTextAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);

        AddGuardrail(plan.PlanDir, "01-first");

        Assert.Equal(ExitCodes.Success, await InvokeAsync("lock", plan.PlanDir, "--diff"));
        // --diff is a report: it never rewrites the lock.
        string lockAfter = await File.ReadAllTextAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);
        Assert.Equal(lockBefore, lockAfter);
    }

    [Fact]
    public async Task Lock_MissingFolder_ExitsOne()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-plan-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(ExitCodes.HarnessError, await InvokeAsync("lock", missing));
    }
}
