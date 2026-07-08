using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// A decorator <see cref="IRunObserver"/> that keeps the DURING-RUN static log site
/// (<c>logs/&lt;runId&gt;/index.html</c> + per-task pages) up to date as the run proceeds (issue #141
/// item 2). It WRAPS the real observer (live <see cref="LiveRunObserver"/> or
/// <see cref="ConsoleRunObserver"/>), forwards every event verbatim, and AFTER forwarding rewrites
/// the site through <see cref="LogSiteRenderer"/>:
/// <list type="bullet">
///   <item>On <see cref="TaskStarting"/> a task flips pending → running and the index is rewritten —
///     a running task links to the LIVE server (a click tails it) when a server is up, else plain text.</item>
///   <item>On <see cref="TaskFinished"/> a task flips to its settled status, its static page is written
///     (<see cref="LogSiteRenderer.WriteTaskPageIfHasAttempts"/>), and the index is rewritten — a
///     settled task with attempts on disk now links to its static page.</item>
/// </list>
///
/// <para>The index always carries the <c>meta refresh</c> (<c>includeRefresh:true</c>) so a
/// <c>file://</c> view re-reads it as it is rewritten; the DURABLE final/<c>--export</c> index (no
/// refresh, all-static) is written separately by the run's end-of-run path, NOT here.</para>
///
/// <para>The renderer writes atomically, so a browser never reads a torn file. This decorator's own
/// status map is the only shared mutable state, and M4 worker threads call in concurrently, so all
/// access to it — and the write that projects from it — is serialised under one lock. Renders are
/// best-effort: a render failure (e.g. a transient file lock) is swallowed so a UX nicety never
/// flips a task's outcome or aborts the run.</para>
/// </summary>
public sealed class OnTheFlyLogSiteObserver : IRunObserver
{
    private readonly IRunObserver _inner;
    private readonly string _logsRoot;
    private readonly string _runId;
    private readonly IReadOnlyList<TaskNode> _tasks;
    private readonly IReadOnlyDictionary<string, TaskNode> _tasksById;
    private readonly Func<string, string?>? _liveUrlForTask;

    // Per-task status word, seeded "pending". Mutated and projected under one lock — events arrive
    // from concurrent M4 workers, and the index render reads the whole map, so the two must not race.
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _statusByTask;

    /// <param name="inner">The real observer every event is forwarded to (live or console).</param>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree the site is written into.</param>
    /// <param name="runId">The run id (titles the rendered index).</param>
    /// <param name="tasks">The plan's tasks, in plan order — the rows of the index.</param>
    /// <param name="liveUrlForTask">
    /// Resolver mapping a task id to its live-server URL, or null when no server is up. A RUNNING task
    /// links to this URL (a click tails it) when non-null; null = the running task is plain text.
    /// </param>
    public OnTheFlyLogSiteObserver(
        IRunObserver inner,
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? liveUrlForTask)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logsRoot = logsRoot;
        _runId = runId;
        _tasks = tasks;
        _tasksById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _liveUrlForTask = liveUrlForTask;
        _statusByTask = tasks.ToDictionary(
            t => t.Id, _ => LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Pending), StringComparer.Ordinal);
    }

    /// <summary>
    /// Write the initial all-pending index at run start (every task pending, plain text), so the "all
    /// tasks" page exists and is browsable the moment the run begins. Best-effort. Delegates to the
    /// static <see cref="WriteInitialIndex(string, string, IReadOnlyList{TaskNode}, Func{string, string?})"/>.
    /// </summary>
    public void WriteInitialIndex() => WriteInitialIndex(_logsRoot, _runId, _tasks, _liveUrlForTask);

    /// <summary>
    /// Write the initial all-pending index (every task <c>pending</c>, plain link, with the during-run
    /// meta-refresh) WITHOUT an observer instance, so the live path can write it — and print the
    /// link to it — BEFORE constructing <see cref="LiveRunObserver"/> (which starts the Spectre
    /// <c>AnsiConsole.Live</c> region in its ctor). Any console write into an active Live region
    /// corrupts the table, so the initial index + its static-index link must be emitted first (#145
    /// Bug 1). Best-effort: a render failure is swallowed (a UX nicety never aborts the run).
    /// </summary>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree the index is written into.</param>
    /// <param name="runId">The run id (titles the rendered index).</param>
    /// <param name="tasks">The plan's tasks, in plan order — the rows of the index.</param>
    /// <param name="liveUrlForTask">Unused at the all-pending start (no task is running yet); accepted
    /// so the static signature matches the instance's link-resolution surface for callers.</param>
    public static void WriteInitialIndex(
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? liveUrlForTask)
    {
        _ = liveUrlForTask; // no task is running at the all-pending start, so no live link is resolved yet
        string pending = LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Pending);
        TryRender(() => LogSiteRenderer.WriteIndex(
            logsRoot,
            runId,
            tasks,
            statusResolver: _ => pending,
            linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
            includeRefresh: true));
    }

    public void TaskStarting(TaskNode task)
    {
        _inner.TaskStarting(task);
        SetStatus(task.Id, LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Running));
        RenderIndex();
    }

    public void AttemptStarting(TaskNode task, int attempt, int budget) =>
        _inner.AttemptStarting(task, attempt, budget);

    public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
        _inner.GuardrailFinished(task, result);

    public void TaskFinished(TaskResult result)
    {
        _inner.TaskFinished(result);
        SetStatus(result.TaskId, StatusWord(result.Outcome));

        // Write the finished task's static page so the index's link to it (and the terminal's
        // post-mortem link, #141 item 1) resolves to a rendered page, not a 404.
        if (_tasksById.TryGetValue(result.TaskId, out TaskNode? task))
        {
            TryRender(() => LogSiteRenderer.WriteTaskPageIfHasAttempts(_logsRoot, task));
        }

        RenderIndex();
    }

    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    public void DecisionRecorded(DecisionEntry entry) => _inner.DecisionRecorded(entry);

    public void ParallelismClampedNoProvider(int requested) => _inner.ParallelismClampedNoProvider(requested);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        _inner.PromptPaused(task, reason, backoff, pauseCount);

    // --- site projection --------------------------------------------------------------------

    private void SetStatus(string taskId, string status)
    {
        lock (_gate)
        {
            _statusByTask[taskId] = status;
        }
    }

    /// <summary>
    /// Rewrite the during-run index (with refresh) from the current status map. Holds the lock for the
    /// whole render so the status snapshot and the link choice are consistent (the renderer writes
    /// atomically). The link resolver: a RUNNING task → the live URL when a server is up (else plain),
    /// any task with attempts on disk → its static page, anything else → plain text.
    /// </summary>
    private void RenderIndex()
    {
        lock (_gate)
        {
            // Snapshot the statuses inside the lock so the resolver closures read a stable view.
            var statuses = new Dictionary<string, string>(_statusByTask, StringComparer.Ordinal);
            TryRender(() => LogSiteRenderer.WriteIndex(
                _logsRoot,
                _runId,
                _tasks,
                statusResolver: id => statuses.TryGetValue(id, out string? s) ? s : "unknown",
                linkResolver: id => ResolveLink(id, statuses),
                includeRefresh: true));
        }
    }

    private LogSiteRenderer.IndexLink ResolveLink(string taskId, IReadOnlyDictionary<string, string> statuses)
    {
        bool running = statuses.TryGetValue(taskId, out string? status) &&
                       status == LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Running);

        // A running task links to the live server (a click tails it) when one is up; otherwise it is
        // plain text (no static page to point at yet).
        if (running && _liveUrlForTask?.Invoke(taskId) is { } liveUrl)
        {
            return LogSiteRenderer.IndexLink.LiveTo(liveUrl);
        }

        // A task with attempts on disk (running-without-server, or settled) links to its static page;
        // a pending/no-attempt task is plain text.
        return HasAttempts(taskId) ? LogSiteRenderer.IndexLink.Static : LogSiteRenderer.IndexLink.Plain;
    }

    private bool HasAttempts(string taskId)
    {
        string taskDir = Path.Combine(_logsRoot, taskId);
        if (!Directory.Exists(taskDir))
        {
            return false;
        }

        return Directory.EnumerateDirectories(taskDir)
            .Any(d => Path.GetFileName(d).StartsWith("attempt-", StringComparison.Ordinal));
    }

    /// <summary>Map a finished task's outcome to the index status word (mirrors the journal mapping).</summary>
    private static string StatusWord(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Succeeded),
        TaskOutcome.Skipped => "skipped",
        TaskOutcome.Blocked => LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Blocked),
        TaskOutcome.Cancelled => "cancelled",
        // ActionFailed / GuardrailFailed / InvalidFragment / NeedsHuman are all needs-human terminal.
        _ => LogSiteRenderer.StatusText(Core.Journal.TaskStatus.NeedsHuman),
    };

    /// <summary>
    /// Run a render action, swallowing IO failures: the on-the-fly site is a UX nicety and must never
    /// flip a task's outcome or abort the run. A transient lock/torn-read is retried by the next event.
    /// </summary>
    private static void TryRender(Action render)
    {
        try
        {
            render();
        }
        catch (IOException)
        {
            // best-effort — the next event re-renders
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort — never let a logs-tree permission hiccup abort the run
        }
    }
}
