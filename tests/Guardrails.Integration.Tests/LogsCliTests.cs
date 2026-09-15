using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using Guardrails.Core.State;
using Guardrails.Integration.Tests.LogSite;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, runExit);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // the serve loop is Task.Delay(Infinite, token) — a cancelled token returns at once

        (int exit, string output) = await InvokeAsync(cts.Token, "logs", plan.PlanDir, "--no-open");

        Assert.Equal(ExitCodes.Success, exit);
        // The canonical all-tasks entry is the static index file, named by its absolute path.
        Assert.Contains("All tasks (static log site):", output);
        Assert.Contains(Path.Combine("index.html"), output);
        // And the live tailing server is still offered (the static index links running tasks to it).
        // #573: the label is now "Live run view", because the page it names lists every task with live
        // links instead of being a dead end pointing at a file it cannot open.
        Assert.Contains("Live run view", output);
        // The (re)generated static site exists on disk under logs/<runId>/.
        string logsDir = Path.Combine(plan.PlanDir, "logs");
        string[] indexes = Directory.GetFiles(logsDir, "index.html", SearchOption.AllDirectories);
        Assert.NotEmpty(indexes);
    }

    [Fact]
    public async Task Logs_LiveRunView_ReportsEachTasksJournalStatus_ReadAgainOnEveryLoad()
    {
        // Issue #713, for the other server that renders the live run view. `guardrails logs` runs no harness, so
        // there is no in-process status map for it to share. The journal on disk is its only word on each task,
        // and the static index it has just rendered came from the same journal. It must be READ AGAIN on every
        // load, not captured at startup: this command is the documented way to attach to a run still in flight
        // (#552), the page reloads itself every few seconds, and a startup snapshot would go on showing a task
        // that has since finished as running for as long as the tab stayed open.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, runExit);

        CancellationToken ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        (Task<(int ExitCode, string Output)> serving, string baseUrl) = await StartLogsServerAsync(plan.PlanDir, stop.Token);

        int exit;
        try
        {
            Assert.Equal("succeeded", LiveRunViewRows.Find(await Http.GetStringAsync(baseUrl, ct), "01-first").Status);

            // The harness rewrites run.json as tasks settle. Rewrite it the same way, atomically and through the
            // journal's own serializer, then load the page again: it must follow the file.
            string journalPath = RunJournal.PathFor(plan.PlanDir);
            JournalDocument journal = JournalReader.Read(journalPath);
            JournalDocument rewritten = journal with
            {
                Tasks = journal.Tasks.ToDictionary(
                    kv => kv.Key, kv => kv.Value with { Status = JournalTaskStatus.NeedsHuman }, StringComparer.Ordinal)
            };
            AtomicFile.WriteAllText(journalPath, JsonSerializer.Serialize(rewritten, JournalJson.Options));

            Assert.Equal("needs-human", LiveRunViewRows.Find(await Http.GetStringAsync(baseUrl, ct), "01-first").Status);
        }
        finally
        {
            stop.Cancel(); // the Ctrl-C signal
            (exit, _) = await serving;
        }

        Assert.Equal(ExitCodes.Success, exit);
    }

    /// <summary>
    /// Start <c>guardrails logs --no-open</c> serving in the background and return once its live run view
    /// answers. The port is chosen here so the test knows the URL without reading console output the command
    /// is still writing. A caller-chosen port gets a single bind attempt, so a port lost to another process
    /// between probe and bind ends that invocation, and a fresh port is tried.
    /// </summary>
    private static async Task<(Task<(int ExitCode, string Output)> Serving, string BaseUrl)> StartLogsServerAsync(
        string planDir, CancellationToken stop)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = FreeLoopbackPort();
            string baseUrl = $"http://127.0.0.1:{port}/";
            Task<(int ExitCode, string Output)> serving = Task.Run(
                () => InvokeAsync(stop, "logs", planDir, "--no-open", "--port", port.ToString(CultureInfo.InvariantCulture)),
                ct);

            while (!serving.IsCompleted)
            {
                try
                {
                    using HttpResponseMessage response = await Http.GetAsync(baseUrl, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        return (serving, baseUrl);
                    }
                }
                catch (HttpRequestException)
                {
                    // not listening yet
                }

                await Task.Delay(50, ct);
            }
        }

        throw new InvalidOperationException("guardrails logs could not bind a free loopback port in 10 attempts");
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
