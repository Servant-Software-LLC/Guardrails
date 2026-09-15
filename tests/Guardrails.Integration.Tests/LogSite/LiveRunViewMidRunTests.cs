using System.Text.RegularExpressions;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Issues #713 and #714 through the whole of <c>guardrails run</c>, not only its composition seam. A real run is
/// held mid-flight by a gated task, and what the run says about its own log server is read while the run is still
/// going: the live run view's Status column (#713), plus the offline notices on the pages it is writing and the
/// diagram link it printed (#714). That is the moment the maintainer asked, of plan 39's run, whether the
/// <c>attempt 1</c> values came from tasks that had completed, and whether the run was still going at all.
/// <para>
/// <see cref="LiveRunViewStatusTests"/> and <see cref="LiveLinksTests"/> prove what <c>BuildObserverChain</c> does
/// once it is handed the server. Only a real run proves that <c>RunCommand</c> hands it over. A seam that works in
/// xUnit while the command never wires it is the defect this repo keeps finding at exactly this kind of boundary.
/// </para>
/// </summary>
public sealed class LiveRunViewMidRunTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The longest the held task's action may take to start. A bound on a wait, so a broken run fails the test
    /// instead of hanging it; nothing here asserts how fast anything is.
    /// </summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task MidRun_TheLiveRunViewTellsStatesApart_AndEveryLiveLinkNamesTheRunsOwnServer()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-done")
            .AddTask("02-held", true, true, "01-done")
            .AddTask("03-waiting", true, true, "02-held");
        using var gate = new Gate();
        File.WriteAllText(plan.ActionPath("02-held"), gate.ActionScript());

        var io = new StringConsoleIo();
        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<int> run = Task.Run(
            () => CommandFactory.BuildRootCommand(io).Parse(["run", plan.PlanDir, "--no-ui"]).InvokeAsync(configuration: null, ct),
            ct);

        int exit;
        string baseUrl = string.Empty;
        try
        {
            // 02's action is running, so its TaskStarting has fired, and with it the during-run index write: the
            // harness raises that event before it runs the attempt. 01 settled before 02 could be scheduled.
            await gate.WaitUntilStartedAsync(run);

            string logsRoot = Assert.Single(Directory.GetDirectories(Path.Combine(plan.PlanDir, "logs")));
            string index = File.ReadAllText(Path.Combine(logsRoot, "index.html"));

            // The during-run index links a running task to the run's own server. That is how this test learns the
            // port without reading console output the run is still writing.
            Match live = Regex.Match(index, "href=\"(?<base>http://127\\.0\\.0\\.1:\\d+/)tasks/02-held\"");
            Assert.True(live.Success, $"the during-run index does not link 02-held to a live server:\n{index}");
            baseUrl = live.Groups["base"].Value;

            string html = await Http.GetStringAsync(baseUrl, ct);

            Assert.Equal("succeeded", LiveRunViewRows.Find(html, "01-done").Status);
            Assert.Equal("running", LiveRunViewRows.Find(html, "02-held").Status);
            Assert.Equal("pending", LiveRunViewRows.Find(html, "03-waiting").Status);

            // #714: opened as files, the pages this run is writing cannot poll. Their notices must send the reader to
            // this run's server, which is up, and not off to start another one with `guardrails logs`.
            Assert.Contains($"<a href=\"{baseUrl}\">", OfflineNotice.In(index), StringComparison.Ordinal);
            string diagram = File.ReadAllText(Path.Combine(logsRoot, "diagram.html"));
            Assert.Contains($"<a href=\"{baseUrl}diagram.html\">", OfflineNotice.In(diagram), StringComparison.Ordinal);
        }
        finally
        {
            // Always let the run finish before the fixture's folders are deleted underneath it.
            gate.Release();
            exit = await run;
        }

        Assert.Equal(ExitCodes.Success, exit);

        // #714: the diagram line the run printed at start names that same server's live copy.
        Assert.Contains($"Live status diagram: {baseUrl}diagram.html", io.OutText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Holds one task's action until the test releases it: the action marks that it started, then waits for a
    /// release file. Both files live outside the plan's workspace, so the task's own write scope never sees them.
    /// </summary>
    private sealed class Gate : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gr-713-gate-" + Guid.NewGuid().ToString("N"));

        public Gate() => Directory.CreateDirectory(_dir);

        private string Started => Path.Combine(_dir, "started");

        private string Released => Path.Combine(_dir, "release");

        public string ActionScript() => OperatingSystem.IsWindows()
            ? $$"""
               New-Item -ItemType File -Force -Path '{{Started}}' | Out-Null
               while (-not (Test-Path -LiteralPath '{{Released}}')) { Start-Sleep -Milliseconds 50 }
               Write-Output 'released'
               exit 0
               """
            : $$"""
               #!/usr/bin/env bash
               touch '{{Started}}'
               while [ ! -f '{{Released}}' ]; do sleep 0.05; done
               echo released
               exit 0
               """;

        public async Task WaitUntilStartedAsync(Task<int> run)
        {
            DateTime deadline = DateTime.UtcNow + StartBudget;
            while (!File.Exists(Started))
            {
                Assert.False(run.IsCompleted, "the run ended before the held task's action started");
                Assert.True(DateTime.UtcNow < deadline, $"the held task's action did not start within {StartBudget}");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        public void Release() => File.WriteAllText(Released, string.Empty);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
