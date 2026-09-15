using System.Text.RegularExpressions;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Issue #713: <b>the live run view's Status column must say what happened to each task.</b>
/// <para>
/// The root page of the run's own log server (<c>http://127.0.0.1:&lt;port&gt;/</c>, #573) filled its State
/// column from the highest <c>attempt-N</c> directory on disk. An attempt directory is created when the attempt
/// STARTS, so every task the harness had ever scheduled read <c>attempt N</c> for the rest of the run. Plan 39's
/// autonomous run showed eighteen tasks as <c>attempt 1</c>: seventeen had succeeded and one was still running. A
/// task whose last attempt failed its guardrails would have read the same way, as progress. The during-run static
/// index showed the right states for the same run at the same moment.
/// </para>
/// <para>
/// So the page now reads the status map that drives that index (<see cref="OnTheFlyLogSiteObserver"/>), handed
/// to the server by <c>RunCommand.BuildObserverChain</c>, the composition both branches of <c>guardrails run</c>
/// call. Every test here goes through that real seam and a real server. Every one also compares the page's cell
/// with the during-run index's cell for the same task, because the property is "cannot disagree with the index",
/// not merely "prints some status word".
/// </para>
/// </summary>
public sealed class LiveRunViewStatusTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task ASucceededTask_DoesNotReadLikeARunningOne()
    {
        // Both tasks have exactly one attempt directory, which is all the old column looked at. It printed
        // "attempt 1" for each, and the operator watching plan 39 could not tell the finished one from the one
        // still going.
        using var site = new Site("01-done", "02-going", "03-waiting");
        await using LogServer server = site.StartServer();
        OnTheFlyDiagramObserver chain = site.Chain(server);

        site.WriteAttempt("01-done", 1);
        chain.TaskStarting(site.Task("01-done"));
        chain.TaskFinished(Result("01-done", TaskOutcome.Succeeded));
        site.WriteAttempt("02-going", 1);
        chain.TaskStarting(site.Task("02-going"));

        string html = await GetRootAsync(server);
        LiveRunViewRows.Row done = LiveRunViewRows.Find(html, "01-done");
        LiveRunViewRows.Row going = LiveRunViewRows.Find(html, "02-going");
        LiveRunViewRows.Row waiting = LiveRunViewRows.Find(html, "03-waiting");

        Assert.Equal("succeeded", done.Status);
        Assert.Equal("running", going.Status);
        Assert.Equal("pending", waiting.Status);

        // The attempt number is still on the page, as the detail beside the status rather than in its place.
        Assert.Equal("attempt 1", done.Attempt);
        Assert.Equal("attempt 1", going.Attempt);
        Assert.Equal("—", waiting.Attempt);

        // One map, so one answer: the live page and the during-run index say the same thing.
        Assert.Equal(site.IndexStatus("01-done"), done.Status);
        Assert.Equal(site.IndexStatus("02-going"), going.Status);
        Assert.Equal(site.IndexStatus("03-waiting"), waiting.Status);
    }

    [Fact]
    public async Task ARunningTask_DoesNotReadLikeAFinishedOne_EvenAfterAFailedAttempt()
    {
        // A retry is where a source keyed on the latest ATTEMPT goes wrong. 02's first attempt failed its
        // guardrails and its second is in flight, so "what the last attempt did" says failed while the task is
        // still running. Both tasks are on attempt 2, which the old column printed identically.
        using var site = new Site("01-done", "02-retrying");
        await using LogServer server = site.StartServer();
        OnTheFlyDiagramObserver chain = site.Chain(server);

        site.WriteAttempt("01-done", 1);
        site.WriteAttempt("01-done", 2);
        chain.TaskStarting(site.Task("01-done"));
        chain.TaskFinished(Result("01-done", TaskOutcome.Succeeded));

        TaskNode retrying = site.Task("02-retrying");
        chain.TaskStarting(retrying);
        site.WriteAttempt("02-retrying", 1);
        chain.AttemptStarting(retrying, 1, 2);
        chain.AttemptFinished(retrying, Attempt(1, AttemptOutcome.GuardrailFailed));
        site.WriteAttempt("02-retrying", 2);
        chain.AttemptStarting(retrying, 2, 2);

        string html = await GetRootAsync(server);
        LiveRunViewRows.Row done = LiveRunViewRows.Find(html, "01-done");
        LiveRunViewRows.Row running = LiveRunViewRows.Find(html, "02-retrying");

        Assert.Equal("running", running.Status);
        Assert.Equal("succeeded", done.Status);
        Assert.Equal("attempt 2", running.Attempt);
        Assert.Equal("attempt 2", done.Attempt);

        Assert.Equal(site.IndexStatus("02-retrying"), running.Status);
        Assert.Equal(site.IndexStatus("01-done"), done.Status);
    }

    [Fact]
    public async Task AFailedFinalAttempt_DoesNotReadLikeEitherASucceededOrARunningTask()
    {
        // The silent-failure shape. Under the old column, a task whose last attempt failed its guardrails read
        // "attempt 1", the same cell as a success and as a task still working, so a stopped task looked like
        // progress. The during-run index calls every failed terminal outcome needs-human, in red, and so must
        // this page.
        using var site = new Site("01-done", "02-going", "03-failed");
        await using LogServer server = site.StartServer();
        OnTheFlyDiagramObserver chain = site.Chain(server);

        site.WriteAttempt("01-done", 1);
        chain.TaskStarting(site.Task("01-done"));
        chain.TaskFinished(Result("01-done", TaskOutcome.Succeeded));
        site.WriteAttempt("03-failed", 1);
        chain.TaskStarting(site.Task("03-failed"));
        chain.TaskFinished(Result("03-failed", TaskOutcome.GuardrailFailed));
        site.WriteAttempt("02-going", 1);
        chain.TaskStarting(site.Task("02-going"));

        string html = await GetRootAsync(server);
        LiveRunViewRows.Row done = LiveRunViewRows.Find(html, "01-done");
        LiveRunViewRows.Row going = LiveRunViewRows.Find(html, "02-going");
        LiveRunViewRows.Row failed = LiveRunViewRows.Find(html, "03-failed");

        Assert.Equal("needs-human", failed.Status);
        Assert.NotEqual(done.Status, failed.Status);
        Assert.NotEqual(going.Status, failed.Status);

        Assert.Equal(site.IndexStatus("03-failed"), failed.Status);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static async Task<string> GetRootAsync(LogServer server) =>
        await Http.GetStringAsync(server.BaseUrl, TestContext.Current.CancellationToken);

    private static TaskResult Result(string id, TaskOutcome outcome) =>
        new() { TaskId = id, Outcome = outcome, Summary = $"{id} {outcome}" };

    private static AttemptRecord Attempt(int attempt, AttemptOutcome outcome) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = outcome,
        LogDir = "logs/fixture"
    };

    /// <summary>
    /// A throwaway plan folder with a <c>logs/&lt;runId&gt;/</c> tree, a real log server over it, and the real
    /// observer chain composed around that server.
    /// </summary>
    private sealed class Site : IDisposable
    {
        private const string RunId = "test-run";

        private readonly Dictionary<string, TaskNode> _tasks = new(StringComparer.Ordinal);

        public Site(params string[] ids)
        {
            Directory.CreateDirectory(LogsRoot);
            foreach (string id in ids)
            {
                _tasks[id] = new TaskNode
                {
                    Id = id,
                    Directory = Path.Combine(Dir, "tasks", id),
                    Description = "task " + id,
                    Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
                    Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
                };
            }

            Plan = new PlanDefinition
            {
                PlanDirectory = Dir,
                Workspace = Dir,
                Config = new RunConfig { Version = 1 },
                Tasks = [.. ids.Select(id => _tasks[id])]
            };
        }

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-713-" + Guid.NewGuid().ToString("N"));

        private string LogsRoot => Path.Combine(Dir, "logs", RunId);

        private PlanDefinition Plan { get; }

        public TaskNode Task(string id) => _tasks[id];

        public LogServer StartServer()
        {
            LogServer? server = LogServer.TryStart(Dir, RunId, Plan.Tasks, port: 0, TextWriter.Null);
            Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
            return server!;
        }

        /// <summary>The chain exactly as <c>guardrails run</c> composes it when its log server is up.</summary>
        public OnTheFlyDiagramObserver Chain(LogServer server) => RunCommand.BuildObserverChain(
            IRunObserver.Null, LogsRoot, RunId, Plan, logServer: server, diagramSeed: null,
            onRow: null, includeDetail: false);

        public void WriteAttempt(string taskId, int attempt)
        {
            string dir = Path.Combine(LogsRoot, taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "action-stdout.log"), "output");
        }

        /// <summary>The status word the during-run index (<c>logs/&lt;runId&gt;/index.html</c>) shows for a task.</summary>
        public string IndexStatus(string taskId)
        {
            string html = File.ReadAllText(Path.Combine(LogsRoot, "index.html"));
            Match row = Regex.Match(
                html,
                "<tr><td>(?:<a href=\"[^\"]*\">)?" + Regex.Escape(taskId) + "(?:</a>)?</td><td class=\"status\" data-status=\"(?<word>[^\"]*)\"");
            Assert.True(row.Success, $"the during-run index has no row for {taskId}:\n{html}");
            return row.Groups["word"].Value;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
