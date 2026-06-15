using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails logs</c> through the real composition root for its non-blocking paths:
/// an invalid folder errors, and a never-run plan exits cleanly with a hint instead of serving.
/// The serving path itself runs until Ctrl-C, so it is exercised at the <c>LogServer</c> level
/// (see <see cref="LogServerTests"/>) rather than by invoking the blocking command here.
/// </summary>
public sealed class LogsCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task Logs_NeverRunPlan_PrintsHint_ExitsZero_DoesNotBlock()
    {
        // No journal yet → nothing to view; the command must return rather than start a server.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("logs", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("No run journal", output);
    }

    [Fact]
    public async Task Logs_MissingFolder_ExitsHarnessError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-plan-" + Guid.NewGuid().ToString("N"));

        (int exit, _) = await InvokeAsync("logs", missing);

        Assert.Equal(ExitCodes.HarnessError, exit);
    }
}
