using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Issue #714, at the composition seam. A reader who opens a run's during-run pages as files gets an offline
/// notice, because a <c>file://</c> page cannot poll. That notice must point at the run's own live server whenever
/// there is one, and name <c>guardrails logs</c> only when there is not. The page cannot discover the server's
/// port, but the writers that produce it are built by <c>RunCommand.BuildObserverChain</c> around that server, so
/// they know it and write it in.
/// </summary>
public sealed class LiveLinksTests
{
    [Fact]
    public async Task WithTheRunsLogServer_BothOfflineNotices_LinkThatServersLivePages_AndKeepTheFallback()
    {
        using var site = new Site();
        await using LogServer server = site.StartServer();
        OnTheFlyDiagramObserver chain = site.Chain(server);

        chain.TaskStarting(site.AlphaTask); // any event rewrites both during-run pages

        // The live link first; `guardrails logs` still named for a reader whose run has since ended (#714 review).
        string indexNotice = OfflineNotice.In(site.Read("index.html"));
        Assert.Contains($"<a href=\"{server.BaseUrl}\">{server.BaseUrl}</a>", indexNotice, StringComparison.Ordinal);
        Assert.Contains("guardrails logs", indexNotice, StringComparison.Ordinal);

        string diagramNotice = OfflineNotice.In(site.Read("diagram.html"));
        Assert.Contains($"<a href=\"{server.DiagramUrl}\">{server.DiagramUrl}</a>", diagramNotice, StringComparison.Ordinal);
        Assert.Contains("guardrails logs", diagramNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALogServer_BothOfflineNotices_NameGuardrailsLogs_AndLinkNoServer()
    {
        using var site = new Site();
        OnTheFlyDiagramObserver chain = site.Chain(server: null);

        chain.TaskStarting(site.AlphaTask);

        foreach (string page in new[] { "index.html", "diagram.html" })
        {
            string notice = OfflineNotice.In(site.Read(page));
            Assert.Contains("guardrails logs", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("http://", notice, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheLiveTableBranch_WritesItsFirstPagesWithTheLiveLinksToo()
    {
        // The live-table branch writes its first index and diagram BEFORE the chain exists, because the Spectre
        // region the chain wraps must not start until both links are printed (#145). Those two writes have to carry
        // the server's URLs themselves, or a page opened before the first task starts names the wrong remedy.
        using var site = new Site();
        await using LogServer server = site.StartServer();

        OnTheFlyLogSiteObserver.WriteInitialIndex(
            site.LogsRoot, Site.RunId, site.Plan.Tasks, server.UrlForTask, liveRunUrl: server.BaseUrl);
        OnTheFlyDiagramObserver.WriteInitialDiagram(
            site.LogsRoot, site.Plan, journalForSeed: null, liveDiagramUrl: server.DiagramUrl);

        Assert.Contains($"<a href=\"{server.BaseUrl}\">", OfflineNotice.In(site.Read("index.html")), StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{server.DiagramUrl}\">", OfflineNotice.In(site.Read("diagram.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSettledDiagram_DoesNotAdvertiseAServerThatIsAboutToStop()
    {
        // The final page is written as the run ends and its server stops, and it carries no poll that could ever
        // reveal the notice. A live URL in it could only ever be a dead link, so the settled page keeps the
        // server-less wording.
        using var site = new Site();
        await using LogServer server = site.StartServer();
        OnTheFlyDiagramObserver chain = site.Chain(server);

        chain.TaskStarting(site.AlphaTask);
        chain.WriteFinalStatic();

        Assert.DoesNotContain(server.DiagramUrl, site.Read("diagram.html"), StringComparison.Ordinal);
    }

    /// <summary>A throwaway one-task plan folder with a <c>logs/&lt;runId&gt;/</c> tree.</summary>
    private sealed class Site : IDisposable
    {
        public const string RunId = "test-run";

        public Site()
        {
            Directory.CreateDirectory(LogsRoot);
            AlphaTask = new TaskNode
            {
                Id = "01-alpha",
                Directory = Path.Combine(Dir, "tasks", "01-alpha"),
                Description = "task 01-alpha",
                Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
                Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
            };
            Plan = new PlanDefinition
            {
                PlanDirectory = Dir,
                Workspace = Dir,
                Config = new RunConfig { Version = 1 },
                Tasks = [AlphaTask]
            };
        }

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-714-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TaskNode AlphaTask { get; }

        public PlanDefinition Plan { get; }

        public LogServer StartServer()
        {
            LogServer? server = LogServer.TryStart(Dir, RunId, Plan.Tasks, port: 0, TextWriter.Null);
            Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
            return server!;
        }

        /// <summary>The chain exactly as <c>guardrails run</c> composes it, with or without its log server.</summary>
        public OnTheFlyDiagramObserver Chain(LogServer? server) => RunCommand.BuildObserverChain(
            IRunObserver.Null, LogsRoot, RunId, Plan, logServer: server, diagramSeed: null,
            onRow: null, includeDetail: false);

        public string Read(string page) => File.ReadAllText(Path.Combine(LogsRoot, page));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
