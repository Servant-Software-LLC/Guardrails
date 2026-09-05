using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Guardrails.Cli.Ui;

/// <summary>
/// Spectre live-table progress: one row per task, updated in place as workers report
/// events. Used only when the terminal is interactive and <c>--no-ui</c> is absent;
/// otherwise the plain <see cref="ConsoleRunObserver"/> runs. All mutation is gated —
/// M4 workers call in concurrently.
///
/// <para><b>Nothing is ever written OUT OF BAND (design 37 §4.1, issue #372).</b> Every line this observer
/// has to say goes into the bounded <see cref="LiveNarrative"/> pane and is rendered as part of the Live
/// region's target, which becomes <c>Rows([…narrative…, _table])</c> the moment the pane is non-empty and
/// stays the bare <see cref="_table"/> while it is empty. There must be ZERO <c>AnsiConsole.</c> references
/// left in this file: a raw write from inside the Live region desynchronises Spectre's repaint bookkeeping
/// and stamps the table through the line it just wrote. That invariant is asserted, not assumed — see the
/// <c>LiveNarrativeCompositeTests</c> frame-integrity test, which is why <see cref="IAnsiConsole"/> is
/// injectable at all (§7.4).</para>
/// </summary>
public sealed class LiveRunObserver : IRunObserver, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Table _table;

    // The console the Live region runs on (design 37 §7.4). Injected — defaulting to AnsiConsole.Console —
    // so a TestConsole can drive a whole simulated run and the rendered frames can be ASSERTED. Without it
    // every claim about what this surface emits is a screenshot in a PR description.
    private readonly IAnsiConsole _console;

    // The bounded narrative pane (design 37 §4.2). Empty for the dominant plan shape (flat, no advisories),
    // in which case the Live target stays the bare table and the run renders byte-identically to before.
    // Read and written under _gate only.
    private IReadOnlyList<NarrativeEntry> _narrative = [];
    private int _elided;

    // Row index keyed by EITHER a task id or a wave-phase key "<waveDir>/(<phase>)" (issue #469). One map,
    // because a phase row is updated through exactly the same UpdateCell path as a task row — the parenthesised
    // segment cannot collide with an SSOT §14.2 wave-qualified task id.
    private readonly Dictionary<string, int> _rowByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningState> _running = new(StringComparer.Ordinal);

    // Task lookup for the Model column's pending-row seed (RebuildRows) — a TaskLiveRow carries only the
    // id, not the TaskNode, so this is how a pending cell reads task.Action.Tier / task.Action.Kind.
    // Replaced wholesale by the #404 mid-run splice; read and written under _gate.
    private Dictionary<string, TaskNode> _taskById;

    // The LAUNCH event's (runner, tier, climbed) per task (design 29 §4.2/§4.3), written by
    // AttemptRouteResolved and read back by AttemptModelResolved — the post-action event has only model
    // ids, never the promptRunners block name, so it cannot render the cell without this. A retry simply
    // overwrites the entry.
    private readonly Dictionary<string, (string Runner, string? Tier, bool Climbed)> _routeByTask = new(StringComparer.Ordinal);

    // In-flight wave phases (issue #469): the JIT breakdown, and — per design 23 §9 — the shape #476's wave
    // gates will reuse. Driven by the SAME 1 Hz ticker as _running; no new timer and no new lock.
    private readonly Dictionary<string, PhaseState> _phases = new(StringComparer.Ordinal);

    // Waves that have RUN a JIT breakdown this session — distinct from _phases, which is only the IN-FLIGHT
    // ones. #404's splice gives such a wave real tasks, and without this the row plan would drop its phase
    // row at the exact moment the breakdown finally had an outcome to report (design 37 §5.2 B2).
    private readonly HashSet<string> _breakdownWaves = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _liveLoop;
    private readonly Timer _ticker;
    private readonly Func<string, string?>? _logUrlForTask;
    private readonly string? _planDirectory;
    private readonly string? _runId;
    // #379 collapse-completed-waves state. For a FLAT plan (or --all-tasks) these stay inert and the
    // table is the pre-#379 flat one-row-per-task list; for a WAVED plan the row set is (re)planned via
    // LiveTableRows as each wave settles, collapsing completed waves to a single summary row.
    //
    // MUTABLE since #404: the run may LOAD a wave as an empty JIT stub and only learn its tasks half an
    // hour later, at WaveBreakdownFinished. Both are replaced wholesale by SpliceWave and are read and
    // written only under _gate — the same discipline every other piece of table state here follows.
    private IReadOnlyList<TaskNode> _tasks;
    private IReadOnlyList<WaveNode> _waves;
    private readonly bool _showAllTasks;
    private readonly HashSet<string> _completedWaves = new(StringComparer.Ordinal);
    private LiveDisplayContext? _context;

    // Tick re-entrancy guard (issue #469). The phase probe runs OUTSIDE _gate, so two ticks could otherwise
    // overlap in it; this keeps the per-phase probe/notice fields single-writer and makes the 25-minute
    // pre-announcement fire exactly once. Not a lock — a skipped repaint costs nothing.
    private int _ticking;

    /// <summary>A task currently running: when it started and the status word to prefix the clock.</summary>
    private readonly record struct RunningState(DateTimeOffset Since, string Prefix);

    /// <summary>
    /// One in-flight wave phase. <see cref="Snapshot"/> and <see cref="LastProbe"/> are written by the
    /// ticker thread only, OUTSIDE <see cref="_gate"/>, so filesystem latency never blocks the table; the
    /// re-entrancy guard on <see cref="Tick"/> keeps that single-writer property true even if a probe
    /// overruns a tick.
    /// </summary>
    private sealed class PhaseState
    {
        public required string Key { get; init; }
        public required WaveBreakdownContext Context { get; init; }
        public required DateTimeOffset Since { get; init; }
        public BreakdownProgress.Snapshot Snapshot { get; set; }
        public DateTimeOffset LastProbe { get; set; }
        public bool CeilingNoticeFired { get; set; }
    }

    /// <param name="tasks">The tasks to render, one row each.</param>
    /// <param name="logUrlForTask">
    /// Optional resolver mapping a task id to its live-log URL. When supplied, a running task's
    /// Detail cell renders a clickable <c>view log</c> link (OSC 8 hyperlink in capable terminals,
    /// plain text elsewhere). Null = no links (no log server).
    /// </param>
    /// <param name="planDirectory">
    /// Optional plan folder. When supplied, a FINISHED task's Detail cell carries a durable
    /// <c>logs</c> link (a <c>file://</c> hyperlink to its on-disk log directory) for post-mortem —
    /// available on success, needs-human, and failure alike, and still valid after the run ends and
    /// the live log server is gone. Null = no post-mortem links.
    /// </param>
    /// <param name="runId">
    /// The run id selecting the <c>logs/&lt;runId&gt;/</c> tree the post-mortem link points into
    /// (SSOT §8). Required alongside <paramref name="planDirectory"/> for the post-mortem link;
    /// null suppresses the link the same way a null plan dir does.
    /// </param>
    /// <param name="waves">
    /// The plan's waves in strict order, or null/empty for a FLAT plan (issue #379). When supplied (and
    /// <paramref name="showAllTasks"/> is false) a COMPLETED wave's per-task rows collapse to a single
    /// summary line as the wave settles — the active/pending waves keep full rows. A flat plan is
    /// unaffected: its table is byte-identical to the pre-#379 one-row-per-task list.
    /// </param>
    /// <param name="showAllTasks">
    /// The <c>--all-tasks</c> opt-out (issue #379): when true, NO wave ever collapses — every task keeps
    /// its own row, exactly as a flat plan renders. Ignored for a flat plan (nothing to collapse).
    /// </param>
    /// <param name="console">
    /// The console the Live region runs on (design 37 §7.4). Defaults to <see cref="AnsiConsole.Console"/>,
    /// which is what every production caller wants; a test passes a <c>TestConsole</c> so the rendered frames
    /// become assertable — the #372 invariant (no line ever carries both narrative text and a table border
    /// glyph) is a gate rather than an eyeball check because of this parameter.
    /// </param>
    public LiveRunObserver(
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? logUrlForTask = null,
        string? planDirectory = null,
        string? runId = null,
        IReadOnlyList<WaveNode>? waves = null,
        bool showAllTasks = false,
        IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
        _logUrlForTask = logUrlForTask;
        _planDirectory = planDirectory;
        _runId = runId;
        _tasks = tasks;
        _taskById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _waves = waves ?? [];
        _showAllTasks = showAllTasks;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");
        // Appended LAST (design 29 §4.1): Update/Tick/the wave-phase branch all write hard-coded cell
        // indices 1 and 2, so inserting a column ahead of those would silently re-target every one of
        // them. Width(8) is measured, not assumed — an auto-sized column lets one long block name steal
        // width from every row for the whole run; pinned at 8 it wraps inside its own cell instead.
        _table.AddColumn(new TableColumn("Model").Width(8));

        RebuildRows();

        _liveLoop = _console.Live(_table).StartAsync(async ctx =>
        {
            lock (_gate)
            {
                _context = ctx;
            }

            ctx.Refresh();
            await _done.Task.ConfigureAwait(false);
            ctx.Refresh();
        });

        // Tick once a second so a running task's elapsed clock advances even when no event fires —
        // the "is it alive?" signal for long actions, and a duration cue for unattended runs.
        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void TaskStarting(TaskNode task)
    {
        lock (_gate)
        {
            // #379: never start a clock for a task whose wave has collapsed (it has no row). A task in a
            // completed wave is only ever replayed as finished on resume, never started — but guard anyway
            // so a phantom entry can't accumulate in _running and spin the ticker forever.
            if (!_rowByKey.ContainsKey(task.Id))
            {
                return;
            }

            _running[task.Id] = new RunningState(DateTimeOffset.UtcNow, "running");
        }

        Update(task.Id, "[yellow]running[/]", LogLinkMarkup(task.Id) ?? string.Empty);
    }

    public void AttemptStarting(TaskNode task, int attempt, int budget)
    {
        if (attempt <= 1)
        {
            return;
        }

        lock (_gate)
        {
            if (_running.TryGetValue(task.Id, out RunningState state))
            {
                _running[task.Id] = state with { Prefix = $"retry {attempt}/{budget}" };
            }
        }

        // STATUS only. The Detail cell is left exactly as the just-fired AttemptFinished wrote it — design
        // 37 §5.1's retry row is `retry 2/3 1:12 │ attempt 1 GuardrailFailed · view log`, and re-asserting
        // the vaguer "previous attempt failed" over it here would overwrite the precise outcome word with a
        // paraphrase of itself. AttemptFinished always precedes the next AttemptStarting (the executor's
        // retry loop journals the attempt before looping), and only a NON-succeeded attempt is retried, so
        // the cell is always populated by the time this fires.
        Update(task.Id, $"[yellow]retry {attempt}/{budget}[/]", null);
    }

    public void AttemptFinished(TaskNode task, Core.Journal.AttemptRecord record)
    {
        // Design 37 §4.4 #1: the TABLE CELL, never a line. This emitter alone contributed ~30 of the ~60
        // out-of-band lines a 30-task plan produced, and what it carried was the outcome WORD — which on
        // success duplicates the `succeeded` status arriving milliseconds later from TaskFinished, and on
        // failure belongs in the Detail cell the operator is already looking at. The per-attempt history it
        // used to narrate is in the journal, on the task's log page, and in observer.jsonl.
        if (AttemptDetailCell(record.Outcome, record.Attempt, LogLinkMarkup(task.Id)) is { } detail)
        {
            Update(task.Id, null, detail);
        }
    }

    /// <summary>
    /// A finished attempt's Detail cell (design 37 §4.4 #1) — null on <see cref="Core.Journal.AttemptOutcome.Succeeded"/>,
    /// because the row is about to carry the task's own settled status and an "attempt N Succeeded" cell would
    /// be overwritten by it within milliseconds; otherwise the attempt number and the outcome, with the live-log
    /// link appended when one is wired.
    ///
    /// <para><c>GuardrailFailed</c> / <c>ActionFailed</c> / <c>Timeout</c> are
    /// <see cref="Core.Journal.AttemptOutcome"/>'s OWN words — this introduces no new vocabulary for an
    /// operator to learn, and no mapping table that could drift from the enum.</para>
    ///
    /// <para>Public (not private) for the same reason <see cref="StatusMarkup"/> and <see cref="ModelCell"/>
    /// are: the Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure function IS the test seam.</para>
    /// </summary>
    public static string? AttemptDetailCell(
        Core.Journal.AttemptOutcome outcome, int attempt, string? logLinkMarkup)
    {
        if (outcome == Core.Journal.AttemptOutcome.Succeeded)
        {
            return null;
        }

        string cell = $"attempt {attempt} {Markup.Escape(outcome.ToString())}";
        return logLinkMarkup is null ? cell : $"{cell} · {logLinkMarkup}";
    }

    // --- the narrative pane (design 37 §4) ---------------------------------------------------

    /// <summary>
    /// Append one NON-coalescing entry — a wave transition, a decision, a one-shot notice — and repaint.
    /// Each is a distinct event, so none of them ever folds into a count (§4.5). Caller holds <see cref="_gate"/>.
    /// </summary>
    private void AppendNarrative(string markup)
    {
        IReadOnlyList<NarrativeEntry> next = LiveNarrative.Append(
            _narrative, new NarrativeEntry(markup, null, 1), LiveNarrative.BudgetFor(_console.Profile.Width));
        _elided += _narrative.Count + 1 - next.Count;
        _narrative = next;
        RefreshTarget();
    }

    /// <summary>
    /// Append one COALESCING entry under <paramref name="coalesceKey"/> (§4.5). <paramref name="format"/> is
    /// handed the entry's new occurrence count and renders the whole line — the singular case must stay
    /// byte-identical to what this surface printed before design 37, and the counted case reads differently
    /// per emitter, so the wording lives with the emitter and only the BOOKKEEPING lives here.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private void AppendCoalescingNarrative(string coalesceKey, Func<int, string> format)
    {
        int at = LiveNarrative.CoalesceIndexOf(_narrative, coalesceKey);
        int count = at < 0 ? 1 : _narrative[at].Count + 1;
        IReadOnlyList<NarrativeEntry> next = LiveNarrative.Append(
            _narrative,
            new NarrativeEntry(format(count), coalesceKey, count),
            LiveNarrative.BudgetFor(_console.Profile.Width));

        // A fold replaces in place, so the buffer only GROWS for a genuinely new entry; anything the budget
        // then dropped off the front is what the elision line counts.
        _elided += _narrative.Count + (at < 0 ? 1 : 0) - next.Count;
        _narrative = next;
        RefreshTarget();
    }

    /// <summary>
    /// Re-point the Live region at the current composite and repaint. Called ONLY when the narrative list
    /// changes (~10–20 times in a whole run under the §4.4 routing) — a cell mutation needs neither, because
    /// the <see cref="Table"/> instance inside the composite is the same object Spectre re-renders in place.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private void RefreshTarget()
    {
        if (_context is null)
        {
            return;
        }

        _context.UpdateTarget(ComposeTarget());
        _context.Refresh();
    }

    /// <summary>
    /// The Live region's target (§4.1): the bare table while the narrative is empty — byte-identical to what
    /// a flat, advisory-free run rendered before design 37, which is what keeps <c>LiveTableRows.Plan</c>'s
    /// byte-identity assertions meaningful — and <c>Rows([…narrative…, table])</c> once it is not.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private IRenderable ComposeTarget()
    {
        if (_narrative.Count == 0)
        {
            return _table;
        }

        IReadOnlyList<string> lines = LiveNarrative.Render(_narrative, _elided, _planDirectory, _runId);
        var items = new List<IRenderable>(lines.Count + 1);
        foreach (string line in lines)
        {
            items.Add(new Markup(line));
        }

        items.Add(_table);
        return new Rows(items);
    }

    /// <summary>
    /// Repaint the Status cell of every running task with its live elapsed clock, and — issue #469 — of
    /// every in-flight wave phase with its clock plus the two observed liveness fragments. Cell writes run
    /// under the same gate as event updates, so the table mutates from one place at a time.
    ///
    /// <para><b>The disk probe runs OUTSIDE the gate.</b> A phase probe stats a directory and a file; doing
    /// that while holding <see cref="_gate"/> would let filesystem latency block every worker's event. So
    /// the tick snapshots the phase list under the lock, probes without it, then re-takes it to write. A
    /// re-entrancy guard keeps that single-writer discipline true if a probe ever overruns a tick — and it
    /// is what makes the 25-minute notice fire exactly once.</para>
    /// </summary>
    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
        {
            return; // a previous tick is still probing; skipping one repaint is free
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            PhaseState[] phases;
            lock (_gate)
            {
                if (_context is null || (_running.Count == 0 && _phases.Count == 0))
                {
                    return;
                }

                phases = _phases.Count == 0 ? [] : [.. _phases.Values];
            }

            foreach (PhaseState phase in phases)
            {
                if (now - phase.LastProbe < TimeSpan.FromSeconds(BreakdownProgress.ProbeIntervalSeconds))
                {
                    continue;
                }

                phase.Snapshot = BreakdownProgress.Probe(
                    phase.Context.TasksDirectory, phase.Context.StreamLogPath,
                    phase.Context.IntentManifestPath, now);
                phase.LastProbe = now;
            }

            lock (_gate)
            {
                if (_context is null)
                {
                    return;
                }

                foreach (KeyValuePair<string, RunningState> entry in _running)
                {
                    // #379: skip a running task whose wave collapsed out from under it (defensive — a wave
                    // only collapses once all its tasks are settled and removed from _running).
                    if (!_rowByKey.TryGetValue(entry.Key, out int row))
                    {
                        continue;
                    }

                    string elapsed = BreakdownProgress.FormatElapsed(now - entry.Value.Since);
                    _table.UpdateCell(row, 1, new Markup($"[yellow]{entry.Value.Prefix} {elapsed}[/]"));
                }

                foreach (PhaseState phase in phases)
                {
                    TimeSpan elapsed = now - phase.Since;
                    if (_rowByKey.TryGetValue(phase.Key, out int row))
                    {
                        // Yellow, exactly like running/retry: the colour never claims a cause, because the
                        // harness cannot distinguish "waiting" from "dead" and this row does not pretend to.
                        _table.UpdateCell(row, 1, new Markup(
                            $"[yellow]{Markup.Escape(BreakdownProgress.StatusMarkup(elapsed, phase.Context.Ceiling, BreakdownProgress.AuthoringPhase))}[/]"));
                        _table.UpdateCell(row, 2, new Markup(RunningPhaseDetail(phase)));
                    }

                    MaybeAnnounceCeiling(phase, elapsed);
                }

                _context.Refresh();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// The one-shot pre-announcement, five minutes before the kill — the moment it becomes actionable, and
    /// never repeated (design 23 §5.1). Two narrative entries (design 37 §4.4 #9–#10), which is why this is
    /// an instance method: nothing writes out of band any more. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void MaybeAnnounceCeiling(PhaseState phase, TimeSpan elapsed)
    {
        if (phase.CeilingNoticeFired
            || elapsed < TimeSpan.FromMinutes(BreakdownProgress.CeilingNoticeMinutes)
            || elapsed >= phase.Context.Ceiling)
        {
            return;
        }

        phase.CeilingNoticeFired = true;
        string wave = Markup.Escape(phase.Context.WaveDir);
        string spent = BreakdownProgress.FormatClock(elapsed);
        string ceiling = BreakdownProgress.FormatClock(phase.Context.Ceiling);
        AppendNarrative(
            $"[yellow]{wave}: {spent} of a {ceiling} ceiling — the breakdown will be CUT OFF at {ceiling}.[/]");
        AppendNarrative(
            "  [grey]Let it run. Ctrl+C here ends the authoring session: the harness leaves the wave "
            + "loadable, so the work in flight is lost.[/]");
    }

    /// <summary>
    /// The RUNNING phase row's Detail cell: the shared fragments plus this observer's live-log link, in the
    /// order count · stream · link — so an 80-column wrap costs the link, never a decision-critical fact.
    /// </summary>
    private string RunningPhaseDetail(PhaseState phase)
    {
        string detail = Markup.Escape(BreakdownProgress.DetailMarkup(phase.Snapshot));
        if (WavePageLinkMarkup(phase.Context.WaveDir, "view log") is { } link)
        {
            detail = detail.Length == 0 ? link : $"{detail} · {link}";
        }

        return detail;
    }

    /// <summary>
    /// A <c>file://</c> OSC 8 link to the WAVE's static log page (<c>logs/&lt;runId&gt;/&lt;waveDir&gt;/index.html</c>)
    /// — the page the phase panel (design 23 §5.3) writes, so a click lands on the evidence list rather than
    /// an OS file listing. Null when no plan dir / run id, exactly like the per-task post-mortem link.
    /// </summary>
    private string? WavePageLinkMarkup(string waveDir, string text)
    {
        if (_planDirectory is null || _runId is null)
        {
            return null;
        }

        string page = Path.GetFullPath(Path.Combine(_planDirectory, "logs", _runId, waveDir, "index.html"));
        return $"[link={new Uri(page).AbsoluteUri}]{text}[/]";
    }

    /// <summary>
    /// Spectre markup for a clickable "view log" link, or null when no log server is wired.
    /// <c>[link=…]</c> emits an OSC 8 hyperlink in capable terminals (Windows Terminal, VS Code,
    /// iTerm2) and degrades to plain underlined text elsewhere.
    /// </summary>
    private string? LogLinkMarkup(string taskId) =>
        _logUrlForTask?.Invoke(taskId) is { } url ? $"[link={url}]view log[/]" : null;

    /// <summary>
    /// Spectre markup for a durable <c>logs</c> link to a task's STATIC log page
    /// (<c>logs/&lt;runId&gt;/&lt;id&gt;/index.html</c>) — the self-contained inlined view of every attempt's
    /// output, guardrail logs and the task's Source section (#141 item 1). A <c>file://</c> OSC 8
    /// hyperlink that survives the run: the on-the-fly site writer (issue #141 item 2) writes this page
    /// when the task finishes, so a click opens a rendered HTML page rather than a raw file listing in
    /// the OS file browser. Null when no plan dir / run id.
    /// </summary>
    private string? PostMortemLinkMarkup(string taskId)
    {
        if (_planDirectory is null || _runId is null)
        {
            return null;
        }

        string page = PostMortemPagePath(_planDirectory, _runId, taskId);
        return $"[link={new Uri(page).AbsoluteUri}]logs[/]";
    }

    /// <summary>
    /// The absolute path the finished-task <c>logs</c> link targets: the task's STATIC log page
    /// <c>logs/&lt;runId&gt;/&lt;taskId&gt;/index.html</c> (issue #141 item 1), NOT the log directory. The
    /// on-the-fly site writer (#141 item 2) writes this page on finish, so the link opens a rendered
    /// HTML page rather than a raw OS file-browser listing. Public (not private) because the Cli
    /// assembly ships no InternalsVisibleTo — same test-seam rationale as <c>RunCommand.Hyperlink</c>.
    /// </summary>
    public static string PostMortemPagePath(string planDirectory, string runId, string taskId) =>
        Path.GetFullPath(Path.Combine(planDirectory, "logs", runId, taskId, "index.html"));

    public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
        Update(task.Id, null, result.Passed
            ? $"[green]{Markup.Escape(result.Name)} ✓[/]"
            : $"[red]{Markup.Escape(result.Name)} ✗ {Markup.Escape(result.Reason ?? "")}[/]");

    public void TaskFinished(TaskResult result)
    {
        lock (_gate)
        {
            _running.Remove(result.TaskId); // stop the clock — outcome + summary are terminal
        }

        string detail = Markup.Escape(result.Summary);
        if (PostMortemLinkMarkup(result.TaskId) is { } link)
        {
            detail += $" · {link}";
        }

        // #485: the kind qualifies the STATUS cell only. The Detail cell is left untouched in all three
        // cases — it already carries the question, and it is the most elastic cell, so a prefix there
        // would push the question off-screen on a narrow terminal.
        Update(result.TaskId, StatusMarkup(result.Outcome, result.NeedsHumanKind), detail);
    }

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount)
    {
        // Show the task as PAUSED (blue, distinct from yellow "running"/"retry" and red failure) and
        // freeze its clock prefix so an operator reads "healthy task waiting out a rate limit", not a
        // failing one (issue #115). The retry budget is untouched.
        lock (_gate)
        {
            if (_running.TryGetValue(task.Id, out RunningState state))
            {
                _running[task.Id] = state with { Prefix = $"paused {(int)backoff.TotalSeconds}s" };
            }
        }

        Update(task.Id,
            $"[blue]paused {(int)backoff.TotalSeconds}s[/]",
            $"[blue]transient — {Markup.Escape(reason)} (pause {pauseCount}; no retry burn)[/]");
    }

    public void WaveStarting(WaveNode wave, int index, int total)
    {
        lock (_gate)
        {
            // Design 37 §4.4 #5: the table's own segmentation, moved out of ROWS by #145 and staying out —
            // now as a narrative entry INSIDE the Live region's target rather than a raw write beside it.
            AppendNarrative(
                $"[bold]Wave {index}/{total}:[/] {Markup.Escape(wave.Dir)} — {wave.Tasks.Count} task(s)");

            // Regenerate the wave-scoped diagram so it reflects the now-authored tasks before
            // execution begins (issue #359). Runs silently inside the live region to avoid ANSI
            // output interleaving; failures are swallowed — never change the run outcome.
            GraphCommand.RenderWaveScoped(wave.Directory, TextWriter.Null);
        }
    }

    public void WaveFinished(WaveNode wave, Core.Journal.WaveStatus status, bool skipped)
    {
        lock (_gate)
        {
            string verb = skipped
                ? "[green]already complete — skipped (resume)[/]"
                : status == Core.Journal.WaveStatus.Completed
                    ? "[green]completed[/]"
                    : $"[red]halted ({Markup.Escape(status.ToString().ToLowerInvariant())})[/]";
            AppendNarrative($"[bold]Wave {Markup.Escape(wave.Dir)}:[/] {verb}"); // §4.4 #6

            // #379: collapse a COMPLETED wave's per-task rows to a single summary line — its rows are pure
            // noise once settled (logs stay on the static site + live diagram). A HALTED wave keeps its full
            // rows so the failing task stays visible. Guarded behind "plan has waves" + not --all-tasks, so a
            // flat plan (and the opt-out) never rebuilds and renders byte-identically to before.
            if (!_showAllTasks
                && _waves.Count > 0
                && status == Core.Journal.WaveStatus.Completed
                && _completedWaves.Add(wave.Dir))
            {
                RebuildRows();
                _context?.Refresh();
            }
        }
    }

    public void WaveBreakdownStarting(WaveBreakdownContext context)
    {
        var phase = new PhaseState
        {
            Key = WavePhaseLiveRow.KeyFor(context.WaveDir, WavePhaseLiveRow.BreakdownPhase),
            Context = context,
            Since = DateTimeOffset.UtcNow
        };

        lock (_gate)
        {
            _phases[phase.Key] = phase;
            _breakdownWaves.Add(context.WaveDir);

            // The TWO one-shot entries (design 37 §4.4 #7–#8). Line 2 is the breakdown log dir — the fallback
            // when OSC 8 links are unsupported — and both are rare (once per JIT wave), so both are kept.
            // Everything that then repeats per second goes through UpdateCell, never here.
            AppendNarrative(
                $"[bold]Wave {context.Index}/{context.Total}:[/] {Markup.Escape(context.WaveDir)} — "
                + $"authoring tasks (JIT breakdown). Ceiling {BreakdownProgress.FormatClock(context.Ceiling)}.");
            AppendNarrative($"  [grey]Breakdown log: {Markup.Escape(context.BreakdownLogDir)}[/]");

            if (_rowByKey.TryGetValue(phase.Key, out int row))
            {
                _table.UpdateCell(row, 1, new Markup(
                    $"[yellow]{Markup.Escape(BreakdownProgress.StatusMarkup(TimeSpan.Zero, context.Ceiling, BreakdownProgress.AuthoringPhase))}[/]"));
                _table.UpdateCell(row, 2, new Markup(RunningPhaseDetail(phase)));
                _context?.Refresh();
            }
        }
    }

    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        WaveNode? authoredWave)
    {
        string key = WavePhaseLiveRow.KeyFor(context.WaveDir, WavePhaseLiveRow.BreakdownPhase);

        // Probe once more OUTSIDE the gate, so the settled row reports what is really on disk rather than
        // the last 2-second sample.
        BreakdownProgress.Snapshot snapshot = BreakdownProgress.Probe(
            context.TasksDirectory, context.StreamLogPath, context.IntentManifestPath, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _phases.Remove(key); // stop the clock — the outcome is terminal
            if (_rowByKey.TryGetValue(key, out int row))
            {
                string colour = failureKind is null ? "green" : "red";
                string status = BreakdownProgress.TerminalStatus(BreakdownProgress.TerminalWord(failureKind), elapsed);
                string detail = Markup.Escape(BreakdownProgress.TerminalDetail(failureKind, snapshot));
                if (WavePageLinkMarkup(context.WaveDir, "logs") is { } link)
                {
                    detail = $"{detail} · {link}";
                }

                _table.UpdateCell(row, 1, new Markup($"[{colour}]{Markup.Escape(status)}[/]"));
                _table.UpdateCell(row, 2, new Markup(detail));
            }

            // #404: the wave the run will PROCEED with, spliced into the row plan so its eleven tasks stop
            // running for forty minutes behind a single settled row. The settled phase row STAYS above them
            // — it is the wave's authoring provenance (time spent, folders written, a link into the
            // breakdown evidence) and the only place those numbers appear live — and RebuildRows carries
            // its just-written cells across.
            //
            // authoredWave is non-null ONLY where the run will proceed: Scheduler passes
            // `proceeding ? authoredWave : null`, so its non-null-ness IS the "this wave is going to run"
            // signal, which is why this is the splice trigger rather than WaveStarting. Every escalate/halt
            // path passes null, takes none of this, and leaves the table byte-identical to before.
            if (authoredWave is not null)
            {
                (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) =
                    SpliceWave(_tasks, _waves, authoredWave);
                _tasks = tasks;
                _waves = waves;
                _taskById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
                RebuildRows();
            }

            _context?.Refresh();
        }
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        lock (_gate)
        {
            // §4.4 #11: once per run, kept.
            AppendNarrative(
                "[bold yellow]WARNING:[/] plan manifests changed since the last run " +
                $"(previous hash {Markup.Escape(previousPlanHash)}). Resuming anyway; use --fresh for a clean slate.");
        }
    }

    public void DecisionRecorded(DecisionEntry entry)
    {
        lock (_gate)
        {
            // An autonomy-policy decision (SSOT §2.1/§7) — the headline is pre-rendered, subject names the
            // units. Design 37 §4.4 #12 adds the `decision:<boundary>` prefix: a colourless terminal renders
            // the green headline alone as an unmarked sentence among wave lines, and ConsoleRunObserver
            // already prints this token, so adopting it closes a live/plain wording divergence AND makes the
            // line legible under NO_COLOR. No brackets around the token — this string is Spectre markup, and
            // `[decision:wave]` would be parsed as a style tag (§5.2's rendered block shows the bare form).
            string subject = string.IsNullOrEmpty(entry.Subject) ? "" : $": {Markup.Escape(entry.Subject)}";
            AppendNarrative(
                $"[grey]decision:{Markup.Escape(entry.Boundary)}[/]  [green]{Markup.Escape(entry.Headline)}[/]{subject}");
        }
    }

    public void VerifierAdvisoryFound(string taskId, string finding)
    {
        lock (_gate)
        {
            // The DoR §6.5 run-start advisory (#229). COALESCED (design 37 §4.4 #3): it fires once per
            // affected TASK and is unbounded in the worst case, so a 24-task advisory burst at run start
            // would evict the whole pane in one second. Scheduler.cs already applies exactly this discipline
            // one level up — "repeating the same sentence three times before the run starts is how an
            // operator learns to skip the block entirely" — and this is that rule one level further out.
            AppendCoalescingNarrative(
                LiveNarrative.VerifierAdvisoryKey, count => VerifierAdvisoryLine(count, taskId, finding));
        }
    }

    /// <summary>
    /// The verifier advisory's line (#229, design 37 §4.5). At <paramref name="count"/> 1 this is
    /// byte-identical to the line this surface printed before design 37, so a single occurrence reads exactly
    /// as it always did; beyond that it states how many tasks are affected and which one is latest — the fact
    /// that makes a systemic misconfiguration legible as systemic. Yellow, not red: §12.6 forbids a verifier
    /// condition from ever failing a build, and colouring it as a failure buys a triage the operator does not
    /// owe. Both harness strings are escaped — a runner name with a bracket would otherwise be read as markup.
    /// </summary>
    public static string VerifierAdvisoryLine(int count, string taskId, string finding) =>
        count <= 1
            ? $"[yellow]verifier advisory[/] [grey]{Markup.Escape(taskId)}[/]: {Markup.Escape(finding)}"
            : $"[yellow]verifier advisory[/] — {count} task(s), latest "
              + $"[grey]{Markup.Escape(taskId)}[/]: {Markup.Escape(finding)}";

    public void OverwatchNoVerdict(string taskId, string reason)
    {
        lock (_gate)
        {
            // Issue #452, COALESCED (design 37 §4.4 #4) — the same shape as the verifier advisory: the same
            // sentence about a systemic misconfiguration, repeated per task. It must still be SAID: before
            // #452 a billed supervisor that produced nothing was byte-identical, on this surface, to one that
            // had nothing to say.
            AppendCoalescingNarrative(
                LiveNarrative.OverwatchNoVerdictKey, count => OverwatchNoVerdictLine(count, taskId, reason));
        }
    }

    /// <summary>
    /// The silent-overwatcher advisory's line (#452, design 37 §4.5). Singular is byte-identical to the
    /// pre-design-37 line. Yellow, not red — the overwatcher gates nothing: a no-verdict changes no task
    /// outcome and no exit code.
    /// </summary>
    public static string OverwatchNoVerdictLine(int count, string taskId, string reason) =>
        count <= 1
            ? $"[yellow]overwatch: no verdict[/] [grey]{Markup.Escape(taskId)}[/] — {Markup.Escape(reason)}"
            : $"[yellow]overwatch: no verdict[/] — {count} task(s), latest "
              + $"[grey]{Markup.Escape(taskId)}[/]: {Markup.Escape(reason)}";

    /// <summary>
    /// The attempt model MISMATCH line (#349, design 37 §4.5). Only ever rendered when a requested model is
    /// present — design 29 §3.3 binds the Model cell's <c>!</c> to a companion line ONLY in the mismatch case,
    /// and in the agreeing case the cell alone is the whole disclosure (§4.4 #2). The wording comes from
    /// <see cref="AttemptModelSummary"/>, the same formatter the plain surface renders, so the two surfaces
    /// cannot state the same attempt two different ways.
    /// </summary>
    public static string ModelMismatchLine(
        int count, string taskId, int attempt, string model, string? requestedModel) =>
        count <= 1
            ? $"[yellow]model[/] [grey]{Markup.Escape(taskId)}[/] attempt {attempt}: "
              + $"[yellow]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]"
            : $"[yellow]model MISMATCH[/] — {count} attempt(s), latest [grey]{Markup.Escape(taskId)}[/]: "
              + $"[yellow]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]";

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        lock (_gate)
        {
            // Design 37 §4.4 #2, SPLIT on the presence of a requested model. The AGREEING case is CELL ONLY:
            // this emitter's own comment already conceded that "a per-attempt disclosure is not news", and
            // the Model cell is written a few lines below. The MISMATCH case keeps its line — design 29 §3.3
            // makes the cell's `!` "a POINTER, not a code: it never appears without a companion line" — and
            // coalesces, because a route misconfiguration repeats per attempt across the whole run.
            if (requestedModel is not null)
            {
                AppendCoalescingNarrative(
                    LiveNarrative.ModelMismatchKey,
                    count => ModelMismatchLine(count, task.Id, attempt, model, requestedModel));
            }

            // The CORRECTION over the launch-event cell (design 29 §4.2/§4.3): this event cannot fire
            // until the runner has reported what it ran on (MEASURED at 14m02s+ per attempt), so the cell
            // is populated at LAUNCH by AttemptRouteResolved and this only confirms or corrects it.
            // `substituted` mirrors AttemptModelResolved's own signal: requestedModel is non-null ONLY
            // when the provider served something else. No route recorded for this task (a script attempt,
            // or a legacy/no-route "(cli default)" attempt) leaves the cell exactly as the launch event
            // (or the pending seed) left it, rather than guessing at a block name that was never resolved.
            if (_routeByTask.TryGetValue(task.Id, out (string Runner, string? Tier, bool Climbed) route)
                && _rowByKey.TryGetValue(task.Id, out int row))
            {
                _table.UpdateCell(row, 3, new Markup(
                    ModelCell(route.Runner, route.Tier, route.Climbed, substituted: requestedModel is not null, isScript: false)));
                _context?.Refresh();
            }
        }
    }

    /// <summary>
    /// The live table's Model cell at attempt LAUNCH (#524, design 29 §4.2/§4.3) — the PRIMARY source,
    /// because <see cref="AttemptModelResolved"/> cannot fire until the runner has reported what it ran
    /// on (§1.1: MEASURED at 14m02s and longer per attempt), so a cell fed only from it is a placeholder
    /// for the whole attempt. Remembers the (runner, tier, climbed) triple so the later confirmation/
    /// correction from <see cref="AttemptModelResolved"/> can re-render the SAME cell without re-deriving
    /// the climb signal — <paramref name="requestedTier"/>'s presence is that signal, exactly as
    /// <c>AttemptProvenance.RequestedModel</c>'s presence is the substitution signal.
    /// </summary>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)
    {
        bool climbed = requestedTier is not null;
        string cell = ModelCellFromRoute(runner, tier, requestedTier);

        lock (_gate)
        {
            _routeByTask[task.Id] = (runner, tier, climbed);
            if (_rowByKey.TryGetValue(task.Id, out int row))
            {
                _table.UpdateCell(row, 3, new Markup(cell));
                _context?.Refresh();
            }
        }
    }

    /// <summary>Stop the live region (the final summary prints after disposal).</summary>
    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync().ConfigureAwait(false); // no tick during/after teardown
        _done.TrySetResult();
        await _liveLoop.ConfigureAwait(false);
    }

    /// <summary>
    /// (Re)build the table's rows + the <see cref="_rowByKey"/> index map from the current
    /// <see cref="_tasks"/>/<see cref="_waves"/>/<see cref="_completedWaves"/> state. Called at
    /// construction, each time a wave settles (<see cref="WaveFinished"/>, to collapse it, issue #379), and
    /// — since #404 — each time a JIT wave is spliced in mid-run. Caller holds <see cref="_gate"/> (or runs
    /// single-threaded during the ctor).
    ///
    /// <para><b>Every keyed row's rendered CELLS survive the rebuild</b> (design 37 §0.4). #379's original
    /// safety argument was positional: a rebuild only happened at a wave boundary, where the hard barrier
    /// (SSOT §14.4) guarantees every task in a not-yet-completed later wave is still <c>pending</c>, so
    /// re-seeding those rows to <c>pending</c> discarded nothing. That argument does not survive #404. Under
    /// <c>--all-tasks</c> the collapse rebuild is guarded off entirely, so before #404 this ran exactly once,
    /// at construction; a mid-run splice makes it run again with completed tasks on screen, and re-seeding
    /// would flip every green <c>succeeded</c> back to grey <c>pending</c>. Carrying the cells across
    /// retires that latent fragility for every caller instead of adding a second special case for this one.
    /// A COLLAPSED wave's task rows are still dropped — they are not in the new row plan at all, which is
    /// #379's whole point.</para>
    /// </summary>
    private void RebuildRows()
    {
        // Snapshot the rendered cells of every keyed row BEFORE the table is cleared. Keyed rows only: a
        // wave summary line is derived state and is regenerated from _completedWaves each time.
        // TableRowCollection is enumerable but not indexable in Spectre 0.51.1, hence the materialisation.
        List<TableRow> existing = [.. _table.Rows];
        var preserved = new Dictionary<string, IRenderable[]>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> entry in _rowByKey)
        {
            if (entry.Value < existing.Count)
            {
                preserved[entry.Key] = [.. existing[entry.Value]];
            }
        }

        _rowByKey.Clear();
        _table.Rows.Clear();

        IReadOnlyList<LiveTableRow> rows =
            LiveTableRows.Plan(_tasks, _waves, _completedWaves, _showAllTasks, _breakdownWaves);
        for (int i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case TaskLiveRow task:
                    _rowByKey[task.TaskId] = i;
                    if (preserved.TryGetValue(task.TaskId, out IRenderable[]? taskCells))
                    {
                        _table.AddRow(taskCells);
                        break;
                    }

                    _table.AddRow(
                        new Markup(Markup.Escape(task.TaskId)),
                        new Markup("[grey]pending[/]"),
                        new Markup(string.Empty),
                        new Markup(PendingModelCell(task.TaskId)));
                    break;
                case WaveSummaryLiveRow wave:
                    _table.AddRow(
                        new Markup($"[green]✔ {Markup.Escape(wave.WaveDir)} — {wave.TaskCount}/{wave.TaskCount} tasks green[/]"),
                        new Markup(string.Empty),
                        new Markup(string.Empty),
                        new Markup(string.Empty));
                    break;
                case WavePhaseLiveRow phase:
                    // Issue #469: the row exists from RUN START, so a two-wave JIT plan is legible as a
                    // two-wave plan before anything happens, and the breakdown has a row to update the
                    // moment it begins. No new glyph: only the `—` every other row already prints. Its
                    // SETTLED cells (18m42s · 5 task folders · logs) are preserved across #404's splice
                    // rebuild by the same mechanism as a task row — that authoring provenance is the only
                    // place those numbers appear live, and §5.2's B2 keeps the row.
                    _rowByKey[phase.BreakdownKey] = i;
                    if (preserved.TryGetValue(phase.BreakdownKey, out IRenderable[]? phaseCells))
                    {
                        _table.AddRow(phaseCells);
                        break;
                    }

                    _table.AddRow(
                        new Markup($"{Markup.Escape(phase.WaveDir)} — JIT breakdown"),
                        new Markup("[grey]pending[/]"),
                        new Markup("[grey]no tasks yet — authored at the barrier[/]"),
                        new Markup(string.Empty));
                    break;
            }
        }
    }

    /// <summary>
    /// The row plan after a freshly-authored wave replaces its own JIT stub (#404) — pure over the row plan,
    /// mirroring <c>Scheduler.SpliceAuthoredWave</c>: the ONE wave whose <see cref="WaveNode.Dir"/> matches
    /// is replaced, and the flattened task list is re-derived as the union in strict wave order (SSOT §14.2),
    /// never appended to. Deriving rather than appending is what keeps the row order the loader's order even
    /// if several waves are authored across one run.
    ///
    /// <para>A wave this table was never told about — a FLAT plan, or an <c>attach</c> against a different
    /// plan — returns the inputs unchanged rather than growing a row set the operator's plan does not have.
    /// </para>
    ///
    /// <para>Public (not private) for the same reason <see cref="StatusMarkup"/> and <see cref="ModelCell"/>
    /// are: the Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure function IS the test seam.</para>
    /// </summary>
    public static (IReadOnlyList<TaskNode> Tasks, IReadOnlyList<WaveNode> Waves) SpliceWave(
        IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves, WaveNode authoredWave)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(waves);
        ArgumentNullException.ThrowIfNull(authoredWave);

        if (!waves.Any(w => string.Equals(w.Dir, authoredWave.Dir, StringComparison.Ordinal)))
        {
            return (tasks, waves);
        }

        List<WaveNode> spliced =
        [
            .. waves.Select(w =>
                string.Equals(w.Dir, authoredWave.Dir, StringComparison.Ordinal) ? authoredWave : w)
        ];

        return ([.. spliced.SelectMany(w => w.Tasks)], spliced);
    }

    /// <summary>
    /// A pending task row's Model cell, seeded from what is already known at load (design 29 §4.2): the
    /// task's own resolved tier, or <c>(script)</c> for a script action. Never blank — an empty cell in a
    /// live table reads as "still resolving", which is exactly the wrong claim about a task that has not
    /// started (§1.1's rule, applied at the other end of a task's life).
    /// </summary>
    private string PendingModelCell(string taskId) =>
        _taskById.TryGetValue(taskId, out TaskNode? task)
            ? ModelCell(
                runner: null, tier: task.Action.Tier, climbed: false, substituted: false,
                isScript: task.Action.Kind == ActionKind.Script)
            : ModelCell(runner: null, tier: null, climbed: false, substituted: false, isScript: false);

    private void Update(string taskId, string? statusMarkup, string? detailMarkup)
    {
        lock (_gate)
        {
            // #379: a task whose wave has collapsed has no row — tolerate the missing id as a no-op
            // (a resume replays completed-wave `skipped`/finish events; its logs stay on the static site).
            if (!_rowByKey.TryGetValue(taskId, out int row))
            {
                return;
            }

            if (statusMarkup is not null)
            {
                _table.UpdateCell(row, 1, new Markup(statusMarkup));
            }

            if (detailMarkup is not null)
            {
                _table.UpdateCell(row, 2, new Markup(detailMarkup));
            }

            _context?.Refresh();
        }
    }

    /// <summary>
    /// The Spectre markup for a finished task's Status cell, keyed on its <see cref="TaskOutcome"/> and —
    /// for a needs-human halt — the agent's optional <paramref name="needsHumanKind"/> claim (issue #485).
    /// Public (not private) for the same reason <see cref="Commands.RunCommand.Hyperlink"/> is — the
    /// Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure mapping method is the test seam
    /// (issue #190: proves <see cref="TaskOutcome.RateLimited"/> renders distinctly from the generic
    /// needs-human red).
    ///
    /// <para>#485 uses the width-scarce TERSE form (<c>needs human (work)</c> / <c>needs human (guardrail)</c>)
    /// because this is the narrowest surface in the product; every other surface prints the full contract
    /// token. UNCLASSIFIED renders the unqualified <c>needs human</c> — byte-for-byte what every run has
    /// always printed — so it cannot read as either kind, cannot look broken, and costs zero characters.
    /// Colour stays red for all three: #190 spent blue on "provider-side, re-run later", and a defective
    /// guardrail is not re-run-later, so a second colour would blur that signal for no gain the text does
    /// not already carry.</para>
    /// </summary>
    public static string StatusMarkup(TaskOutcome outcome, string? needsHumanKind = null) => outcome switch
    {
        TaskOutcome.Succeeded => "[green]succeeded[/]",
        TaskOutcome.Skipped => "[green]skipped[/]",
        TaskOutcome.Blocked => "[orange3]blocked[/]",
        TaskOutcome.Cancelled => "[grey]cancelled[/]",
        // Issue #190: distinct from a generic needs-human — blue matches the PromptPaused transient
        // color convention above, so a human reading the table associates blue with "provider-side,
        // re-run later", never "your task is broken" (red).
        TaskOutcome.RateLimited => "[blue]rate limited[/]",
        _ => NeedsHumanKinds.Terse(needsHumanKind) is { } terse
            ? $"[red]needs human ({terse})[/]"
            : "[red]needs human[/]"
    };

    /// <summary>
    /// The ONE rendering of an attempt's resolved model (#349) — the single formatter BOTH the live table
    /// and the plain <see cref="ConsoleRunObserver"/> call, so the two surfaces cannot drift into two
    /// different ways of stating the same fact. <paramref name="model"/> is the attempt's
    /// best-known-actual model; <paramref name="requestedModel"/> is non-null only when the route asked
    /// for something else, and its PRESENCE is what makes this a mismatch line rather than an ordinary
    /// one. The formatter reads the two fields it is handed and re-derives nothing.
    ///
    /// <para>Public (not private) for the same reason <see cref="StatusMarkup"/> and
    /// <see cref="PostMortemPagePath"/> are: no live terminal renders in a non-interactive test and the
    /// Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure function IS the test seam.</para>
    ///
    /// <para>The two forms are deliberately DIFFERENT, not one shape with an optional field: a formatter
    /// that always named one model would render every mismatch as an ordinary attempt, and one that always
    /// named two would make the two-string form carry no information at all. The mismatch form leads with
    /// the model that ACTUALLY ran — the fact the operator needs first — and names the requested one after
    /// the word MISMATCH, so a line scrolled past at 3am reads as a disagreement without being decoded by
    /// someone who never asked for a second model.</para>
    ///
    /// <para>Plain text, no Spectre markup: the live renderer escapes this whole string before writing it,
    /// so a bracket here would be shown rather than interpreted.</para>
    /// </summary>
    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} — MISMATCH: the route requested {requestedModel}";

    /// <summary>
    /// The live table's Model column cell (design 29 §4.2) — a pure formatter over the six
    /// distinguishable cell states, so the seam is testable without touching the live region (no test
    /// may construct <see cref="LiveRunObserver"/>).
    ///
    /// <para>A known <paramref name="runner"/> (the <c>promptRunners</c> block key) renders as that name,
    /// plus a trailing <c>!</c> when <paramref name="climbed"/> or <paramref name="substituted"/> — never
    /// the model id and never a mismatch sentence (§3.3: that sentence is 61 characters, measured, and
    /// would re-lay-out every other row). The <c>!</c> is a POINTER, not a code: it never appears without
    /// a companion line above the live region that spells the disagreement out in full (the shipped
    /// <see cref="AttemptModelSummary"/> wording), so the cell never says anything that line does not.</para>
    ///
    /// <para>An unknown runner (nothing resolved yet — the row-build seed) renders the repo's own
    /// stand-in convention: <c>(medium)</c>/<c>(easy)</c>/<c>(hard)</c> for a tagged prompt task,
    /// <c>(script)</c> for a script action, else the untagged placeholder <c>—</c>. Never blank: an empty
    /// cell in a live table reads as "still resolving", which is a wrong claim whether the row is
    /// running healthily on an already-resolved route or has not started at all (§1.1).</para>
    ///
    /// <para>Colour is redundant by construction — grey where the cell agrees, yellow where it carries
    /// <c>!</c> — exactly the pair <see cref="AttemptModelResolved"/> already spends today, so a
    /// colourblind operator on a colour-capable terminal loses nothing.</para>
    /// </summary>
    public static string ModelCell(
        string? runner, string? tier, bool climbed, bool substituted, bool isScript)
    {
        if (runner is not null)
        {
            bool mismatch = climbed || substituted;
            string text = mismatch ? $"{runner} !" : runner;
            string colour = mismatch ? "yellow" : "grey";
            return $"[{colour}]{Markup.Escape(text)}[/]";
        }

        string placeholder = isScript ? "(script)" : tier is not null ? $"({tier})" : "—";
        return $"[grey]{Markup.Escape(placeholder)}[/]";
    }

    /// <summary>
    /// Translates the <see cref="Core.Execution.IRunObserver.AttemptRouteResolved"/> launch event into
    /// <see cref="ModelCell"/>'s arguments (design 29 §4.2/§4.3): <c>climbed</c> is
    /// <c>requestedTier is not null</c>, because <c>requestedTier</c> is written ONLY when a §6.2 climb
    /// moved the rung, so its presence is the signal. Delegates to <see cref="ModelCell"/> rather than
    /// re-implementing its formatting — the two are asserted to AGREE over the whole input domain, so an
    /// inlined divergent copy would pass today and fail the moment they drift.
    /// </summary>
    public static string ModelCellFromRoute(string runner, string? tier, string? requestedTier) =>
        ModelCell(runner, tier, climbed: requestedTier is not null, substituted: false, isScript: false);
}
