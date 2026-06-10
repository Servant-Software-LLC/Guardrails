using System.CommandLine;
using System.Security.Cryptography;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails run --dry-run</c> through the real CLI pipeline and asserts the
/// M7 contract: it prints the waves preview and per-task resolution, marks resume SKIPs
/// after a partial run, exits 0, and — critically — never touches state (the <c>state/</c>
/// directory is left byte-for-byte identical).
/// </summary>
public sealed class DryRunCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeCapturingAsync(params string[] args)
    {
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create());
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

    [Fact]
    public async Task DryRun_FreshPlan_PrintsWavesAndResolution_ExitsZero_NoStateCreated()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--dry-run");

        Assert.Equal(ExitCodes.Success, exit);

        // Waves preview present.
        Assert.Contains("Wave 0:", output);
        Assert.Contains("Wave 1:", output);
        Assert.Contains("01-first", output);
        Assert.Contains("02-second", output);

        // Per-task resolution section present.
        Assert.Contains("Per-task resolution:", output);
        Assert.Contains("RETRY BUDGET", output);

        // Nothing was journaled run: no state directory should have been created.
        Assert.False(Directory.Exists(Path.Combine(plan.PlanDir, "state")),
            "a dry run must not create runtime state");
    }

    [Fact]
    public async Task DryRun_AfterPartialRun_ShowsSkipMarkers_AndLeavesStateUntouched()
    {
        // 01 succeeds, 02 fails its guardrail → a partial run that journals 01 as succeeded.
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second",
                guardrailBody: StatePlanBuilder.Fail("not yet"),
                dependsOn: "01-first");

        int runExit = await InvokeCapturingAsync_Run(plan.PlanDir);
        Assert.Equal(ExitCodes.TaskFailed, runExit);

        string stateDir = Path.Combine(plan.PlanDir, "state");
        Assert.True(Directory.Exists(stateDir), "the partial run should have created state");

        // Snapshot the entire state tree before the dry run.
        IReadOnlyDictionary<string, string> before = HashTree(stateDir);

        (int dryExit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--dry-run");

        Assert.Equal(ExitCodes.Success, dryExit);

        // 01 succeeded → it would be SKIPPED on resume; 02 failed → it would run.
        Assert.Contains("SKIP (succeeded)", output);
        Assert.Contains("01-first", output);
        Assert.Contains("would be SKIPPED", output);

        // The state tree is byte-for-byte identical — the dry run touched nothing.
        IReadOnlyDictionary<string, string> after = HashTree(stateDir);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DryRun_NoSuccesses_ReportsNoSkips()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--dry-run");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("no tasks would be skipped", output);
    }

    [Fact]
    public async Task Run_DeterministicPlan_OmitsTotalCostLine()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.Success, exit);
        // A script-only plan records no costUsd, so the run summary omits the cost line.
        Assert.DoesNotContain("Total prompt cost", output);
    }

    [Fact]
    public async Task Run_PromptPlan_PrintsTotalCostLine()
    {
        // The fake-claude runner records total_cost_usd; the run summary must aggregate it.
        using var plan = new FakeClaudePlanBuilder()
            .AddPromptTask("01-generate", mode: "fragment", cost: "0.0150");

        (int exit, string output) = await InvokeCapturingAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Total prompt cost: $0.0150", output);
    }

    private static async Task<int> InvokeCapturingAsync_Run(string planDir)
    {
        (int exit, _) = await InvokeCapturingAsync("run", planDir);
        return exit;
    }

    /// <summary>A path → SHA-256 map of every file under <paramref name="root"/>, for exact equality.</summary>
    private static IReadOnlyDictionary<string, string> HashTree(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
            map[relative.Replace('\\', '/')] = Convert.ToHexString(hash);
        }

        return map;
    }
}
