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
/// <para>The index always carries the in-place live poll (<c>includeRefresh:true</c>) so a SERVED view
/// updates itself as it is rewritten, and stops on its own when the run settles or the poll fails
/// (issue #543 — it replaced a <c>meta refresh</c> that could never stop by itself); the DURABLE
/// final/<c>--export</c> index (no poll, all-static) is written separately by the run's end-of-run
/// path, NOT here.</para>
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
    private readonly IReadOnlyList<WaveNode> _waves;
    private readonly IReadOnlyDictionary<string, TaskNode> _tasksById;
    private readonly Func<string, string?>? _liveUrlForTask;

    // Per-task status word, seeded "pending". Mutated and projected under one lock — events arrive
    // from concurrent M4 workers, and the index render reads the whole map, so the two must not race.
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _statusByTask;

    // Per-task needs-human CLAIM (issue #485), populated only when a task settles with one. Kept beside
    // _statusByTask and under the SAME lock — one snapshot feeds one render, so a cell can never show a
    // status from one event and a claim from another. No new lock, no new mutable observer state beyond
    // this map, and nothing is written during the Spectre Live region (#145/#372).
    private readonly Dictionary<string, string?> _claimByTask = new(StringComparer.Ordinal);

    // The in-flight JIT breakdown (issue #469). No task event fires during it, so the site must render on a
    // CLOCK or the wave page sits frozen for up to 30 minutes. One timer for the phase's duration, disposed
    // the moment it settles; guarded by the same _gate as everything else, and nothing is written to the
    // console at all (this decorator never touches the Spectre live region).
    private readonly Dictionary<string, WaveNode> _wavesByDir;
    private readonly HashSet<string> _phaseWaves = new(StringComparer.Ordinal);
    private Timer? _phaseTimer;
    private WaveBreakdownContext? _phaseContext;
    private DateTimeOffset _phaseSince;

    /// <summary>
    /// During-run re-render cadence for the breakdown's wave page. Deliberately slower than the 2s probe:
    /// <c>RenderIndex()</c> rewrites the plan index AND every wave index on each call, which over a
    /// 30-minute breakdown would be ~720 writes for information that has not changed. Only the AFFECTED
    /// wave's page is rewritten on this clock; the plan index is rewritten once at start and once at finish.
    /// </summary>
    public const int PhaseRenderIntervalSeconds = 5;

    /// <param name="inner">The real observer every event is forwarded to (live or console).</param>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree the site is written into.</param>
    /// <param name="runId">The run id (titles the rendered index).</param>
    /// <param name="tasks">The plan's tasks, in plan order — the rows of the index.</param>
    /// <param name="liveUrlForTask">
    /// Resolver mapping a task id to its live-server URL, or null when no server is up. A RUNNING task
    /// links to this URL (a click tails it) when non-null; null = the running task is plain text.
    /// </param>
    /// <param name="waves">
    /// The plan's waves (SSOT §14), or null/empty for a FLAT plan. When non-empty, each wave's own
    /// <c>&lt;waveDir&gt;/index.html</c> (issue #380) is rewritten alongside the plan index on every event,
    /// and the plan index gains a wave drill-down nav. Empty ⇒ no wave index, plan index unchanged.
    /// </param>
    public OnTheFlyLogSiteObserver(
        IRunObserver inner,
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? liveUrlForTask,
        IReadOnlyList<WaveNode>? waves = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logsRoot = logsRoot;
        _runId = runId;
        _tasks = tasks;
        _waves = waves ?? Array.Empty<WaveNode>();
        _wavesByDir = _waves.ToDictionary(w => w.Dir, StringComparer.Ordinal);
        _tasksById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _liveUrlForTask = liveUrlForTask;
        _statusByTask = tasks.ToDictionary(
            t => t.Id, _ => LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Pending), StringComparer.Ordinal);
    }

    /// <summary>
    /// Write the initial all-pending index at run start (every task pending, plain text), so the "all
    /// tasks" page exists and is browsable the moment the run begins. Best-effort. Delegates to the
    /// static <see cref="WriteInitialIndex(string, string, IReadOnlyList{TaskNode}, Func{string, string?}, IReadOnlyList{WaveNode})"/>.
    /// </summary>
    public void WriteInitialIndex() => WriteInitialIndex(_logsRoot, _runId, _tasks, _liveUrlForTask, _waves);

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
    /// <param name="waves">The plan's waves (issue #380), or null/empty for a FLAT plan — each wave's own
    /// all-pending <c>&lt;waveDir&gt;/index.html</c> is seeded too so a wave page is browsable from run start.</param>
    public static void WriteInitialIndex(
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? liveUrlForTask,
        IReadOnlyList<WaveNode>? waves = null)
    {
        _ = liveUrlForTask; // no task is running at the all-pending start, so no live link is resolved yet
        string pending = LogSiteRenderer.StatusText(Core.Journal.TaskStatus.Pending);
        IReadOnlyList<WaveNode> waveList = waves ?? Array.Empty<WaveNode>();
        TryRender(() => LogSiteRenderer.WriteIndex(
            logsRoot,
            runId,
            tasks,
            statusResolver: _ => pending,
            linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
            includeRefresh: true,
            waves: waveList));

        // Seed each wave's own all-pending index (issue #380) so a wave page exists from run start. A wave
        // with no tasks leads with the PENDING phase panel (issue #469) — before it, an unauthored wave's
        // page was an unexplained empty table from the first frame.
        foreach (WaveNode wave in waveList)
        {
            WaveNode w = wave;
            TryRender(() => LogSiteRenderer.WriteWaveIndex(
                logsRoot,
                runId,
                w,
                statusResolver: _ => pending,
                linkResolver: _ => LogSiteRenderer.IndexLink.Plain,
                includeRefresh: true,
                phase: LogSiteRenderer.BreakdownPanel(logsRoot, w, decisions: null)));
        }
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
        string status = StatusWord(result.Outcome);
        string? claim = NeedsHumanKinds.Parse(result.NeedsHumanKind);
        SetStatus(result.TaskId, status, claim);

        // Write the finished task's static page so the index's link to it (and the terminal's
        // post-mortem link, #141 item 1) resolves to a rendered page, not a 404.
        if (_tasksById.TryGetValue(result.TaskId, out TaskNode? task))
        {
            TryRender(() => LogSiteRenderer.WriteTaskPageIfHasAttempts(_logsRoot, task, status, claim));
        }

        RenderIndex();
    }

    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    public void DecisionRecorded(DecisionEntry entry) => _inner.DecisionRecorded(entry);

    public void ParallelismClampedNoProvider(int requested) => _inner.ParallelismClampedNoProvider(requested);

    // #229 §6.5: forwarded EXPLICITLY. The interface default is an empty body, so a decorator that
    // simply omits this swallows the run-start advisory with no trace at all — and this decorator is
    // in BOTH chains, so the omission would hide it in every mode.
    public void VerifierAdvisoryFound(string taskId, string finding) => _inner.VerifierAdvisoryFound(taskId, finding);

    // #349: forwarded EXPLICITLY, and unchanged — the pair was folded ONCE at the attempt, so re-deriving
    // or reformatting it here would make this a second owner of the rule. The interface default is an empty
    // body, so omitting it swallows the attempt-model disclosure in every mode (the VerifierAdvisoryFound
    // lesson again). `requestedModel` is written only when the two differ, so passing null through AS null
    // is what keeps its presence meaningful.
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        _inner.AttemptModelResolved(task, attempt, model, requestedModel);

    // #524: forwarded EXPLICITLY, verbatim, for exactly the reason above — the interface default is an
    // empty body, so omitting this compiles cleanly and drops the LAUNCH-time route disclosure in every
    // mode. Every argument passes through untouched: `runner` and `model` are both `string`, so a
    // transposition would compile and name the model id where the promptRunners block belongs, and
    // `requestedTier` is written only when a §6.2 climb moved the rung, so nulling it here would erase
    // the climb signal forever. This observer does not ACT on it: the route an attempt took is not a
    // log-site artifact, so it forwards and nothing else.
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        _inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier);

    // Forwarded EXPLICITLY, verbatim — the interface default is an empty body, so omitting this compiles
    // cleanly and drops the per-attempt outcome in every mode (the VerifierAdvisoryFound lesson again).
    // This observer does not ACT on it: an attempt's outcome is not a log-site artifact, so it forwards
    // and nothing else.
    public void AttemptFinished(TaskNode task, int attempt, Core.Journal.AttemptOutcome outcome) =>
        _inner.AttemptFinished(task, attempt, outcome);

    public void OverwatchNoVerdict(string taskId, string reason) => _inner.OverwatchNoVerdict(taskId, reason);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        _inner.PromptPaused(task, reason, backoff, pauseCount);

    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        _inner.OutOfScopeStripped(task, stripped);

    public void WaveStarting(WaveNode wave, int index, int total) =>
        _inner.WaveStarting(wave, index, total);

    public void WaveFinished(WaveNode wave, Core.Journal.WaveStatus status, bool skipped) =>
        _inner.WaveFinished(wave, status, skipped);

    // #513: declared EXPLICITLY, not inherited. A default-method member a decorator does not declare is
    // swallowed here and never reaches the renderer behind it.
    public void WaveGateFinished(
        WaveNode wave, bool isEntryGate, IReadOnlyList<Core.Journal.PlanPreflightCheck> checks) =>
        _inner.WaveGateFinished(wave, isEntryGate, checks);

    // --- the JIT breakdown phase (issue #469) -----------------------------------------------

    // Forwarded EXPLICITLY, and then ACTED on. The interface default is an empty body, so omitting these
    // would swallow the phase in every mode — this decorator is in both chains (the VerifierAdvisoryFound
    // lesson). Acting on it is what turns the wave page from a permanent dead end into the post-mortem.
    public void WaveBreakdownStarting(WaveBreakdownContext context)
    {
        _inner.WaveBreakdownStarting(context);

        lock (_gate)
        {
            _phaseContext = context;
            _phaseSince = DateTimeOffset.UtcNow;
            _phaseWaves.Add(context.WaveDir);
            _phaseTimer?.Dispose();
            var interval = TimeSpan.FromSeconds(PhaseRenderIntervalSeconds);
            _phaseTimer = new Timer(_ => RenderPhasePage(), null, interval, interval);
        }

        RenderIndex(); // once at start, so the plan index's wave nav is not stale for half an hour
        RenderPhasePage();
    }

    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        WaveNode? authoredWave)
    {
        Timer? stopped;
        lock (_gate)
        {
            stopped = _phaseTimer;
            _phaseTimer = null;
            _phaseContext = null;
        }

        stopped?.Dispose();

        // Forward AFTER stopping the clock, so no re-render races the settled page.
        _inner.WaveBreakdownFinished(context, elapsed, authoredTaskCount, failureKind, authoredWave);

        BreakdownProgress.Snapshot snapshot = BreakdownProgress.Probe(
            context.TasksDirectory, context.StreamLogPath, context.IntentManifestPath, DateTimeOffset.UtcNow);
        WritePhasePage(context.WaveDir, LogSiteRenderer.SettledBreakdownPanel(
            _logsRoot, context.WaveDir, failureKind, elapsed,
            BreakdownProgress.TerminalDetail(failureKind, snapshot)));

        RenderIndex();
    }

    /// <summary>
    /// Rewrite ONLY the breaking-down wave's page, with the running phase panel. The during-run page already
    /// carries a 2s <c>meta refresh</c>, so an open browser animates for free.
    /// </summary>
    private void RenderPhasePage()
    {
        WaveBreakdownContext? context;
        DateTimeOffset since;
        lock (_gate)
        {
            context = _phaseContext;
            since = _phaseSince;
        }

        if (context is null)
        {
            return;
        }

        // Probe outside the lock — filesystem latency must not block the event path.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BreakdownProgress.Snapshot snapshot = BreakdownProgress.Probe(
            context.TasksDirectory, context.StreamLogPath, context.IntentManifestPath, now);
        LogSiteRenderer.PhasePanel panel = LogSiteRenderer.RunningBreakdownPanel(
            _logsRoot, context.WaveDir, now - since, context.Ceiling, BreakdownProgress.DetailMarkup(snapshot));

        lock (_gate)
        {
            if (!ReferenceEquals(_phaseContext, context))
            {
                return; // settled while we were probing
            }
        }

        WritePhasePage(context.WaveDir, panel);
    }

    /// <summary>
    /// Write ONE wave's page carrying <paramref name="panel"/>, from the current status snapshot. The plan
    /// index and every other wave page are deliberately untouched: nothing about them changes while a
    /// breakdown runs, and rewriting them on this clock would cost ~720 writes over a full ceiling.
    /// </summary>
    private void WritePhasePage(string waveDir, LogSiteRenderer.PhasePanel panel)
    {
        if (!_wavesByDir.TryGetValue(waveDir, out WaveNode? wave))
        {
            return;
        }

        lock (_gate)
        {
            var statuses = new Dictionary<string, string>(_statusByTask, StringComparer.Ordinal);
            var claims = new Dictionary<string, string?>(_claimByTask, StringComparer.Ordinal);
            string StatusOf(string id) => statuses.TryGetValue(id, out string? s) ? s : "unknown";
            string? ClaimOf(string id) => claims.TryGetValue(id, out string? c) ? c : null;

            TryRender(() => LogSiteRenderer.WriteWaveIndex(
                _logsRoot,
                _runId,
                wave,
                statusResolver: StatusOf,
                linkResolver: id => ResolveLink(id, statuses),
                includeRefresh: true,
                halt: null,
                claimResolver: ClaimOf,
                phase: panel));
        }
    }

    // --- site projection --------------------------------------------------------------------

    private void SetStatus(string taskId, string status, string? claim = null)
    {
        lock (_gate)
        {
            _statusByTask[taskId] = status;
            _claimByTask[taskId] = claim;
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
            var claims = new Dictionary<string, string?>(_claimByTask, StringComparer.Ordinal);
            string StatusOf(string id) => statuses.TryGetValue(id, out string? s) ? s : "unknown";
            string? ClaimOf(string id) => claims.TryGetValue(id, out string? c) ? c : null;
            LogSiteRenderer.IndexLink LinkOf(string id) => ResolveLink(id, statuses);

            TryRender(() => LogSiteRenderer.WriteIndex(
                _logsRoot,
                _runId,
                _tasks,
                statusResolver: StatusOf,
                linkResolver: LinkOf,
                includeRefresh: true,
                waves: _waves,
                claimResolver: ClaimOf));

            // Rewrite each wave's own index too (issue #380), from the same status snapshot, so a
            // waved run's per-wave drill-down refreshes as the wave progresses. A wave whose breakdown
            // phase has begun is OWNED by WritePhasePage — this render has no idea how the session is
            // going, so re-asserting "not yet authored" over it would be the wrong answer stated
            // confidently.
            foreach (WaveNode wave in _waves)
            {
                WaveNode w = wave;
                if (_phaseWaves.Contains(w.Dir))
                {
                    continue;
                }

                TryRender(() => LogSiteRenderer.WriteWaveIndex(
                    _logsRoot,
                    _runId,
                    w,
                    statusResolver: StatusOf,
                    linkResolver: LinkOf,
                    includeRefresh: true,
                    halt: null,
                    claimResolver: ClaimOf,
                    phase: LogSiteRenderer.BreakdownPanel(_logsRoot, w, decisions: null)));
            }
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
