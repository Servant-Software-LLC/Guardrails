using System.Net;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the loopback <see cref="LogServer"/> end-to-end over a temp plan folder with
/// hand-written attempt logs: the landing page lists tasks, the per-task <c>/files</c> and
/// <c>/file</c> endpoints surface the latest attempt's log files, unknown tasks 404, and the
/// file endpoint refuses to escape the attempt directory. The server binds to localhost only.
/// </summary>
public sealed class LogServerTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Landing_ListsEveryTask_WithLinks()
    {
        using var temp = new TempPlan();
        IReadOnlyList<TaskNode> tasks = [Task("01-alpha", "First task"), Task("02-beta", "Second task")];
        await using LogServer server = Start(temp.Dir, tasks);

        string html = await GetStringAsync(server.BaseUrl);

        Assert.Contains("01-alpha", html);
        Assert.Contains("02-beta", html);
        Assert.Contains("/tasks/01-alpha", html);
        Assert.Contains("Second task", html);
    }

    [Fact]
    public async Task UnknownTask_Returns404()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/99-does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Files_ReportsLatestAttempt_AndPrefersClaudeStreamOverActionStdout()
    {
        // No transcript.md present (a script task, or a prompt attempt before the transcript was
        // rendered): claude-stream.jsonl still beats action-stdout.log.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "old attempt");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "hello");
        temp.WriteLog("01-alpha", attempt: 2, "claude-stream.jsonl", "{}");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("attempt").GetInt32());
        Assert.Equal("claude-stream.jsonl", root.GetProperty("preferred").GetString());
        string[] files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("action-stdout.log", files);
        Assert.Contains("claude-stream.jsonl", files);
    }

    [Fact]
    public async Task Files_PrefersTranscriptMd_OverClaudeStreamAndActionStdout()
    {
        // Issue #118: when transcript.md (the groomed, human-readable agent view) is present, it is
        // the default file the task page opens — ahead of the raw claude-stream.jsonl and the
        // action-stdout.log. transcript.md sorts AFTER both of those ordinally, so this proves the
        // preference is intentional and not an artefact of alphabetical first-file fallback.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "stdout");
        temp.WriteLog("01-alpha", attempt: 1, "claude-stream.jsonl", "{}");
        temp.WriteLog("01-alpha", attempt: 1, "transcript.md", "# groomed view");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("transcript.md", root.GetProperty("preferred").GetString());
    }

    [Fact]
    public async Task Files_ListsEveryAttempt_Ascending()
    {
        // Issue #103: the attempt selector needs the full list of attempts, not just the latest.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "second");
        temp.WriteLog("01-alpha", attempt: 3, "action-stdout.log", "third");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("attempt").GetInt32()); // selected defaults to latest
        int[] attempts = root.GetProperty("attempts").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, attempts);
    }

    [Fact]
    public async Task Files_WithAttemptParam_SelectsThatAttempt_NotLatest()
    {
        // Issue #103: ?attempt=N serves the requested prior attempt's files while a later attempt
        // exists — the live viewer inspecting attempt-1 while attempt-2 runs.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first");
        temp.WriteLog("01-alpha", attempt: 1, "first-only.log", "x");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "second");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files?attempt=1");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("attempt").GetInt32());
        string[] files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("first-only.log", files); // an attempt-1-only file proves the right dir was read
        // The full attempt list is unaffected by the selection.
        int[] attempts = root.GetProperty("attempts").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2 }, attempts);
    }

    [Fact]
    public async Task Files_UnknownAttemptParam_FallsBackToLatest()
    {
        // A URL naming an attempt that does not exist yet (mid-run) falls back to latest, not 404.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files?attempt=9");
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task File_WithAttemptParam_ReturnsThatAttemptsContent()
    {
        // Issue #103: /file?...&attempt=N reads the requested attempt, not the latest.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first attempt output");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "second attempt output");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string body = await GetStringAsync(
            $"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log&attempt=1");

        Assert.Equal("first attempt output", body); // explicitly the OLDER attempt
    }

    [Fact]
    public async Task TaskPage_HasAttemptSelector()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "x");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string html = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha");

        // An attempt <select> beside the existing file <select> (mirror the file picker).
        Assert.Contains("id=\"attempt\"", html);
        Assert.Contains("id=\"file\"", html);
    }

    [Fact]
    public async Task File_ReturnsRawContent_OfLatestAttempt()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first attempt output");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "second attempt output");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log");

        Assert.Equal("second attempt output", body); // latest attempt wins
    }

    [Fact]
    public async Task File_ReReadsAfterAppend_NeverServesStaleContent()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "line one\n");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);
        string url = $"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log";

        // First serve populates the read cache.
        Assert.Equal("line one\n", await GetStringAsync(url));

        // The log grows (more bytes + fresh mtime) — the cache key changes, so the next serve must
        // re-read and include the appended tail rather than returning the cached first line.
        temp.AppendLog("01-alpha", attempt: 1, "action-stdout.log", "line two\n");

        Assert.Equal("line one\nline two\n", await GetStringAsync(url));
    }

    [Fact]
    public async Task File_RepeatedRequestsOfUnchangedFile_AreConsistent()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "stable content");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);
        string url = $"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log";

        // Multiple serves of an idle file (the cache-hit path) must return identical content.
        Assert.Equal("stable content", await GetStringAsync(url));
        Assert.Equal("stable content", await GetStringAsync(url));
        Assert.Equal("stable content", await GetStringAsync(url));
    }

    [Fact]
    public async Task File_NoAttemptYet_ReturnsEmptyButOk()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_PathTraversal_IsRejected()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "safe");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/file?name=..%2F..%2Fstate.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Landing_WithStatusProvider_RendersColouredStatusColumn()
    {
        using var temp = new TempPlan();
        IReadOnlyList<TaskNode> tasks = [Task("01-alpha", "First"), Task("02-beta", "Second")];
        LogServer? server = LogServer.TryStart(temp.Dir, tasks, port: 0, TextWriter.Null,
            statusForTask: id => id == "01-alpha" ? "succeeded" : "needs-human");
        Assert.NotNull(server);

        await using (server)
        {
            string html = await GetStringAsync(server!.BaseUrl);
            Assert.Contains("<th>Status</th>", html);
            Assert.Contains("data-status=\"succeeded\"", html);
            Assert.Contains("data-status=\"needs-human\"", html);
        }
    }

    [Fact]
    public async Task Landing_WithoutStatusProvider_HasNoStatusColumn()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string html = await GetStringAsync(server.BaseUrl);

        Assert.DoesNotContain("<th>Status</th>", html);
    }

    [Fact]
    public async Task UrlForTask_KnownReturnsUrl_UnknownReturnsNull()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        Assert.NotNull(server.UrlForTask("01-alpha"));
        Assert.StartsWith(server.BaseUrl, server.UrlForTask("01-alpha")!);
        Assert.Null(server.UrlForTask("nope"));
    }

    [Fact]
    public async Task EphemeralPort_ManyConcurrentServers_AllBindToDistinctPorts()
    {
        // Stand up several port-0 servers at once: each probes a free ephemeral port then binds an
        // HttpListener to it. This is the path the TOCTOU retry hardens — under contention a probe
        // can hand out a port that is then taken before the bind. The retry loop must let every
        // server bind, on a distinct port. (Deterministic: asserts all-bound, not a timing race.)
        using var temp = new TempPlan();
        IReadOnlyList<TaskNode> tasks = [Task("01-alpha", "First")];

        var servers = new List<LogServer>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                LogServer? server = LogServer.TryStart(temp.Dir, tasks, port: 0, TextWriter.Null);
                Assert.NotNull(server); // the retry loop survives the probe→bind race
                servers.Add(server!);
            }

            // Every server got its own port — none collided.
            string[] urls = servers.Select(s => s.BaseUrl).ToArray();
            Assert.Equal(urls.Length, urls.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            foreach (LogServer server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task CallerChosenPort_BindsToExactlyThatPort()
    {
        // A non-zero port is honoured verbatim with a single bind attempt (no retry — the caller
        // picked it). Pick a free port via a throwaway probe, then assert the server lands on it.
        int port = FreeLoopbackPort();
        using var temp = new TempPlan();
        LogServer? server = LogServer.TryStart(temp.Dir, [Task("01-alpha", "First")], port, TextWriter.Null);
        Assert.NotNull(server);

        await using (server)
        {
            Assert.Equal($"http://127.0.0.1:{port}/", server!.BaseUrl);
        }
    }

    // --- helpers ----------------------------------------------------------------------------

    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static LogServer Start(string planDir, IReadOnlyList<TaskNode> tasks)
    {
        LogServer? server = LogServer.TryStart(planDir, tasks, port: 0, TextWriter.Null);
        Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
        return server!;
    }

    private static async Task<string> GetStringAsync(string url) =>
        await Http.GetStringAsync(url, TestContext.Current.CancellationToken);

    private static TaskNode Task(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
    };

    /// <summary>A throwaway plan directory under the temp path; cleaned up on dispose.</summary>
    private sealed class TempPlan : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-logsrv-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        public void WriteLog(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(Dir, "state", "logs", taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        /// <summary>
        /// Appends to an existing attempt log, mirroring how the producing process grows it (more
        /// bytes, fresh mtime). Used to prove the read cache never serves stale content.
        /// </summary>
        public void AppendLog(string taskId, int attempt, string fileName, string extra)
        {
            string path = Path.Combine(Dir, "state", "logs", taskId, $"attempt-{attempt}", fileName);
            File.AppendAllText(path, extra);
            // Guarantee a distinct LastWriteTimeUtc from the prior serve even on coarse-grained
            // filesystem timestamp resolution, so the change is observable to the (Length, mtime)
            // cache key regardless of how fast the test runs.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        }

        public void Dispose()
        {
            // UnauthorizedAccessException is NOT a subtype of IOException on .NET — catch both
            // so a locked file on Windows doesn't mask the original test failure.
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
