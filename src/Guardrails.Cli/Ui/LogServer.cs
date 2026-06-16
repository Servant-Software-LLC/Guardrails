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
///   <item><c>GET /tasks/{id}</c> — a page that tails the latest attempt's log directory.</item>
///   <item><c>GET /tasks/{id}/files</c> — JSON: the latest attempt number + the files in it.</item>
///   <item><c>GET /tasks/{id}/file?name={f}</c> — the raw text of one log file (tailed by the page).</item>
/// </list>
///
/// The <c>{id}</c> must be a known task id and <c>{name}</c> a bare filename inside the attempt
/// directory — both are validated to keep the file surface inside <c>state/logs/&lt;id&gt;/</c>.
/// </summary>
public sealed class LogServer : IAsyncDisposable
{
    private const string PreferredPrimary = "claude-stream.jsonl";
    private const string PreferredSecondary = "action-stdout.log";

    private readonly HttpListener _listener;
    private readonly string _logsRoot;
    private readonly IReadOnlyList<TaskNode> _tasks;
    private readonly HashSet<string> _taskIds;
    private readonly Func<string, string?>? _statusForTask;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

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
    /// <param name="planDirectory">Plan folder whose <c>state/logs/</c> tree is served.</param>
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

                string logsRoot = Path.Combine(planDirectory, "state", "logs");
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
                WriteJson(context, FilesJson(taskId));
                return;
            case "file":
                WriteFile(context, taskId, context.Request.QueryString["name"]);
                return;
            default:
                TrySetStatus(context, HttpStatusCode.NotFound);
                return;
        }
    }

    // --- payloads ---------------------------------------------------------------------------

    private string FilesJson(string taskId)
    {
        string? attemptDir = LatestAttemptDir(taskId, out int? attemptNumber);
        var files = attemptDir is null
            ? new List<string>()
            : Directory.EnumerateFiles(attemptDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

        string? preferred =
            files.Contains(PreferredPrimary) ? PreferredPrimary :
            files.Contains(PreferredSecondary) ? PreferredSecondary :
            files.FirstOrDefault();

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

            if (preferred is null)
            {
                writer.WriteNull("preferred");
            }
            else
            {
                writer.WriteString("preferred", preferred);
            }

            writer.WriteStartArray("files");
            foreach (string file in files)
            {
                writer.WriteStringValue(file);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteFile(HttpListenerContext context, string taskId, string? name)
    {
        if (string.IsNullOrEmpty(name) || !IsSafeFileName(name))
        {
            TrySetStatus(context, HttpStatusCode.BadRequest);
            return;
        }

        string? attemptDir = LatestAttemptDir(taskId, out _);
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

    /// <summary>The highest-numbered <c>attempt-N</c> directory for a task, or null if none yet.</summary>
    private string? LatestAttemptDir(string taskId, out int? attemptNumber)
    {
        attemptNumber = null;
        string taskDir = Path.Combine(_logsRoot, taskId);
        if (!Directory.Exists(taskDir))
        {
            return null;
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
            .Replace("__STATUS_TH__", withStatus ? "<th>Status</th>" : string.Empty)
            .Replace("__ROWS__", rows.ToString());
    }

    private static string TaskPageHtml(string taskId) =>
        TaskTemplate.Replace("__TASK_JSON__", JsonSerializer.Serialize(taskId))
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
  body { font-family: system-ui, sans-serif; margin: 2rem; background: #0b0f14; color: #d6deeb; }
  h1 { font-size: 1.3rem; }
  p { color: #8aa0b3; }
  table { border-collapse: collapse; margin-top: 1rem; width: 100%; }
  td, th { padding: .45rem .8rem; border-bottom: 1px solid #1c2733; text-align: left; }
  th { color: #8aa0b3; font-weight: 600; }
  a { color: #7fdbff; text-decoration: none; }
  a:hover { text-decoration: underline; }
  td.status { font-weight: 600; }
  td.status[data-status="succeeded"], td.status[data-status="skipped"] { color: #3fb950; }
  td.status[data-status="needs-human"], td.status[data-status="failed"] { color: #f85149; }
  td.status[data-status="running"] { color: #d29922; }
  td.status[data-status="pending"], td.status[data-status="blocked"], td.status[data-status="unknown"] { color: #8aa0b3; }
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
  body { font-family: system-ui, sans-serif; margin: 1.5rem; background: #0b0f14; color: #d6deeb; }
  h1 { font-size: 1.15rem; }
  a { color: #7fdbff; text-decoration: none; }
  a:hover { text-decoration: underline; }
  .bar { color: #8aa0b3; margin-bottom: .8rem; }
  select { background: #121a24; color: #d6deeb; border: 1px solid #243343; border-radius: 4px; padding: .2rem .4rem; }
  pre { background: #06090d; border: 1px solid #1c2733; border-radius: 6px; padding: 1rem;
        white-space: pre-wrap; word-break: break-word; font-size: .82rem; line-height: 1.35;
        max-height: 75vh; overflow: auto; }
</style>
</head>
<body>
<h1>__TASK_HTML__</h1>
<div class="bar"><a href="/">&larr; all tasks</a> &middot; attempt <span id="attempt">—</span>
  &middot; file <select id="file"></select>
  &middot; <span id="tick">live</span></div>
<pre id="log">waiting for log output…</pre>
<script>
const TASK = __TASK_JSON__;
let current = null;

async function refreshFiles() {
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/files`);
    if (!r.ok) return;
    const d = await r.json();
    document.getElementById('attempt').textContent = (d.attempt ?? '—');
    const sel = document.getElementById('file');
    const have = new Set([...sel.options].map(o => o.value));
    for (const f of d.files) {
      if (!have.has(f)) {
        const o = document.createElement('option');
        o.value = f; o.textContent = f; sel.appendChild(o);
      }
    }
    if (current === null && d.files.length) {
      sel.value = d.preferred ?? d.files[0];
      current = sel.value;
    }
  } catch (e) { /* server stopped — run probably ended */ }
}

async function refreshLog() {
  const sel = document.getElementById('file');
  current = sel.value || current;
  if (!current) return;
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/file?name=${encodeURIComponent(current)}`);
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

document.getElementById('file').addEventListener('change', () => {
  current = document.getElementById('file').value;
  refreshLog();
});
setInterval(refreshFiles, 2000);
setInterval(refreshLog, 1000);
refreshFiles().then(refreshLog);
</script>
</body>
</html>
""";
}
