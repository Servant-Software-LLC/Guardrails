using System.Net;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the loopback <see cref="LogServer"/> end-to-end over a temp plan folder with
/// hand-written attempt logs. The canonical "all tasks" page is the static index FILE (issue #143),
/// so the live <c>/</c> route is a pointer note at that file's path (NOT a task table) and the
/// per-task page is an active-task deadend (no "all tasks" link). The per-task <c>/files</c> and
/// <c>/file</c> endpoints surface the latest attempt's log files, unknown tasks 404, and the file
/// endpoint refuses to escape the attempt directory. The server binds to localhost only.
/// </summary>
public sealed class LogServerTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Root_IsPointerNote_ToStaticIndexFile_NotATaskTable()
    {
        // Issue #143: the http server no longer serves its own all-tasks landing. GET / is a small note
        // pointing at the canonical static index FILE by path (a browser blocks http→file, so it is
        // shown as text), and it does NOT render the dynamic task table / per-task /tasks/{id} links.
        using var temp = new TempPlan();
        IReadOnlyList<TaskNode> tasks = [Task("01-alpha", "First task"), Task("02-beta", "Second task")];
        await using LogServer server = Start(temp.Dir, tasks);

        string html = await GetStringAsync(server.BaseUrl);

        // It names the static index file's path under this run's logs/<runId>/ tree.
        string indexPath = Path.GetFullPath(Path.Combine(temp.Dir, "logs", TempPlan.RunId, "index.html"));
        Assert.Contains(indexPath, html);
        Assert.Contains("static index", html);

        // The retired landing's task table / per-task links are gone.
        Assert.DoesNotContain("/tasks/01-alpha", html);
        Assert.DoesNotContain("<th>Description</th>", html);
        Assert.DoesNotContain("<th>Status</th>", html);
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
    public async Task TaskPage_IsActiveDeadend_HasNoAllTasksLink()
    {
        // Issue #143: the live per-task tailing page is an active-task DEADEND — reached by clicking a
        // running task on the static index, left via the browser Back button. It carries no "all tasks"
        // navigation back to the (now retired) http landing.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "x");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string html = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha");

        Assert.DoesNotContain("all tasks", html);     // no "← all tasks" link text
        Assert.DoesNotContain("href=\"/\"", html);    // and no link back to the root landing
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
                LogServer? server = LogServer.TryStart(temp.Dir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
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
        // The product honours a non-zero caller-chosen port with a SINGLE bind attempt (no retry —
        // the caller picked it; see LogServer.TryStart maxAttempts). We pick a free port via a
        // throwaway probe, then hand THAT back as the chosen port. There is a tiny TOCTOU window
        // between releasing the probe and the server binding, in which a shared CI runner can steal
        // the port — making the single-attempt bind return null (issue #277 flake: Assert.NotNull
        // failed on ubuntu-latest). Re-probe a FRESH free port and re-bind to close that window.
        //
        // Crucially, the "honoured EXACTLY" assertion stays OUTSIDE the retry loop: the loop only
        // re-tries on a null return (a lost bind race), never on a bound-but-wrong-port server. So a
        // regression where the server ignores the chosen port and OS-assigns a different one still
        // fails here (Assert.Equal), and a regression where the caller-chosen bind never succeeds
        // still fails (Assert.NotNull after the budget is exhausted) — the de-flake weakens neither.
        using var temp = new TempPlan();

        LogServer? server = null;
        int port = 0;
        for (int attempt = 0; attempt < BindRetryBudget && server is null; attempt++)
        {
            port = FreeLoopbackPort();
            server = LogServer.TryStart(temp.Dir, TempPlan.RunId, [Task("01-alpha", "First")], port, TextWriter.Null);
        }

        Assert.NotNull(server); // a free caller-chosen port bound within the budget (not a product bug)

        await using (server)
        {
            Assert.Equal($"http://127.0.0.1:{port}/", server!.BaseUrl);
        }
    }

    /// <summary>
    /// How many times <see cref="CallerChosenPort_BindsToExactlyThatPort"/> re-probes a fresh free
    /// port and re-binds when the single-attempt caller-chosen bind loses the probe→bind race. Each
    /// re-probe draws a different ephemeral port from the OS, so a port stolen in one iteration is
    /// dodged by the next; the compound odds of losing the race this many times running are
    /// vanishingly small, while a genuine "caller-chosen bind never works" regression still exhausts
    /// the budget and fails the test.
    /// </summary>
    private const int BindRetryBudget = 10;

    // --- #141 item 4: empty-file marking in /files -------------------------------------------

    [Fact]
    public async Task Files_FileDetails_CarriesSize_AndFlagsEmptyFile()
    {
        // #141 item 4: a zero-byte capture (empty stdout/stderr) must be distinguishable. /files now
        // emits a fileDetails[] of { name, size, empty } per file alongside the bare names.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "has content");
        temp.WriteLog("01-alpha", attempt: 1, "action-stderr.log", string.Empty); // empty capture
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files");
        using JsonDocument doc = JsonDocument.Parse(json);

        var details = doc.RootElement.GetProperty("fileDetails").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e);

        Assert.True(details["action-stdout.log"].GetProperty("size").GetInt64() > 0);
        Assert.False(details["action-stdout.log"].GetProperty("empty").GetBoolean());
        Assert.Equal(0, details["action-stderr.log"].GetProperty("size").GetInt64());
        Assert.True(details["action-stderr.log"].GetProperty("empty").GetBoolean());
    }

    [Fact]
    public async Task TaskPage_Js_HasEmptyOptionHandling_AndSourceFetch()
    {
        // JS is not executed in tests, so assert the page carries the hooks: the empty-option marking
        // (the .empty class + " (empty)" suffix, #141 item 4) and the Source fetch (#141 item 3).
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "x");
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        string html = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha");

        Assert.Contains("(empty)", html);                 // the empty suffix the option handler appends
        Assert.Contains("fileDetails", html);             // the page reads the per-file empty flags
        Assert.Contains("/source", html);                 // it fetches the Source list
        Assert.Contains("/sourcefile?name=", html);       // and a click views one source file's text
        Assert.Contains("<h2>Source</h2>", html);         // the Source section header
    }

    [Fact]
    public async Task TaskPage_Js_SourceViewPausesTailing_WithResumeControl_Issue147()
    {
        // #147: clicking a Source link must not get overwritten by the 1s tail tick. JS isn't executed
        // in tests, so assert the page carries the hooks: a viewingSource pause flag, refreshLog gated on
        // it (so it can't re-derive `current` and clobber the source view), and a "back to live log"
        // resume control + handler.
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "x");
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        string html = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha");

        Assert.Contains("let viewingSource", html);           // the pause flag
        Assert.Contains("if (viewingSource) return;", html);  // refreshLog gated on it (no clobber)
        Assert.Contains("id=\"resume\"", html);               // the "back to live log" control
        Assert.Contains("backToLiveLog", html);               // and the resume handler
    }

    // --- #141 item 3: source routes ----------------------------------------------------------

    [Fact]
    public async Task Source_ListsActionAndGuardrailFiles()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/source");
        using JsonDocument doc = JsonDocument.Parse(json);

        string[] names = doc.RootElement.GetProperty("sources").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();

        Assert.Contains("action.ps1", names);
        Assert.Contains("01-check.ps1", names);
        Assert.Equal("action.ps1", names[0]); // action leads the list
    }

    [Fact]
    public async Task Source_IncludesGuardrailJsonSidecar_WhenPresent()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha", withSidecar: true)]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/source");
        using JsonDocument doc = JsonDocument.Parse(json);

        string[] names = doc.RootElement.GetProperty("sources").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();

        Assert.Contains("01-check.json", names);
    }

    [Fact]
    public async Task Source_FlagsEmptySourceFile()
    {
        // #141 item 4 applied to the Source list: a zero-byte guardrail script is flagged empty.
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha", emptyGuardrail: true)]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/source");
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement guardrail = doc.RootElement.GetProperty("sources").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "01-check.ps1");
        Assert.True(guardrail.GetProperty("empty").GetBoolean());
    }

    [Fact]
    public async Task SourceFile_ServesAGuardrailScriptText()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/sourcefile?name=01-check.ps1");

        Assert.Equal("exit 0\n", body); // the guardrail script's raw text
    }

    [Fact]
    public async Task SourceFile_ServesTheActionText()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/sourcefile?name=action.ps1");

        Assert.Equal("Write-Output 'the action body'\n", body);
    }

    [Fact]
    public async Task SourceFile_UnknownName_IsRejected()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/sourcefile?name=not-a-source.ps1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SourceFile_TraversalName_IsRejected()
    {
        // A traversal name resolves against the KNOWN source set only — it has no entry, so it is
        // rejected. The path is never built from the request, so traversal is impossible by construction.
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [TaskWithRealSources(temp, "01-alpha")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/sourcefile?name=..%2F..%2Fstate.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        // The server serves logs/<runId>/ (SSOT §8); the fixtures below write under the same runId.
        LogServer? server = LogServer.TryStart(planDir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
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

    /// <summary>
    /// A task whose action + guardrail paths point at REAL files written under <paramref name="temp"/>
    /// (so the source routes can serve their content). Mirrors the loader: absolute paths, a guardrail
    /// <c>.json</c> sidecar when <paramref name="withSidecar"/>. The action body is non-empty; the
    /// guardrail script is written empty when <paramref name="emptyGuardrail"/> so the empty-marking is
    /// exercised against a zero-byte source.
    /// </summary>
    private static TaskNode TaskWithRealSources(
        TempPlan temp, string id, bool withSidecar = false, bool emptyGuardrail = false)
    {
        string taskDir = Path.Combine(temp.Dir, "tasks", id);
        string guardrailsDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(guardrailsDir);

        string actionPath = Path.Combine(taskDir, "action.ps1");
        File.WriteAllText(actionPath, "Write-Output 'the action body'\n");

        string guardrailPath = Path.Combine(guardrailsDir, "01-check.ps1");
        File.WriteAllText(guardrailPath, emptyGuardrail ? string.Empty : "exit 0\n");

        var guardrails = new List<GuardrailDefinition>
        {
            new() { Name = "01-check", Path = guardrailPath, Kind = ActionKind.Script }
        };

        if (withSidecar)
        {
            // The loader includes a guardrail's <name>.json metadata sidecar in the source set.
            File.WriteAllText(Path.Combine(guardrailsDir, "01-check.json"), "{ \"timeoutSeconds\": 30 }\n");
        }

        return new TaskNode
        {
            Id = id,
            Directory = taskDir,
            Description = "task " + id,
            Action = new ActionDefinition { Path = actionPath, Kind = ActionKind.Script },
            Guardrails = guardrails
        };
    }

    /// <summary>A throwaway plan directory under the temp path; cleaned up on dispose.</summary>
    private sealed class TempPlan : IDisposable
    {
        /// <summary>A fixed run id so the fixtures and the server agree on which logs/&lt;runId&gt;/ tree to use.</summary>
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-logsrv-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        public void WriteLog(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(Dir, "logs", RunId, taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        /// <summary>
        /// Appends to an existing attempt log, mirroring how the producing process grows it (more
        /// bytes, fresh mtime). Used to prove the read cache never serves stale content.
        /// </summary>
        public void AppendLog(string taskId, int attempt, string fileName, string extra)
        {
            string path = Path.Combine(Dir, "logs", RunId, taskId, $"attempt-{attempt}", fileName);
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
