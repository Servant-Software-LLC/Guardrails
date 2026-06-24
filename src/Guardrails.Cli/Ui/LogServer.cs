using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// A loopback-only HTTP server that surfaces each task's live attempt log while a run is in
/// flight, so the user can answer "is it actually working?" without leaving the terminal.
/// Bound to <c>127.0.0.1</c> on an ephemeral port (logs may echo secrets — it is NEVER exposed
/// off the local machine). The lifetime is the run: <see cref="TryStart"/> at the top,
/// <see cref="DisposeAsync"/> in a finally.
///
/// Routes:
/// <list type="bullet">
///   <item><c>GET /</c> — landing page listing every task, each linking to its log page.</item>
///   <item><c>GET /tasks/{id}</c> — a page that tails an attempt's log directory (latest by default).</item>
///   <item><c>GET /tasks/{id}/files[?attempt=N]</c> — JSON: the selected attempt number, every
///     available attempt number, and the files in the selected attempt (default = latest), with a
///     <c>fileDetails[]</c> carrying each file's size + <c>empty</c> flag (#141 item 4).</item>
///   <item><c>GET /tasks/{id}/file?name={f}[&amp;attempt=N]</c> — the raw text of one log file
///     from the selected attempt (default = latest; tailed by the page).</item>
///   <item><c>GET /tasks/{id}/source</c> — JSON listing the task's action file + guardrail scripts /
///     sidecars (each <c>{ name, label, empty }</c>) for the page's "Source" section (#141 item 3).</item>
///   <item><c>GET /tasks/{id}/sourcefile?name={f}</c> — the raw text of ONE of the task's known source
///     files, resolved only through the precomputed source set (an unknown / traversal name is rejected).</item>
/// </list>
///
/// The <c>{id}</c> must be a known task id. For <c>file</c>, <c>{name}</c> must be a bare filename
/// inside the attempt directory (validated to keep the surface inside <c>logs/&lt;runId&gt;/&lt;id&gt;/</c>);
/// for <c>sourcefile</c>, <c>{name}</c> must match one of the task's declared source files (the path is
/// the known absolute <see cref="LogSiteRenderer.SourceFile"/> path, never derived from the request).
/// </summary>
public sealed class LogServer : IAsyncDisposable
{
    // Preference order for the file the task page opens by default (issue #118). transcript.md —
    // the groomed, human-readable projection of the agent stream (#27) — is what the user almost
    // always wants when they click "view log", so it leads. claude-stream.jsonl (the raw debug
    // stream) and action-stdout.log follow as fallbacks for script tasks / pre-transcript attempts.
    private static readonly string[] PreferenceOrder =
        ["transcript.md", "claude-stream.jsonl", "action-stdout.log"];

    private readonly HttpListener _listener;
    private readonly string _logsRoot;
    private readonly IReadOnlyList<TaskNode> _tasks;
    private readonly HashSet<string> _taskIds;
    private readonly Func<string, string?>? _statusForTask;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    // Per-task source files (the action + every guardrail script + any .json sidecar), precomputed from
    // the plan's TaskNode definitions so the source routes (#141 item 3) resolve a requested name ONLY
    // against this known set — path-safe by construction (an unknown name never resolves to a path).
    // Keyed by task id; the inner map is filename → SourceFile (absolute path + label).
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, LogSiteRenderer.SourceFile>> _sourcesByTask;

    // Per-file read cache, keyed by absolute path. The accept loop serves requests concurrently,
    // so all access is under _fileCacheLock. A cached entry is reused ONLY when the file's current
    // (Length, LastWriteTimeUtc) exactly match the values captured at the last serve; since these
    // logs are append-only and the writer touches mtime on every write, a changed entry is always
    // re-read — the cache only skips redundant reads of an idle file, never serves stale bytes.
    private readonly object _fileCacheLock = new();
    private readonly Dictionary<string, CachedFile> _fileCache = new(StringComparer.Ordinal);

    private readonly record struct CachedFile(long Length, DateTime LastWriteTimeUtc, string Content);

    private LogServer(
        HttpListener listener,
        string baseUrl,
        string logsRoot,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? statusForTask)
    {
        _listener = listener;
        _baseUrl = baseUrl;
        _logsRoot = logsRoot;
        _tasks = tasks;
        _statusForTask = statusForTask;
        _taskIds = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.Ordinal);
        _sourcesByTask = BuildSourceMap(tasks);
    }

    /// <summary>
    /// Precompute each task's known source files (the action + every guardrail script + any <c>.json</c>
    /// sidecar) from the plan's <see cref="TaskNode"/> definitions, keyed task id → (filename →
    /// <see cref="LogSiteRenderer.SourceFile"/>). The source routes (#141 item 3) resolve a requested
    /// <c>name</c> ONLY through this map, so an unknown / traversal name simply has no entry and is
    /// rejected — the file surface stays the known source set, never an arbitrary path. A duplicate
    /// filename (e.g. a guardrail named after the action) keeps the first; labels remain unique enough
    /// for the UI. Reuses the renderer's discovery so the live and static views list the SAME files.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, LogSiteRenderer.SourceFile>> BuildSourceMap(
        IReadOnlyList<TaskNode> tasks)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, LogSiteRenderer.SourceFile>>(StringComparer.Ordinal);
        foreach (TaskNode task in tasks)
        {
            var byName = new Dictionary<string, LogSiteRenderer.SourceFile>(StringComparer.Ordinal);
            foreach (LogSiteRenderer.SourceFile source in SourcesFor(task))
            {
                byName.TryAdd(source.Name, source);
            }

            map[task.Id] = byName;
        }

        return map;
    }

    /// <summary>The ordered source files surfaced for one task: action first, then its guardrail scripts/sidecars.</summary>
    private static IEnumerable<LogSiteRenderer.SourceFile> SourcesFor(TaskNode task)
    {
        yield return LogSiteRenderer.ActionSource(task);
        foreach (LogSiteRenderer.SourceFile guardrail in LogSiteRenderer.GuardrailSources(task))
        {
            yield return guardrail;
        }
    }

    /// <summary>The base URL the server is listening on, e.g. <c>http://localhost:54321/</c>.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>The log page URL for a task, or null if the id is unknown.</summary>
    public string? UrlForTask(string taskId) =>
        _taskIds.Contains(taskId) ? $"{_baseUrl}tasks/{Uri.EscapeDataString(taskId)}" : null;

    /// <summary>
    /// Start a loopback log server for <paramref name="planDirectory"/>'s tasks. Best-effort: if
    /// the listener cannot bind (locked-down host, port in use), prints one warning to
    /// <paramref name="warn"/> and returns null — the run proceeds without it, never blocked by a
    /// UX nicety. <paramref name="port"/> = 0 selects a free ephemeral port.
    /// </summary>
    /// <param name="planDirectory">Plan folder whose <c>logs/&lt;runId&gt;/</c> tree is served.</param>
    /// <param name="runId">The run whose attempt logs are served (selects <c>logs/&lt;runId&gt;/</c>).</param>
    /// <param name="tasks">The plan's tasks — the only ids the server will serve.</param>
    /// <param name="port">Listen port; 0 selects a free ephemeral port.</param>
    /// <param name="warn">Where a bind failure's single warning line is written.</param>
    /// <param name="statusForTask">
    /// Optional resolver mapping a task id to a status word (e.g. <c>succeeded</c>,
    /// <c>needs-human</c>). When supplied, the landing page renders a coloured Status column —
    /// used by <c>guardrails logs</c> (a standalone viewer has no terminal table to carry status).
    /// Null = no Status column (the live run path, where the terminal table shows status).
    /// </param>
    public static LogServer? TryStart(
        string planDirectory,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        int port,
        TextWriter warn,
        Func<string, string?>? statusForTask = null)
    {
        try
        {
            // HttpListener prefixes need a concrete port (it cannot itself take ephemeral port 0),
            // so for port 0 we probe a free port with a TcpListener, then bind the HttpListener to
            // it. That probe→bind gap is a TOCTOU window: another process can steal the port in
            // between. For a caller-chosen port we honour it with a single attempt; for an
            // ephemeral port we retry with a fresh probe if the bind loses the race.
            int maxAttempts = port > 0 ? 1 : 10;
            HttpListenerException? lastBindFailure = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int boundPort = port > 0 ? port : FreeLoopbackPort();
                // Bind to the numeric loopback address rather than the name "localhost" so the
                // "never exposed off this machine" guarantee is unconditional and not affected by
                // custom /etc/hosts or DNS overrides that map "localhost" to a routable address.
                string bindUrl = $"http://127.0.0.1:{boundPort}/";
                // BaseUrl uses the numeric address too — keeps it honest and matches the binding.
                string baseUrl = $"http://127.0.0.1:{boundPort}/";

                var listener = new HttpListener();
                listener.Prefixes.Add(bindUrl);
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    // The probed port was taken between probe and bind (or the caller's port is in
                    // use). Drop this listener; retry with a fresh probe for an ephemeral port,
                    // or fall through to the outer catch on the last attempt.
                    ((IDisposable)listener).Dispose();
                    lastBindFailure = ex;
                    continue;
                }

                // Per-attempt artifacts live under logs/<runId>/<task>/attempt-N/ (SSOT §8, plan-08:
                // a sibling of state/, divided by runId), NOT the pre-plan-08 state/logs/<task>/. The
                // run is selected by the journal's runId (the live run owns it; the post-mortem reads
                // it for the Status column), so the server walks exactly that run's tree.
                string logsRoot = Path.Combine(planDirectory, "logs", runId);
                var server = new LogServer(listener, baseUrl, logsRoot, tasks, statusForTask);
                server._acceptLoop = Task.Run(server.AcceptLoopAsync);
                return server;
            }

            // Exhausted the retry budget without binding — surface the last race failure to the
            // existing warn-and-return-null path below.
            throw lastBindFailure!;
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException or PlatformNotSupportedException)
        {
            warn.WriteLine($"Log server not started ({ex.Message}); run continues without live log links.");
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                return; // listener stopped during shutdown — expected
            }
            catch (HttpListenerException)
            {
                return; // listener disposed
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                TrySetStatus(context, HttpStatusCode.InternalServerError);
            }
            finally
            {
                try { context.Response.Close(); } catch (Exception) { /* client gone */ }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            WriteHtml(context, LandingHtml());
            return;
        }

        if (segments[0] != "tasks")
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        // /tasks/{id}[/files|/file]
        if (segments.Length < 2)
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        string taskId = Uri.UnescapeDataString(segments[1]);
        if (!_taskIds.Contains(taskId))
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        if (segments.Length == 2)
        {
            WriteHtml(context, TaskPageHtml(taskId));
            return;
        }

        switch (segments[2])
        {
            case "files":
                WriteJson(context, FilesJson(taskId, ParseAttempt(context.Request.QueryString["attempt"])));
                return;
            case "file":
                WriteFile(context, taskId,
                    context.Request.QueryString["name"],
                    ParseAttempt(context.Request.QueryString["attempt"]));
                return;
            case "source":
                WriteJson(context, SourceJson(taskId));
                return;
            case "sourcefile":
                WriteSourceFile(context, taskId, context.Request.QueryString["name"]);
                return;
            default:
                TrySetStatus(context, HttpStatusCode.NotFound);
                return;
        }
    }

    // --- payloads ---------------------------------------------------------------------------

    private string FilesJson(string taskId, int? requestedAttempt)
    {
        // Every attempt the task has on disk, ascending — the attempt <select> mirrors this and the
        // live viewer can inspect a finished attempt-1 while attempt-2 runs (issue #103).
        IReadOnlyList<int> attempts = AttemptNumbers(taskId);

        // Resolve the directory for the SELECTED attempt: an explicit ?attempt=N when it exists,
        // else the latest. An unknown/invalid N falls back to latest rather than 404 — the page
        // stays usable while a run is mid-flight and an attempt the URL named has not started yet.
        string? attemptDir = ResolveAttemptDir(taskId, requestedAttempt, out int? attemptNumber);

        var files = attemptDir is null
            ? new List<string>()
            : Directory.EnumerateFiles(attemptDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

        string? preferred =
            PreferenceOrder.FirstOrDefault(files.Contains) ?? files.FirstOrDefault();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (attemptNumber is { } n)
            {
                writer.WriteNumber("attempt", n);
            }
            else
            {
                writer.WriteNull("attempt");
            }

            writer.WriteStartArray("attempts");
            foreach (int a in attempts)
            {
                writer.WriteNumberValue(a);
            }

            writer.WriteEndArray();

            if (preferred is null)
            {
                writer.WriteNull("preferred");
            }
            else
            {
                writer.WriteString("preferred", preferred);
            }

            // The bare filename list stays for back-compat (the page reads d.files for the simple
            // case). fileDetails carries each file's size + empty bool so the page can grey a
            // zero-byte capture's <option> and append " (empty)" (#141 item 4).
            writer.WriteStartArray("files");
            foreach (string file in files)
            {
                writer.WriteStringValue(file);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("fileDetails");
            foreach (string file in files)
            {
                long size = FileSize(Path.Combine(attemptDir!, file));
                writer.WriteStartObject();
                writer.WriteString("name", file);
                writer.WriteNumber("size", size);
                writer.WriteBoolean("empty", size == 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// JSON for <c>GET /tasks/{id}/source</c> (#141 item 3): the action file + every guardrail script
    /// and <c>.json</c> sidecar this task declares, each <c>{ name, label, empty }</c>. The page renders
    /// this as the "Source" list; a click fetches the raw text via <c>/sourcefile?name=…</c>. <c>empty</c>
    /// marks a zero-byte source the same way the file dropdown marks an empty capture (#141 item 4).
    /// </summary>
    private string SourceJson(string taskId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("sources");
            // Discovery order (action first, then guardrails) — re-derived from the TaskNode rather than
            // the lookup map, whose iteration order is unspecified.
            foreach (LogSiteRenderer.SourceFile source in OrderedSources(taskId))
            {
                writer.WriteStartObject();
                writer.WriteString("name", source.Name);
                writer.WriteString("label", source.Label);
                writer.WriteBoolean("empty", FileSize(source.Path) == 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Re-derive a task's source files in display order (action, then guardrails) for the JSON.</summary>
    private IEnumerable<LogSiteRenderer.SourceFile> OrderedSources(string taskId)
    {
        TaskNode? task = _tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        return task is null ? Array.Empty<LogSiteRenderer.SourceFile>() : SourcesFor(task);
    }

    /// <summary>
    /// Serve <c>GET /tasks/{id}/sourcefile?name=…</c> (#141 item 3): the raw text of ONE of the task's
    /// known source files, resolved ONLY through the precomputed source set. An unknown name (or a
    /// traversal attempt) has no entry and is rejected — the path is never built from the request, so
    /// the surface is inherently confined to the action + guardrail files the plan declares.
    /// </summary>
    private void WriteSourceFile(HttpListenerContext context, string taskId, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            TrySetStatus(context, HttpStatusCode.BadRequest);
            return;
        }

        if (!_sourcesByTask.TryGetValue(taskId, out var sources) ||
            !sources.TryGetValue(name, out LogSiteRenderer.SourceFile source))
        {
            // Not one of THIS task's known sources — reject (covers unknown names and any traversal,
            // since the path is the known SourceFile.Path, never derived from the request).
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        if (!File.Exists(source.Path))
        {
            // Declared but absent on disk (e.g. a mid-edit plan) — empty body, not a crash.
            WriteText(context, string.Empty);
            return;
        }

        WriteText(context, ReadFileCached(source.Path));
    }

    /// <summary>The file's byte length, or 0 when it is absent / unreadable (treated as "empty" for the UI).</summary>
    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void WriteFile(HttpListenerContext context, string taskId, string? name, int? requestedAttempt)
    {
        if (string.IsNullOrEmpty(name) || !IsSafeFileName(name))
        {
            TrySetStatus(context, HttpStatusCode.BadRequest);
            return;
        }

        string? attemptDir = ResolveAttemptDir(taskId, requestedAttempt, out _);
        if (attemptDir is null)
        {
            WriteText(context, string.Empty); // attempt not started yet — empty, page keeps polling
            return;
        }

        string full = Path.GetFullPath(Path.Combine(attemptDir, name));
        string root = Path.GetFullPath(attemptDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        WriteText(context, ReadFileCached(full));
    }

    /// <summary>
    /// Reads <paramref name="full"/> (an absolute path), reusing the last-served content when the
    /// file's current length and last-write time are both unchanged since that serve. The task
    /// page polls /file every second; an idle log would otherwise be fully re-read each tick. The
    /// cache is keyed on (Length, LastWriteTimeUtc): these logs are append-only and the writer
    /// touches mtime on every write, so any active write invalidates the entry — it never serves
    /// stale bytes. Access is serialised by <see cref="_fileCacheLock"/> (concurrent accept loop).
    /// </summary>
    private string ReadFileCached(string full)
    {
        var info = new FileInfo(full);
        long length = info.Length;
        DateTime lastWriteUtc = info.LastWriteTimeUtc;

        lock (_fileCacheLock)
        {
            if (_fileCache.TryGetValue(full, out CachedFile cached) &&
                cached.Length == length && cached.LastWriteTimeUtc == lastWriteUtc)
            {
                return cached.Content;
            }
        }

        // Cache miss or the file changed since the last serve — re-read. The producing process may
        // still be writing, so read with a fully shared handle (identical to the original open).
        string content;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            content = reader.ReadToEnd();
        }

        lock (_fileCacheLock)
        {
            _fileCache[full] = new CachedFile(length, lastWriteUtc, content);
        }

        return content;
    }

    // --- log-dir resolution -----------------------------------------------------------------

    /// <summary>
    /// Parse the <c>?attempt=N</c> query value to a positive attempt number, or null when absent /
    /// non-numeric / non-positive — in which case callers default to the latest attempt.
    /// </summary>
    private static int? ParseAttempt(string? raw) =>
        int.TryParse(raw, out int n) && n > 0 ? n : null;

    /// <summary>
    /// Every <c>attempt-N</c> directory number a task has on disk, ascending. Empty when the task
    /// has no log directory yet. Drives the attempt selector and the "which attempts exist" list.
    /// </summary>
    private IReadOnlyList<int> AttemptNumbers(string taskId)
    {
        string taskDir = Path.Combine(_logsRoot, taskId);
        if (!Directory.Exists(taskDir))
        {
            return Array.Empty<int>();
        }

        var numbers = new List<int>();
        foreach (string dir in Directory.EnumerateDirectories(taskDir))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.StartsWith("attempt-", StringComparison.Ordinal) &&
                int.TryParse(leaf.AsSpan("attempt-".Length), out int n))
            {
                numbers.Add(n);
            }
        }

        numbers.Sort();
        return numbers;
    }

    /// <summary>
    /// The directory for the SELECTED attempt: <paramref name="requestedAttempt"/> when that
    /// attempt-N directory exists, otherwise the highest-numbered (latest) attempt. Returns null —
    /// with <paramref name="attemptNumber"/> null — when the task has no attempts yet. The
    /// fall-back-to-latest keeps a mid-run page usable when a URL names an attempt that has not
    /// started, and preserves the pre-#103 "always latest" behaviour when no attempt is requested.
    /// </summary>
    private string? ResolveAttemptDir(string taskId, int? requestedAttempt, out int? attemptNumber)
    {
        attemptNumber = null;
        string taskDir = Path.Combine(_logsRoot, taskId);
        if (!Directory.Exists(taskDir))
        {
            return null;
        }

        if (requestedAttempt is { } requested)
        {
            string candidate = Path.Combine(taskDir, $"attempt-{requested}");
            if (Directory.Exists(candidate))
            {
                attemptNumber = requested;
                return candidate;
            }
        }

        string? best = null;
        int bestN = -1;
        foreach (string dir in Directory.EnumerateDirectories(taskDir))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf.StartsWith("attempt-", StringComparison.Ordinal) &&
                int.TryParse(leaf.AsSpan("attempt-".Length), out int n) && n > bestN)
            {
                bestN = n;
                best = dir;
            }
        }

        if (best is not null)
        {
            attemptNumber = bestN;
        }

        return best;
    }

    private static bool IsSafeFileName(string name) =>
        name.IndexOfAny(new[] { '/', '\\' }) < 0 &&
        !name.Contains("..", StringComparison.Ordinal) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // --- HTML -------------------------------------------------------------------------------

    private string LandingHtml()
    {
        bool withStatus = _statusForTask is not null;
        var rows = new StringBuilder();
        foreach (TaskNode task in _tasks)
        {
            string idEnc = Uri.EscapeDataString(task.Id);
            rows.Append("<tr><td><a href=\"/tasks/").Append(idEnc).Append("\">")
                .Append(WebUtility.HtmlEncode(task.Id)).Append("</a></td>");

            if (withStatus)
            {
                string status = _statusForTask!(task.Id) ?? "unknown";
                rows.Append("<td class=\"status\" data-status=\"").Append(WebUtility.HtmlEncode(status))
                    .Append("\">").Append(WebUtility.HtmlEncode(status)).Append("</td>");
            }

            rows.Append("<td>").Append(WebUtility.HtmlEncode(task.Description)).Append("</td></tr>");
        }

        return LandingTemplate
            .Replace("__STYLE__", LogSiteRenderer.SharedStyle)
            .Replace("__STATUS_TH__", withStatus ? "<th>Status</th>" : string.Empty)
            .Replace("__ROWS__", rows.ToString());
    }

    private static string TaskPageHtml(string taskId) =>
        TaskTemplate.Replace("__STYLE__", LogSiteRenderer.SharedStyle)
                    .Replace("__TASK_JSON__", JsonSerializer.Serialize(taskId))
                    .Replace("__TASK_HTML__", WebUtility.HtmlEncode(taskId));

    // --- response helpers -------------------------------------------------------------------

    private static void WriteHtml(HttpListenerContext context, string html) =>
        WriteBody(context, html, "text/html; charset=utf-8");

    private static void WriteJson(HttpListenerContext context, string json) =>
        WriteBody(context, json, "application/json; charset=utf-8");

    private static void WriteText(HttpListenerContext context, string text) =>
        WriteBody(context, text, "text/plain; charset=utf-8");

    private static void WriteBody(HttpListenerContext context, string body, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        // Prevent browsers from MIME-sniffing log content (which may contain LLM output that
        // looks like HTML) and rendering it as anything other than the declared type.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void TrySetStatus(HttpListenerContext context, HttpStatusCode code)
    {
        try { context.Response.StatusCode = (int)code; } catch (Exception) { /* client gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { /* loop ended */ }
        }

        _shutdown.Dispose();
    }

    // --- templates (placeholders filled per request) ----------------------------------------

    private const string LandingTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guardrails run — task logs</title>
<style>
__STYLE__
</style>
</head>
<body>
<h1>Guardrails run — task logs</h1>
<p>Attempt logs for each task, bound to localhost. Click a task to tail its log.</p>
<table>
<thead><tr><th>Task</th>__STATUS_TH__<th>Description</th></tr></thead>
<tbody>
__ROWS__
</tbody>
</table>
</body>
</html>
""";

    private const string TaskTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TASK_HTML__ — Guardrails log</title>
<style>
__STYLE__
</style>
</head>
<body>
<h1>__TASK_HTML__</h1>
<div class="bar"><a href="/">&larr; all tasks</a>
  &middot; attempt <select id="attempt"></select>
  &middot; file <select id="file"></select>
  &middot; <span id="tick">live</span></div>
<pre id="log">waiting for log output…</pre>
<h2>Source</h2>
<div class="bar" id="source">loading source…</div>
<script>
const TASK = __TASK_JSON__;
let current = null;          // selected file name
let attempt = null;          // selected attempt number (null = follow latest)
let pinned = false;          // true once the user explicitly picks an attempt — stop auto-following latest
let sourceLoaded = false;    // the Source list is static per task — load it once

function attemptQuery() { return attempt === null ? '' : `?attempt=${encodeURIComponent(attempt)}`; }

// Apply the empty-file marking to one <option>: grey it (the .empty CSS class) and suffix " (empty)"
// when the file is zero bytes, so an empty stdout/stderr capture is distinguishable in the dropdown
// (#141 item 4).
function markOption(o, name, empty) {
  o.value = name;
  o.textContent = empty ? name + ' (empty)' : name;
  o.classList.toggle('empty', !!empty);
}

async function refreshFiles() {
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/files${attemptQuery()}`);
    if (!r.ok) return;
    const d = await r.json();

    // Attempt selector: rebuild only when the set of attempts changed, so a new attempt appearing
    // mid-run does not clobber the user's current selection.
    const asel = document.getElementById('attempt');
    const attempts = d.attempts ?? (d.attempt != null ? [d.attempt] : []);
    const haveAttempts = [...asel.options].map(o => Number(o.value));
    const changed = attempts.length !== haveAttempts.length ||
                    attempts.some((a, i) => a !== haveAttempts[i]);
    if (changed) {
      const keep = asel.value;
      asel.innerHTML = '';
      for (const a of attempts) {
        const o = document.createElement('option');
        o.value = a; o.textContent = 'attempt ' + a; asel.appendChild(o);
      }
      // Unpinned: follow the latest (server-reported) attempt. Pinned: keep the user's choice.
      asel.value = pinned && keep ? keep : (d.attempt != null ? d.attempt : '');
    }
    if (!pinned && d.attempt != null) { attempt = d.attempt; asel.value = d.attempt; }

    // Build the file <select> from fileDetails (carrying each file's empty flag) when present,
    // falling back to the bare names. A zero-byte file's option is greyed + " (empty)" (#141 item 4).
    const sel = document.getElementById('file');
    const have = new Set([...sel.options].map(o => o.value));
    const details = d.fileDetails ?? (d.files ?? []).map(n => ({ name: n, empty: false }));
    for (const fd of details) {
      if (!have.has(fd.name)) {
        const o = document.createElement('option');
        markOption(o, fd.name, fd.empty);
        sel.appendChild(o);
      }
    }
    if (current === null && details.length) {
      sel.value = d.preferred ?? details[0].name;
      current = sel.value;
    }
  } catch (e) { /* server stopped — run probably ended */ }
}

async function refreshLog() {
  const sel = document.getElementById('file');
  current = sel.value || current;
  if (!current) return;
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/file?name=${encodeURIComponent(current)}${attempt === null ? '' : '&attempt=' + encodeURIComponent(attempt)}`);
    if (!r.ok) return;
    const t = await r.text();
    const pre = document.getElementById('log');
    const nearBottom = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 40;
    pre.textContent = t.length ? t : 'waiting for log output…';
    if (nearBottom) pre.scrollTop = pre.scrollHeight;
    const tick = document.getElementById('tick');
    tick.textContent = 'updated ' + new Date().toLocaleTimeString();
  } catch (e) { /* transient */ }
}

// The "Source" section (#141 item 3): list the task's action + guardrail files; clicking one fetches
// its raw text into the log <pre> with a header, so a thrown guardrail's script is one click away. The
// set is static per task, so it is fetched once. An empty source is greyed + " (empty)" (#141 item 4).
async function loadSource() {
  if (sourceLoaded) return;
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/source`);
    if (!r.ok) return;
    const d = await r.json();
    const host = document.getElementById('source');
    host.innerHTML = '';
    (d.sources ?? []).forEach((s, i) => {
      if (i > 0) host.appendChild(document.createTextNode(' · '));
      const a = document.createElement('a');
      a.href = '#';
      a.textContent = s.empty ? s.label + ' (empty)' : s.label;
      if (s.empty) a.classList.add('empty');
      a.addEventListener('click', (ev) => { ev.preventDefault(); viewSource(s.name, s.label); });
      host.appendChild(a);
    });
    sourceLoaded = true;
  } catch (e) { /* server stopped */ }
}

async function viewSource(name, label) {
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/sourcefile?name=${encodeURIComponent(name)}`);
    const pre = document.getElementById('log');
    if (!r.ok) { pre.textContent = 'could not load source: ' + label; return; }
    const t = await r.text();
    pre.textContent = '── source: ' + label + ' ──\n\n' + (t.length ? t : '(empty)');
    // Stop tailing a log over the source view: clear the file selection until the user re-picks one.
    current = null;
    document.getElementById('tick').textContent = 'viewing source: ' + label;
  } catch (e) { /* transient */ }
}

document.getElementById('file').addEventListener('change', () => {
  current = document.getElementById('file').value;
  refreshLog();
});

document.getElementById('attempt').addEventListener('change', () => {
  // Pin to the user's chosen attempt and reset the file list, since a different attempt has its
  // own files (and may prefer a different default file).
  attempt = Number(document.getElementById('attempt').value);
  pinned = true;
  current = null;
  document.getElementById('file').innerHTML = '';
  refreshFiles().then(refreshLog);
});

setInterval(refreshFiles, 2000);
setInterval(refreshLog, 1000);
loadSource();
refreshFiles().then(refreshLog);
</script>
</body>
</html>
""";
}
