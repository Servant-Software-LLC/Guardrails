using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// #714 review, W1: <b>the live table and <c>--no-ui</c> must open the same surfaces around the run's server.</b>
/// <para>
/// A run's default mode, the live progress table, needs an interactive console, so no test can drive
/// <c>guardrails run</c> through it; every run test goes through <c>--no-ui</c>. The two modes used to hand the log
/// server's URLs and status source over on separate lines of <c>RunAsync</c>, and a review deleted all four of the
/// live-table branch's handoffs with every related test still green. Both modes now go through
/// <see cref="RunCommand.OpenRunSurfacesAsync"/>. This test opens it both ways around a real server and requires
/// everything a reader could see to match: the printed links and both pages' offline notices as the run starts,
/// then both notices, whether the server is serving, and the status it serves once an event has passed through
/// the chain.
/// </para>
/// </summary>
public sealed class RunSurfacesTests
{
    private const string ServerToken = "<server>/";
    private const string PlanToken = "<plan>";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task TheLiveTableAndNoUi_OpenIdenticalSurfaces_AroundTheRunsServer()
    {
        Surfaces noUi = await OpenAndReadAsync(liveTable: false);
        Surfaces live = await OpenAndReadAsync(liveTable: true);

        // Not two empty answers that happen to agree: --no-ui really does carry the run's server...
        Assert.Contains($"Live status diagram: {ServerToken}diagram.html", noUi.Printed, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{ServerToken}\">", noUi.IndexNoticeAtStart, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{ServerToken}diagram.html\">", noUi.DiagramNoticeAtStart, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{ServerToken}\">", noUi.IndexNoticeAfterEvent, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{ServerToken}diagram.html\">", noUi.DiagramNoticeAfterEvent, StringComparison.Ordinal);
        Assert.True(noUi.Serving);
        Assert.Equal("running", noUi.ServedStatus);

        // ...and the live table carries exactly the same.
        Assert.Equal(noUi, live);
    }

    /// <summary>What a reader of one run's surfaces could see, with that run's own server and folder normalized away.</summary>
    private sealed record Surfaces(
        string Printed,
        string IndexNoticeAtStart,
        string DiagramNoticeAtStart,
        string IndexNoticeAfterEvent,
        string DiagramNoticeAfterEvent,
        bool Serving,
        string? ServedStatus);

    private static async Task<Surfaces> OpenAndReadAsync(bool liveTable)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string runId = "test-run";
        string dir = Path.Combine(Path.GetTempPath(), "gr-w1-" + Guid.NewGuid().ToString("N"));
        string logsRoot = Path.Combine(dir, "logs", runId);
        Directory.CreateDirectory(logsRoot);
        try
        {
            var task = new TaskNode
            {
                Id = "01-alpha",
                Directory = Path.Combine(dir, "tasks", "01-alpha"),
                Description = "task 01-alpha",
                Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
                Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
            };
            var plan = new PlanDefinition
            {
                PlanDirectory = dir,
                Workspace = dir,
                Config = new RunConfig { Version = 1 },
                Tasks = [task]
            };

            LogServer? server = LogServer.TryStart(dir, runId, plan.Tasks, port: 0, TextWriter.Null);
            Assert.NotNull(server);
            await using (server)
            {
                string Normalize(string text) => text
                    .Replace(server!.BaseUrl, ServerToken, StringComparison.Ordinal)
                    .Replace(new Uri(dir).AbsoluteUri, PlanToken, StringComparison.Ordinal)
                    .Replace(dir, PlanToken, StringComparison.Ordinal);
                string Notice(string page) => Normalize(OfflineNotice.In(File.ReadAllText(Path.Combine(logsRoot, page))));

                var io = new StringConsoleIo();
                RunCommand.RunSurfaces surfaces = await RunCommand.OpenRunSurfacesAsync(
                    liveTable, logsRoot, runId, plan, diagramSeed: null, server, onRow: null, includeDetail: false,
                    allTasks: false, io);

                // Read before any event: the console observer writes events to the same stream, the live table
                // does not, and what is under test here is what the open itself printed and wrote.
                string printed = Normalize(io.OutText);
                string indexAtStart = Notice("index.html");
                string diagramAtStart = Notice("diagram.html");

                string? served = null;
                await using (surfaces.LiveTable)
                {
                    surfaces.Chain.TaskStarting(task);
                    if (server!.IsServing)
                    {
                        served = LiveRunViewRows.Find(await Http.GetStringAsync(server.BaseUrl, ct), "01-alpha").Status;
                    }
                }

                return new Surfaces(
                    printed, indexAtStart, diagramAtStart, Notice("index.html"), Notice("diagram.html"),
                    server!.IsServing, served);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
