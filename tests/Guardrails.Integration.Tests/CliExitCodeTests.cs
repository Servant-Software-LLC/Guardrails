using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives the real CLI command pipeline end-to-end and asserts the SSOT §7 exit codes:
/// 0 all green, 1 validation/harness error, 2 ≥1 task failed/blocked.
/// </summary>
public sealed class CliExitCodeTests
{
    private static async Task<int> InvokeAsync(params string[] args)
    {
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create());
        root.Add(ValidateCommand.Create());
        return await root.Parse(args).InvokeAsync();
    }

    [Fact]
    public async Task Run_AllGreen_ExitsZero()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        int exitCode = await InvokeAsync("run", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task Run_FailingGuardrail_ExitsTwo()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first", guardrailPasses: false)
            .AddTask("02-second", dependsOn: "01-first");

        int exitCode = await InvokeAsync("run", plan.PlanDir);

        Assert.Equal(ExitCodes.TaskFailed, exitCode);
    }

    [Fact]
    public async Task Run_ActionFails_ExitsTwo()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first", actionSucceeds: false);

        int exitCode = await InvokeAsync("run", plan.PlanDir);

        Assert.Equal(ExitCodes.TaskFailed, exitCode);
    }

    [Fact]
    public async Task Validate_GoldenExample_ExitsZero()
    {
        int exitCode = await InvokeAsync("validate", GoldenExamplePath);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // NOTE: the M2/M4-era test "Run_PromptPlan_FailsFastWithHarnessError" was removed in M5.
    // Prompt actions/guardrails are now executed (no fail-fast). The prompt pipeline is
    // covered tokenlessly by the fake-CLI integration tests (FakeClaudeRunTests); a real run
    // of the golden example is the opt-in Reality Gate, not a default test.

    [Fact]
    public async Task Validate_MissingFolder_ExitsOne()
    {
        int exitCode = await InvokeAsync("validate", Path.Combine(Path.GetTempPath(), "no-such-plan-" + Guid.NewGuid()));
        Assert.Equal(ExitCodes.HarnessError, exitCode);
    }

    private static string GoldenExamplePath
    {
        get
        {
            // tests/Guardrails.Integration.Tests/bin/... up to repo root.
            string here = AppContext.BaseDirectory;
            string repoRoot = FindRepoRoot(here);
            return Path.Combine(repoRoot, "examples", "hello-guardrails", "hello-guardrails");
        }
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Guardrails.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Guardrails.sln) from " + start);
    }
}
