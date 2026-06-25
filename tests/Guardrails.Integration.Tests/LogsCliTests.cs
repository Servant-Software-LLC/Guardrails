using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails logs</c> through the real composition root. The non-blocking paths — an
/// invalid folder errors, a never-run plan exits cleanly with a hint — run to completion directly.
/// The serving path blocks until Ctrl-C, so it is driven with a pre-cancelled token (the same signal
/// Ctrl-C delivers) to assert it advertises the canonical STATIC index file as the entry point and
/// (re)generates the static site for the run (issue #143) before returning cleanly.
/// </summary>
public sealed class LogsCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args) =>
        await InvokeAsync(CancellationToken.None, args);

    private static async Task<(int ExitCode, string Output)> InvokeAsync(CancellationToken token, params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync(configuration: null, token);
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

    [Fact]
    public async Task Logs_RunPlan_AdvertisesStaticIndex_AndRegeneratesSite()
    {
        // A run first lays down the journal + attempt logs; then `logs` is the post-mortem viewer.
        // Issue #143: its entry point is the canonical STATIC index file (logs/<runId>/index.html),
        // advertised by path; it (re)generates that site from the journal, then starts the tailing
        // backend. Drive the blocking serve with a pre-cancelled token (the Ctrl-C signal) so it
        // returns after advertising; --no-open keeps it headless.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, runExit);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // the serve loop is Task.Delay(Infinite, token) — a cancelled token returns at once

        (int exit, string output) = await InvokeAsync(cts.Token, "logs", plan.PlanDir, "--no-open");

        Assert.Equal(ExitCodes.Success, exit);
        // The canonical all-tasks entry is the static index file, named by its absolute path.
        Assert.Contains("All tasks (static log site):", output);
        Assert.Contains(Path.Combine("index.html"), output);
        // And the live tailing server is still offered (the static index links running tasks to it).
        Assert.Contains("Live tailing server", output);
        // The (re)generated static site exists on disk under logs/<runId>/.
        string logsDir = Path.Combine(plan.PlanDir, "logs");
        string[] indexes = Directory.GetFiles(logsDir, "index.html", SearchOption.AllDirectories);
        Assert.NotEmpty(indexes);
    }
}
