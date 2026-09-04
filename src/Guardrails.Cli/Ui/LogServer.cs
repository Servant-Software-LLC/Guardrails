using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// A loopback-only HTTP server that surfaces each task's live attempt log while a run is in
/// flight, so the user can answer "is it actually working?" without leaving the terminal.
/// Bound to <c>127.0.0.1</c> on an ephemeral port (logs may echo secrets — it is NEVER exposed
/// off the local machine). The lifetime is the run: <see cref="TryStart"/> at the top,
/// <see cref="DisposeAsync"/> in a finally.
///
/// The CANONICAL "all tasks" page is the static index file (<c>logs/&lt;runId&gt;/index.html</c>,
/// rendered by <see cref="LogSiteRenderer"/>) — durable and server-independent. This live server is
/// the active-only TAILING backend reached BY clicking a running task from that static index, so it
/// no longer serves its own all-tasks landing: <c>GET /</c> is a pointer note at the static index's
/// PATH (a browser can't follow <c>http→file</c>, so the path is shown as text), and the per-task
/// page is an active-task DEADEND (no "all tasks" link — the user hits Back) (issue #143).
///
/// Routes:
/// <list type="bullet">
///   <item><c>GET /</c> — a small pointer note directing the user to the canonical static index file
///     (this server only tails active tasks; it cannot link to <c>file://</c>).</item>
///   <item><c>GET /diagram.html</c> — the live status diagram <c>logs/&lt;runId&gt;/diagram.html</c>
///     (issue #522), which <see cref="OnTheFlyDiagramObserver"/> keeps written to that exact path; a
///     404 when the run has not written one yet.</item>
///   <item><c>GET /events</c> — a long-lived stream of <c>logs/&lt;runId&gt;/events.jsonl</c> (plan 34),
///     which <see cref="Core.Execution.RunEventStream"/> keeps appended to: a late subscriber first
///     receives every row already on disk, then subsequent rows as they are appended, one parseable
///     JSON object per line, over the same connection; an empty (not-yet-started) stream when the file
///     does not exist yet, never a 404. On shutdown, <see cref="WriteEventsStream"/> makes a best-effort
///     attempt to deliver a row landing in the final poll interval (e.g. the terminal <c>run-finished</c>
///     row) before the connection ends — best-effort, not a guarantee: the file on disk is always the
///     durable record, and a subscriber whose connection drops for its own reasons re-reads it there.</item>
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
///   <item><c>GET /tasks/{id}/guardrails/{file}</c> and <c>GET /tasks/{id}/preflights/{file}</c> —
///     the raw text of ONE of the task's declared check scripts (issue #522): these are exactly the
///     hrefs <c>MermaidRenderer</c> writes into the diagram's <c>click</c> directives for that task's
///     check nodes, resolved only through the precomputed per-folder source set (an unknown name, or
///     a name declared under the OTHER folder, is rejected).</item>
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

    /// <summary>How often <see cref="WriteEventsStream"/> re-checks <c>events.jsonl</c> for growth once it has caught up.</summary>
    private static readonly TimeSpan EventsPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Upper bound on how long <see cref="DisposeAsync"/> waits for in-flight requests (chiefly a
    /// still-parked <c>GET /events</c> stream) to notice <see cref="_shutdown"/> and return before it
    /// stops the listener. Generous relative to <see cref="EventsPollInterval"/> so the normal path (a
    /// handler waking on cancellation and returning within milliseconds) never brushes it — this only
    /// bounds a pathological handler, and shutdown proceeds regardless once it expires.
    /// </summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <see cref="FinishTeardownAsync"/> waits before actually stopping the listener, once
    /// <see cref="DisposeAsync"/> has already returned. A subscriber's next read of an already-flushed
    /// final row can only be issued after <see cref="DisposeAsync"/> returns, and the shared HTTP.sys
    /// request queue resets every connection it still tracks the instant the listener stops — including
    /// one whose response already completed gracefully — discarding whatever the peer has received but
    /// not yet read. This linger is the subscriber's only real window to read before that reset lands.
    /// Generous relative to loopback round-trip latency (routinely sub-millisecond) without meaningfully
    /// delaying real shutdown.
    /// </summary>
    private static readonly TimeSpan ListenerTeardownLinger = TimeSpan.FromMilliseconds(250);

    private readonly HttpListener _listener;
    private readonly string _logsRoot;
    private readonly IReadOnlyList<TaskNode> _tasks;
    private readonly HashSet<string> _taskIds;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;

    // Every request dispatched by AcceptLoopAsync (see the comment there), so DisposeAsync can wait for
    // ALL of them to finish before disposing _shutdown — a long-lived GET /events stream (plan 34) polls
    // _shutdown.Token.WaitHandle, and disposing that CancellationTokenSource while a wait is outstanding
    // on it is undefined behaviour, so nothing may touch it until every dispatched request has returned.
    private readonly object _inFlightLock = new();
    private readonly List<Task> _inFlightRequests = new();

    // #387 v2: the run's escalations/ dir (logs/<runId>/escalations/) and whether the run is under the
    // proceed-unreviewed opt-in — read for the /tasks/{id}/escalations pick panel and the POST /answer writer,
    // which enforce the SAME non-answerable floor the resume-time consumer does (via EscalationPick).
    private readonly string _escalationsDir;
    private readonly bool _proceedUnreviewed;

    // Per-task source files (the action + every guardrail script + any .json sidecar), precomputed from
    // the plan's TaskNode definitions so the source routes (#141 item 3) resolve a requested name ONLY
    // against this known set — path-safe by construction (an unknown name never resolves to a path).
    // Keyed by task id; the inner map is filename → SourceFile (absolute path + label).
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, LogSiteRenderer.SourceFile>> _sourcesByTask;

    // Per-task check scripts (guardrails + preflights), keyed task id → ((folder, filename) → SourceFile)
    // (issue #522). A SEPARATE map from _sourcesByTask: that one is keyed by bare filename alone (the
    // existing sourcefile route's contract, unchanged) and never included preflights; this one keys on
    // the folder too, so a same-named guardrails/x.ps1 and preflights/x.ps1 resolve to their own file
    // instead of colliding — the diagram's own click hrefs always carry that folder segment
    // (tasks/{id}/guardrails/{file} or tasks/{id}/preflights/{file}), so the lookup can require it.
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<(string Folder, string Name), LogSiteRenderer.SourceFile>> _checkScriptsByTask;

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
        bool proceedUnreviewed)
    {
        _listener = listener;
        _baseUrl = baseUrl;
        _logsRoot = logsRoot;
        _tasks = tasks;
        _taskIds = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.Ordinal);
        _sourcesByTask = BuildSourceMap(tasks);
        _checkScriptsByTask = BuildCheckScriptMap(tasks);
        _escalationsDir = Path.Combine(logsRoot, "escalations");
        _proceedUnreviewed = proceedUnreviewed;
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

    /// <summary>
    /// Precompute each task's check scripts (issue #522) — its guardrail scripts AND its preflight
    /// scripts, each with its optional <c>.json</c> sidecar — keyed task id → ((folder, filename) →
    /// <see cref="LogSiteRenderer.SourceFile"/>). The folder ("guardrails" or "preflights") is part of
    /// the key because the two lists may legally share a filename (issue #332); keying on the pair is
    /// what lets <c>tasks/{id}/guardrails/{file}</c> and <c>tasks/{id}/preflights/{file}</c> resolve to
    /// the right one instead of colliding. A duplicate (folder, filename) keeps the first.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<(string Folder, string Name), LogSiteRenderer.SourceFile>> BuildCheckScriptMap(
        IReadOnlyList<TaskNode> tasks)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<(string, string), LogSiteRenderer.SourceFile>>(StringComparer.Ordinal);
        foreach (TaskNode task in tasks)
        {
            var byFolderAndName = new Dictionary<(string Folder, string Name), LogSiteRenderer.SourceFile>();
            AddCheckScripts(byFolderAndName, "guardrails", task.Guardrails);
            AddCheckScripts(byFolderAndName, "preflights", task.Preflights);
            map[task.Id] = byFolderAndName;
        }

        return map;
    }

    /// <summary>Add one folder's check scripts (+ any <c>.json</c> sidecar) into a check-script map under <paramref name="folder"/>.</summary>
    private static void AddCheckScripts(
        Dictionary<(string Folder, string Name), LogSiteRenderer.SourceFile> map,
        string folder,
        IReadOnlyList<GuardrailDefinition> checks)
    {
        foreach (GuardrailDefinition check in checks)
        {
            string scriptName = Path.GetFileName(check.Path);
            map.TryAdd((folder, scriptName), new LogSiteRenderer.SourceFile(scriptName, scriptName, check.Path));

            string sidecar = Path.ChangeExtension(check.Path, ".json");
            if (File.Exists(sidecar))
            {
                string sidecarName = Path.GetFileName(sidecar);
                map.TryAdd((folder, sidecarName), new LogSiteRenderer.SourceFile(sidecarName, sidecarName, sidecar));
            }
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
    public static LogServer? TryStart(
        string planDirectory,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        int port,
        TextWriter warn,
        bool proceedUnreviewed = false)
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
                var server = new LogServer(listener, baseUrl, logsRoot, tasks, proceedUnreviewed);
                server._acceptLoop = Task.Run(server.AcceptLoopAsync);
                return server;
            }

            // Exhausted the retry budget without binding — surface the last race failure to the
            // existing warn-and-return-null path below.
            throw lastBindFailure!;
        }
        catch (Exception ex)
        {
            // Starting the viewer must NEVER be able to fail a run (issue #552). Until that issue this
            // path only ran in an interactive terminal — an environment the operator is watching and
            // whose failure they can interpret. It now runs for EVERY run that did not pass
            // --no-log-server: CI, a service, a backgrounded shell, a locked-down sandbox with no
            // socket permission. So the catch is deliberately total rather than a list of the three
            // shapes we happened to foresee: a viewer that cannot start is a lost convenience, never a
            // lost run. The expected shapes (a bind race, a refused socket, an unsupported platform)
            // report just their message; anything else names its type, so an unforeseen environment is
            // diagnosable from the run log instead of merely mysterious.
            bool expected = ex is HttpListenerException or SocketException or PlatformNotSupportedException;
            string detail = expected ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            warn.WriteLine($"Log server not started ({detail}); run continues without live log links.");
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

            // Dispatched onto its own task rather than awaited in-line: GET /events (plan 34) is a
            // genuinely long-lived streaming connection, and the accept loop must keep pulling the NEXT
            // queued connection off the listener while that one is still open — matching the concurrent
            // access this class already assumes elsewhere (see the comment on _fileCacheLock). Tracked in
            // _inFlightRequests so DisposeAsync can wait for it to notice _shutdown before disposing the
            // CancellationTokenSource out from under it (see the comment there).
            Task requestTask = Task.Run(() => HandleAndClose(context));
            lock (_inFlightLock)
            {
                _inFlightRequests.RemoveAll(t => t.IsCompleted);
                _inFlightRequests.Add(requestTask);
            }
        }
    }

    private void HandleAndClose(HttpListenerContext context)
    {
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

    private void Handle(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool isPost = string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        if (segments.Length == 0)
        {
            if (isPost) { TrySetStatus(context, HttpStatusCode.MethodNotAllowed); return; }
            WriteHtml(context, PointerNoteHtml());
            return;
        }

        // GET /diagram.html (issue #522): the live status diagram OnTheFlyDiagramObserver keeps written
        // to logs/<runId>/diagram.html. Checked before the "tasks" gate below since it is a top-level
        // path of its own, not a task route — and it is an explicit single case, not a wildcard static
        // file server over _logsRoot (ServedDiagram tests pin that nothing else under logs/<runId>/ is
        // reachable this way).
        if (segments.Length == 1 && segments[0] == "diagram.html")
        {
            if (isPost) { TrySetStatus(context, HttpStatusCode.MethodNotAllowed); return; }
            WriteDiagramFile(context);
            return;
        }

        // GET /events (plan 34): a top-level, long-lived stream of this run's events.jsonl (the
        // RunEventStream projection) — a late subscriber first receives every row already on disk,
        // then subsequent rows as they are appended, over the SAME connection (unlike the
        // poll-again-later /file route). Checked before the "tasks" gate for the same reason as
        // /diagram.html: a top-level path of its own, not a task route, and an explicit single case
        // rather than a wildcard static file server over _logsRoot.
        if (segments.Length == 1 && segments[0] == "events")
        {
            if (isPost) { TrySetStatus(context, HttpStatusCode.MethodNotAllowed); return; }
            WriteEventsStream(context);
            return;
        }

        if (segments[0] != "tasks")
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        // /tasks/{id}[/files|/file|/escalations|/answer]
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

        // #387 v2: POST /tasks/{id}/answer writes the operator's chosen option as the firstmate reply file
        // (the FILE stays the single source of truth — no daemon state/socket/queue); every other route is GET.
        if (isPost)
        {
            if (segments.Length == 3 && segments[2] == "answer")
            {
                HandleAnswerPost(context, taskId);
            }
            else
            {
                TrySetStatus(context, HttpStatusCode.MethodNotAllowed);
            }

            return;
        }

        if (segments.Length == 2)
        {
            WriteHtml(context, TaskPageHtml(taskId));
            return;
        }

        // /tasks/{id}/guardrails/{file} and /tasks/{id}/preflights/{file} (issue #522): the diagram's own
        // check-node hrefs. Resolved only through _checkScriptsByTask, keyed on (folder, filename) — never
        // by joining the request onto a directory path — so a name the task does not declare, or a name
        // declared only under the OTHER folder, is a plain 404.
        if (segments.Length == 4 && (segments[2] == "guardrails" || segments[2] == "preflights"))
        {
            WriteCheckScript(context, taskId, segments[2], Uri.UnescapeDataString(segments[3]));
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
            case "escalations":
                WriteJson(context, EscalationsJson(taskId));
                return;
            default:
                TrySetStatus(context, HttpStatusCode.NotFound);
                return;
        }
    }

    /// <summary>
    /// JSON for <c>GET /tasks/{id}/escalations</c> (#387 v2): this task's OPEN, options-carrying
    /// <c>needs-human</c> escalations — each <c>{ seq, gate, question, options[], answerable, reason }</c>. The
    /// task page renders an ANSWERABLE one's options as buttons; a NON-answerable one (a clamped hard call under
    /// proceed-unreviewed, §7.3) renders NO buttons and shows its halt <c>reason</c> instead. Reuses the SAME
    /// <see cref="EscalationPick.ReadOpen"/> the interactive pick does, so both surfaces present identically.
    /// </summary>
    private string EscalationsJson(string taskId)
    {
        IReadOnlyList<PickableEscalation> escalations = EscalationPick.ReadOpen(_escalationsDir, _proceedUnreviewed)
            .Where(e => string.Equals(e.Subject, taskId, StringComparison.Ordinal))
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("escalations");
            foreach (PickableEscalation e in escalations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("seq", e.Seq);
                writer.WriteString("gate", e.Gate);
                writer.WriteString("question", e.Question);
                writer.WriteBoolean("answerable", e.Answerable);
                if (e.NonAnswerableReason is { } reason)
                {
                    writer.WriteString("reason", reason);
                }

                writer.WriteStartArray("options");
                foreach (string option in e.Options)
                {
                    writer.WriteStringValue(option);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Handle <c>POST /tasks/{id}/answer</c> (#387 v2): parse <c>{ seq, gate, choice }</c> from the request body,
    /// confirm the escalation's subject is THIS task, and write the choice through the shared
    /// <see cref="EscalationPick.WriteChoice"/> — the HARD floor that validates identity, REFUSES a non-answerable
    /// gate / clamped hard call (§7.3), and rejects an off-menu choice. The reply FILE it writes is the single
    /// source of truth, consumed unchanged on the next resume. The <see cref="PickWriteResult"/> maps to an HTTP
    /// status so a non-answerable POST is REJECTED (403), not written.
    /// </summary>
    private void HandleAnswerPost(HttpListenerContext context, string taskId)
    {
        AnswerPostBody? body = ReadAnswerPostBody(context);
        if (body is null || body.Gate is null || body.Choice is null || body.Seq is null)
        {
            TrySetStatus(context, HttpStatusCode.BadRequest);
            return;
        }

        // EscalationPick.WriteChoice is the SINGLE authority on seq+gate: it validates the record exists + is
        // unconsumed, enforces the non-answerable floor (a review-gate / clamped hard call is REFUSED, never
        // written), and rejects an off-menu choice. The URL taskId only routes to the page the button lives on
        // (a needs-human escalation's subject IS its task id); the JSON body carries the authoritative binding.
        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escalationsDir, body.Seq.Value, body.Gate, body.Choice, "log-viewer-pick", _proceedUnreviewed);
        WriteAnswerOutcome(context, outcome);
    }

    /// <summary>Map an <see cref="EscalationPick.WriteChoice"/> outcome to an HTTP status + a JSON <c>{ result, message }</c> body.</summary>
    private static void WriteAnswerOutcome(HttpListenerContext context, PickWriteOutcome outcome)
    {
        HttpStatusCode status = outcome.Result switch
        {
            PickWriteResult.Written => HttpStatusCode.OK,
            PickWriteResult.NotFound => HttpStatusCode.NotFound,
            PickWriteResult.AlreadyConsumed => HttpStatusCode.Conflict,
            PickWriteResult.RefusedNonAnswerable => HttpStatusCode.Forbidden,
            PickWriteResult.OptionNotOffered => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.BadRequest
        };

        string json = JsonSerializer.Serialize(new { result = outcome.Result.ToString(), message = outcome.Message });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Read + parse the <c>POST /answer</c> JSON body <c>{ seq, gate, choice }</c>; null on any read/parse failure.</summary>
    private static AnswerPostBody? ReadAnswerPostBody(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            string raw = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AnswerPostBody>(raw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The <c>POST /tasks/{id}/answer</c> request body (#387 v2): the escalation <c>seq</c> + <c>gate</c> and the chosen <c>choice</c>.</summary>
    private sealed record AnswerPostBody
    {
        public int? Seq { get; init; }
        public string? Gate { get; init; }
        public string? Choice { get; init; }
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

    /// <summary>
    /// Serve <c>GET /diagram.html</c> (issue #522): the live status diagram at
    /// <c>&lt;logsRoot&gt;/diagram.html</c>, which <see cref="OnTheFlyDiagramObserver"/> keeps written to
    /// exactly that path. 404 when the run has not written one yet — never an empty 200 or a stub page,
    /// so "the diagram isn't ready" is never mistaken for "the diagram is empty".
    /// </summary>
    private void WriteDiagramFile(HttpListenerContext context)
    {
        string path = Path.Combine(_logsRoot, "diagram.html");
        if (!File.Exists(path))
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        WriteHtml(context, ReadFileCached(path));
    }

    /// <summary>
    /// Serve <c>GET /events</c> (plan 34): stream <c>&lt;logsRoot&gt;/events.jsonl</c> to a subscriber —
    /// every row already on disk, then each row appended afterward, one parseable JSON object per line,
    /// over a single open connection. A run that has not emitted anything yet (no <c>events.jsonl</c> on
    /// disk) is still a HEALTHY run: the response completes immediately with an empty body rather than
    /// staying open or erroring, mirroring <see cref="WriteFile"/>'s "attempt not started yet" idiom.
    /// Once the file exists, the response is chunked and polls the file for growth until the client
    /// disconnects (a write fails) or the server shuts down (<see cref="_shutdown"/>).
    /// </summary>
    private void WriteEventsStream(HttpListenerContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";

        string path = Path.Combine(_logsRoot, "events.jsonl");
        if (!File.Exists(path))
        {
            context.Response.ContentLength64 = 0;
            return;
        }

        context.Response.SendChunked = true;
        Stream output = context.Response.OutputStream;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            using var pending = new MemoryStream();

            void EmitLines(int read)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        pending.WriteByte((byte)'\n');
                        output.Write(pending.GetBuffer(), 0, (int)pending.Length);
                        pending.SetLength(0);
                    }
                    else
                    {
                        pending.WriteByte(buffer[i]);
                    }
                }

                output.Flush();
            }

            while (!_shutdown.IsCancellationRequested)
            {
                int read = fs.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    if (_shutdown.Token.WaitHandle.WaitOne(EventsPollInterval))
                    {
                        // Shutdown was signalled while parked in this wait. DisposeAsync now waits for
                        // this request to notice _shutdown and return before it lets the listener be torn
                        // down (see the comment there), so a row appended in this last poll interval —
                        // e.g. the run-finished row, written the instant before the caller disposes this
                        // server — has a real window to be attempted here rather than silently dropped.
                        try
                        {
                            int finalRead = fs.Read(buffer, 0, buffer.Length);
                            if (finalRead > 0)
                            {
                                EmitLines(finalRead);
                            }
                        }
                        catch (Exception ex)
                        {
                            // The row is already durable in events.jsonl, so a failure here only costs
                            // this one live subscriber the row — reported distinctly rather than through
                            // the catch-all below, so a genuine failure is never indistinguishable from a
                            // clean, successful delivery.
                            Console.Error.WriteLine(
                                $"[log-server] final /events flush on shutdown failed: {ex.GetType().Name}: {ex.Message}");
                        }

                        return;
                    }

                    continue;
                }

                EmitLines(read);
            }
        }
        catch (Exception)
        {
            // The client disconnected mid-stream, or the listener stopped underneath us — end quietly;
            // the accept loop closes the response either way.
        }
    }

    /// <summary>
    /// Serve <c>GET /tasks/{id}/guardrails/{file}</c> or <c>GET /tasks/{id}/preflights/{file}</c> (issue
    /// #522): the raw text of ONE of the task's declared check scripts, resolved ONLY through
    /// <see cref="_checkScriptsByTask"/>'s (folder, name) key. An unknown name, or a name declared only
    /// under the other folder, has no entry and is rejected — the path is never built from the request.
    /// </summary>
    private void WriteCheckScript(HttpListenerContext context, string taskId, string folder, string name)
    {
        if (!_checkScriptsByTask.TryGetValue(taskId, out var scripts) ||
            !scripts.TryGetValue((folder, name), out LogSiteRenderer.SourceFile source))
        {
            TrySetStatus(context, HttpStatusCode.NotFound);
            return;
        }

        if (!File.Exists(source.Path))
        {
            // Declared but absent on disk — empty body, not a crash (mirrors WriteSourceFile).
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

    /// <summary>
    /// The <c>GET /</c> pointer note (issue #143): the canonical "all tasks" page is the static index
    /// FILE (<c>&lt;logsRoot&gt;/index.html</c>), which is durable and works without this server. A browser
    /// cannot follow an <c>http→file://</c> link, so the static index's absolute path is shown as text for
    /// the user to open. This live server only tails ACTIVE tasks — it deliberately no longer serves an
    /// all-tasks landing of its own.
    /// </summary>
    private string PointerNoteHtml()
    {
        string indexPath = Path.GetFullPath(Path.Combine(_logsRoot, "index.html"));
        return PointerNoteTemplate
            .Replace("__STYLE__", LogSiteRenderer.SharedStyle)
            .Replace("__INDEX_PATH__", WebUtility.HtmlEncode(indexPath));
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

        // Wait for every dispatched request (including a still-polling GET /events stream) to notice
        // _shutdown and return, giving WriteEventsStream's final read-and-flush (see the comment there)
        // a real chance to complete before the listener is touched at all. Bounded by
        // ShutdownDrainTimeout so a pathological handler can never hang shutdown: every handler has
        // already been signalled by _shutdown.Cancel() above, and this stream's own poll interval is
        // 150ms, so the normal path costs milliseconds.
        Task[] pending;
        lock (_inFlightLock)
        {
            pending = _inFlightRequests.ToArray();
        }

        try { await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false); }
        catch (Exception) { /* individual request faults are already handled in HandleAndClose */ }

        // The listener's actual teardown runs afterward, deliberately NOT awaited here — see
        // FinishTeardownAsync for why returning before it runs is what makes a subscriber's read of an
        // already-flushed final row survive shutdown.
        _ = FinishTeardownAsync();
    }

    /// <summary>
    /// Stops and closes the shared <see cref="HttpListener"/>. Confirmed empirically: even once a
    /// handler's final write has completed with no exception (the wait in <see cref="DisposeAsync"/>
    /// already guarantees that), the underlying HTTP.sys request queue still tracks that connection —
    /// a completed HTTP response does not by itself end the TCP connection, since HTTP/1.1 keep-alive
    /// leaves it open awaiting reuse. <c>Stop()</c>/<c>Close()</c> tears the WHOLE queue down at the
    /// kernel level, which resets every connection it still tracks; a TCP reset discards whatever the
    /// peer has already received but not yet read off its own socket, regardless of how long ago the
    /// write completed on this side. The one lever available from here is WHEN that reset lands relative
    /// to the subscriber's own next read: a subscriber can only attempt that read once
    /// <see cref="DisposeAsync"/> has returned (its own code is sequenced that way), so if this ran
    /// inline on <see cref="DisposeAsync"/>'s await chain — no matter how long it waited first — the
    /// reset would always land before the read is even issued. Deferring the reset itself past that
    /// point, with a brief linger, is what gives an already-flushed row a real window to be read before
    /// the shared queue goes away underneath it.
    /// </summary>
    private async Task FinishTeardownAsync()
    {
        await Task.Delay(ListenerTeardownLinger).ConfigureAwait(false);

        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { /* loop ended */ }
        }

        _shutdown.Dispose();
    }

    // --- templates (placeholders filled per request) ----------------------------------------

    private const string PointerNoteTemplate = """
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
<p>This run's <strong>all-tasks page</strong> is the static index file — open it in your browser:</p>
<pre>__INDEX_PATH__</pre>
<p>This live server only <strong>tails active tasks</strong>. Reach a running task by clicking it on
the static index above; this page cannot link to the file directly (a browser blocks
<code>http://</code> &rarr; <code>file://</code>).</p>
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
<div class="bar">attempt <select id="attempt"></select>
  &middot; file <select id="file"></select>
  &middot; <span id="tick">live</span>
  &middot; <a id="resume" href="#" hidden>&#8617; back to live log</a></div>
<pre id="log">waiting for log output…</pre>
<div id="decision" hidden></div>
<h2>Source</h2>
<div class="bar" id="source">loading source…</div>
<script>
const TASK = __TASK_JSON__;
let current = null;          // selected file name
let attempt = null;          // selected attempt number (null = follow latest)
let pinned = false;          // true once the user explicitly picks an attempt — stop auto-following latest
let sourceLoaded = false;    // the Source list is static per task — load it once
let viewingSource = false;   // true while a Source file is shown in the log <pre> — pause tailing (#147)

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
  // While a Source file is shown in the <pre>, do NOT tail a log over it (#147): refreshLog re-derives
  // `current` from the file <select>, which would otherwise undo viewSource's pause within one tick.
  if (viewingSource) return;
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
    // Pause tailing so the 1s refreshLog tick can't overwrite the source view (#147). Cleared by the
    // "back to live log" control or by picking a file/attempt.
    viewingSource = true;
    current = null;
    document.getElementById('resume').hidden = false;
    document.getElementById('tick').textContent = 'viewing source: ' + label;
  } catch (e) { /* transient */ }
}

// Resume live tailing from a Source view (#147): clear the pause, hide the control, fetch the log now.
function backToLiveLog() {
  viewingSource = false;
  document.getElementById('resume').hidden = true;
  refreshLog();
}

document.getElementById('resume').addEventListener('click', (ev) => { ev.preventDefault(); backToLiveLog(); });

document.getElementById('file').addEventListener('change', () => {
  // Picking a file resumes live tailing of it, ending any Source view (#147).
  viewingSource = false;
  document.getElementById('resume').hidden = true;
  current = document.getElementById('file').value;
  refreshLog();
});

document.getElementById('attempt').addEventListener('change', () => {
  // Pin to the user's chosen attempt and reset the file list, since a different attempt has its
  // own files (and may prefer a different default file). Also ends any Source view (#147).
  viewingSource = false;
  document.getElementById('resume').hidden = true;
  attempt = Number(document.getElementById('attempt').value);
  pinned = true;
  current = null;
  document.getElementById('file').innerHTML = '';
  refreshFiles().then(refreshLog);
});

// #387 v2: the "awaiting your decision" panel. Poll /escalations for THIS task's open, options-carrying
// needsHuman escalations. An ANSWERABLE one renders its options as buttons; a NON-answerable one (a clamped
// hard call under proceed-unreviewed, §7.3) renders NO buttons and shows its halt reason instead. A click
// POSTs { seq, gate, choice } to /answer, which writes the reply file (consumed on the next resume).
let answered = {};  // seq-keyed: locally-picked escalations we stop re-rendering as open

async function refreshEscalations() {
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/escalations`);
    if (!r.ok) return;
    const d = await r.json();
    const host = document.getElementById('decision');
    const open = (d.escalations ?? []).filter(e => !answered[e.seq]);
    if (open.length === 0) { host.hidden = true; host.innerHTML = ''; return; }

    host.innerHTML = '<h2>Awaiting your decision</h2>';
    for (const e of open) {
      const block = document.createElement('div');
      block.className = 'decision-block';
      const q = document.createElement('p');
      q.textContent = e.question;
      block.appendChild(q);
      if (e.answerable) {
        for (const opt of (e.options ?? [])) {
          const btn = document.createElement('button');
          btn.type = 'button';
          btn.className = 'pick';
          btn.textContent = opt;
          btn.addEventListener('click', () => submitPick(e.seq, e.gate, opt, block));
          block.appendChild(btn);
        }
      } else {
        const halt = document.createElement('p');
        halt.className = 'halt-reason';
        halt.textContent = e.reason || 'This gate is not answerable — resolve it with real human work, not a pick.';
        block.appendChild(halt);
      }
      host.appendChild(block);
    }
    host.hidden = false;
  } catch (e) { /* server stopped — run probably ended */ }
}

async function submitPick(seq, gate, choice, block) {
  try {
    const r = await fetch(`/tasks/${encodeURIComponent(TASK)}/answer`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ seq, gate, choice })
    });
    const d = await r.json().catch(() => ({ message: r.ok ? 'recorded' : 'rejected' }));
    const note = document.createElement('p');
    note.className = r.ok ? 'pick-ok' : 'pick-err';
    note.textContent = d.message || (r.ok ? 'recorded — resume to apply' : 'rejected');
    block.querySelectorAll('button.pick').forEach(b => b.disabled = true);
    block.appendChild(note);
    if (r.ok) answered[seq] = true;
  } catch (e) {
    const note = document.createElement('p');
    note.className = 'pick-err';
    note.textContent = 'could not reach the log server';
    block.appendChild(note);
  }
}

setInterval(refreshFiles, 2000);
setInterval(refreshLog, 1000);
setInterval(refreshEscalations, 2000);
loadSource();
refreshEscalations();
refreshFiles().then(refreshLog);
</script>
</body>
</html>
""";
}
