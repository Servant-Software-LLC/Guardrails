using Guardrails.Cli;
using Guardrails.Core.Breakdown;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails lock</c> through the REAL composition root
/// (<see cref="CommandFactory.BuildRootCommand"/>) against temp plan folders (SSOT §11):
/// default write (path + file count), <c>--check</c> clean/drift/missing/corrupt, the
/// diagram.md/state exclusion end-to-end, <c>--diff</c> exit codes, and byte-identical re-locks.
/// Going through the factory also proves the command is actually wired in — a <c>lock</c> that
/// works only via a hand-built root but is missing from the factory would ship broken. Output is
/// captured with <see cref="StringConsoleIo"/> (no process-global console) so it stays
/// parallel-safe.
/// </summary>
public sealed class LockCliTests
{
    /// <summary>Exit code <c>--check</c>/<c>--diff</c> return for drift or a missing lock (SSOT §7/§11).</summary>
    private const int DriftExitCode = 2;

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
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
    public async Task Lock_Default_WritesLockFile_PrintsPathAndCount_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(LockPath(plan.PlanDir)), "default run must write guardrails.lock");
        Assert.NotNull(BreakdownManifest.Read(plan.PlanDir));

        // The "Wrote <path> (N file(s))" line names the lock and the captured file count.
        Assert.Contains("Wrote", output);
        Assert.Contains(BreakdownManifest.FileName, output);
        Assert.Contains("file(s)", output);
    }

    [Fact]
    public async Task Lock_RerunOnUnchangedFolder_ProducesByteIdenticalLock()
    {
        // No timestamp in the lock → a second lock on an unchanged folder is byte-identical
        // (a deterministic projection, no git churn — mirrors graph's diagram.md).
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        await InvokeAsync("lock", plan.PlanDir);
        byte[] first = await File.ReadAllBytesAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);

        await InvokeAsync("lock", plan.PlanDir);
        byte[] second = await File.ReadAllBytesAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Check_ImmediatelyAfterLock_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int lockExit, _) = await InvokeAsync("lock", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, lockExit);

        (int checkExit, _) = await InvokeAsync("lock", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, checkExit);
    }

    [Fact]
    public async Task Check_AfterEditingGuardrail_ExitsDrift_WithStaleLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        // A human edits a guardrail script — content drift.
        await File.WriteAllTextAsync(GuardrailFile(plan.PlanDir, "01-first"),
            "Test-Path a, b\nexit 0\n", TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--check");

        // Drift → the "re-lock" signal (exit 2), distinct from a genuine error (exit 1).
        Assert.Equal(DriftExitCode, exit);
        Assert.NotEqual(ExitCodes.HarnessError, exit);
        Assert.Contains("stale", output);
        Assert.Contains("guardrails lock", output);
    }

    [Fact]
    public async Task Check_AfterAddingGuardrail_ExitsDrift()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        AddGuardrail(plan.PlanDir, "01-first");

        (int exit, _) = await InvokeAsync("lock", plan.PlanDir, "--check");
        Assert.Equal(DriftExitCode, exit);
    }

    [Fact]
    public async Task Check_WithoutLock_ExitsDrift_WithMissingLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        Assert.False(File.Exists(LockPath(plan.PlanDir)));

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--check");

        // A missing lock counts as the "re-lock" signal (exit 2), NOT a genuine error (exit 1).
        Assert.Equal(DriftExitCode, exit);
        Assert.NotEqual(ExitCodes.HarnessError, exit);
        Assert.Contains("missing", output);
        Assert.Contains("guardrails lock", output);
    }

    [Fact]
    public async Task Check_CorruptLock_ExitsHarnessError_NotDrift()
    {
        // A present-but-unparseable lock is a genuine error (exit 1), distinct from the missing
        // lock "re-lock" signal (exit 2): CI can tell "the lock is broken" from "re-lock me".
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await File.WriteAllTextAsync(LockPath(plan.PlanDir), "{ not json",
            TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--check");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.NotEqual(DriftExitCode, exit);
        Assert.Contains("corrupt", output);
    }

    [Fact]
    public async Task Check_AfterWritingDiagramAndState_StaysClean()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        // Generated/runtime artifacts must NOT count as drift (SSOT §11 exclusions).
        await File.WriteAllTextAsync(Path.Combine(plan.PlanDir, "diagram.md"), "<!-- generated -->",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(plan.PlanDir, "state"));
        await File.WriteAllTextAsync(Path.Combine(plan.PlanDir, "state", "state.json"), "{}",
            TestContext.Current.CancellationToken);

        (int exit, _) = await InvokeAsync("lock", plan.PlanDir, "--check");
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Diff_WithoutLock_ExitsDrift()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--diff");

        // No BASE to diff against → the actionable "run guardrails lock first" signal (exit 2).
        Assert.Equal(DriftExitCode, exit);
        Assert.Contains("missing", output);
    }

    [Fact]
    public async Task Diff_CorruptLock_ExitsHarnessError()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await File.WriteAllTextAsync(LockPath(plan.PlanDir), "{ not json",
            TestContext.Current.CancellationToken);

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--diff");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("corrupt", output);
    }

    [Fact]
    public async Task Diff_Clean_ExitsZero_WithNoChangesLine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--diff");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("No changes", output);
    }

    [Fact]
    public async Task Diff_WithDrift_ExitsZero_PrintsAdded_AndWritesNothing()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("lock", plan.PlanDir);
        string lockBefore = await File.ReadAllTextAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);

        AddGuardrail(plan.PlanDir, "01-first");

        (int exit, string output) = await InvokeAsync("lock", plan.PlanDir, "--diff");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("ADDED", output);

        // --diff is a report: it never rewrites the lock.
        string lockAfter = await File.ReadAllTextAsync(LockPath(plan.PlanDir), TestContext.Current.CancellationToken);
        Assert.Equal(lockBefore, lockAfter);
    }

    [Fact]
    public async Task Lock_MissingFolder_ExitsHarnessError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-plan-" + Guid.NewGuid().ToString("N"));

        (int exit, _) = await InvokeAsync("lock", missing);

        Assert.Equal(ExitCodes.HarnessError, exit);
    }
}
