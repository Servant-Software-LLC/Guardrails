using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console;

namespace Guardrails.Cli.Ui;

/// <summary>
/// Spectre live-table progress: one row per task, updated in place as workers report
/// events. Used only when the terminal is interactive and <c>--no-ui</c> is absent;
/// otherwise the plain <see cref="ConsoleRunObserver"/> runs. All mutation is gated —
/// M4 workers call in concurrently.
/// </summary>
public sealed class LiveRunObserver : IRunObserver, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Table _table;

    // Row index keyed by EITHER a task id or a wave-phase key "<waveDir>/(<phase>)" (issue #469). One map,
    // because a phase row is updated through exactly the same UpdateCell path as a task row — the parenthesised
    // segment cannot collide with an SSOT §14.2 wave-qualified task id.
    private readonly Dictionary<string, int> _rowByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningState> _running = new(StringComparer.Ordinal);

    // In-flight wave phases (issue #469): the JIT breakdown, and — per design 23 §9 — the shape #476's wave
    // gates will reuse. Driven by the SAME 1 Hz ticker as _running; no new timer and no new lock.
    private readonly Dictionary<string, PhaseState> _phases = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _liveLoop;
    private readonly Timer _ticker;
    private readonly Func<string, string?>? _logUrlForTask;
    private readonly string? _planDirectory;
    private readonly string? _runId;
    // #379 collapse-completed-waves state. For a FLAT plan (or --all-tasks) these stay inert and the
    // table is the pre-#379 flat one-row-per-task list; for a WAVED plan the row set is (re)planned via
    // LiveTableRows as each wave settles, collapsing completed waves to a single summary row.
    private readonly IReadOnlyList<TaskNode> _tasks;
    private readonly IReadOnlyList<WaveNode> _waves;
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
    public LiveRunObserver(
        IReadOnlyList<TaskNode> tasks,
        Func<string, string?>? logUrlForTask = null,
        string? planDirectory = null,
        string? runId = null,
        IReadOnlyList<WaveNode>? waves = null,
        bool showAllTasks = false)
    {
        _logUrlForTask = logUrlForTask;
        _planDirectory = planDirectory;
        _runId = runId;
        _tasks = tasks;
        _waves = waves ?? [];
        _showAllTasks = showAllTasks;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");

        RebuildRows();

        _liveLoop = AnsiConsole.Live(_table).StartAsync(async ctx =>
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

        string detail = "previous attempt failed";
        if (LogLinkMarkup(task.Id) is { } link)
        {
            detail += $" · {link}";
        }

        Update(task.Id, $"[yellow]retry {attempt}/{budget}[/]", detail);
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
    /// never repeated (design 23 §5.1). Written above the live region under <see cref="_gate"/>, the shipped
    /// <see cref="WaveStarting"/> / <see cref="OverwatchNoVerdict"/> idiom for a ONE-SHOT line (#145/#372);
    /// nothing repeats above the region. Caller holds the gate.
    /// </summary>
    private static void MaybeAnnounceCeiling(PhaseState phase, TimeSpan elapsed)
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
        AnsiConsole.MarkupLine(
            $"[yellow]{wave}: {spent} of a {ceiling} ceiling — the breakdown will be CUT OFF at {ceiling}.[/]");
        AnsiConsole.MarkupLine(
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
            // Above the live region (like PlanHashMismatch/DecisionRecorded) so it segments the table by
            // wave without mutating table rows (the #145 in-region-write corruption is avoided).
            AnsiConsole.MarkupLine(
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
            AnsiConsole.MarkupLine($"[bold]Wave {Markup.Escape(wave.Dir)}:[/] {verb}");

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

            // The first of the TWO one-shot lines, above the live region under the gate — the shipped
            // WaveStarting / DecisionRecorded / OverwatchNoVerdict idiom (#145/#372). Everything that then
            // repeats per second goes through UpdateCell, never here.
            AnsiConsole.MarkupLine(
                $"[bold]Wave {context.Index}/{context.Total}:[/] {Markup.Escape(context.WaveDir)} — "
                + $"authoring tasks (JIT breakdown). Ceiling {BreakdownProgress.FormatClock(context.Ceiling)}.");
            AnsiConsole.MarkupLine($"  [grey]Breakdown log: {Markup.Escape(context.BreakdownLogDir)}[/]");

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
            if (!_rowByKey.TryGetValue(key, out int row))
            {
                return;
            }

            string colour = failureKind is null ? "green" : "red";
            string status = BreakdownProgress.TerminalStatus(BreakdownProgress.TerminalWord(failureKind), elapsed);
            string detail = Markup.Escape(BreakdownProgress.TerminalDetail(failureKind, snapshot));
            if (WavePageLinkMarkup(context.WaveDir, "logs") is { } link)
            {
                detail = $"{detail} · {link}";
            }

            _table.UpdateCell(row, 1, new Markup($"[{colour}]{Markup.Escape(status)}[/]"));
            _table.UpdateCell(row, 2, new Markup(detail));
            _context?.Refresh();
        }
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        lock (_gate)
        {
            AnsiConsole.MarkupLine(
                "[bold yellow]WARNING:[/] plan manifests changed since the last run " +
                $"(previous hash {Markup.Escape(previousPlanHash)}). Resuming anyway; use --fresh for a clean slate.");
        }
    }

    public void DecisionRecorded(DecisionEntry entry)
    {
        lock (_gate)
        {
            // An autonomy-policy decision (SSOT §2.1/§7). Emitted above the live region (like
            // PlanHashMismatch) so the operator sees what a decision did — the headline is pre-rendered,
            // subject names the units. M1 emits only boundary "drift" (a safe drift auto-resolved).
            string subject = string.IsNullOrEmpty(entry.Subject) ? "" : $": {Markup.Escape(entry.Subject)}";
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(entry.Headline)}[/]{subject}");
        }
    }

    public void VerifierAdvisoryFound(string taskId, string finding)
    {
        lock (_gate)
        {
            // The DoR §6.5 run-start advisory (#229), one line per affected task. Written above the
            // live region under _gate exactly like PlanHashMismatch/DecisionRecorded: the Scheduler
            // raises this from INSIDE the Spectre live region, so a raw Console.Write here corrupts
            // the task table (#145). Yellow, not red — §12.6 forbids a verifier condition from ever
            // failing a build, and colouring it as a failure buys the operator a triage they do not
            // owe. Both strings come from the harness, but both are escaped: a runner name with a
            // bracket in it would otherwise be read as Spectre markup.
            AnsiConsole.MarkupLine(
                $"[yellow]verifier advisory[/] [grey]{Markup.Escape(taskId)}[/]: {Markup.Escape(finding)}");
        }
    }

    public void OverwatchNoVerdict(string taskId, string reason)
    {
        lock (_gate)
        {
            // Issue #452. Written ABOVE the live region under _gate, exactly like VerifierAdvisoryFound
            // and DecisionRecorded: the executor raises this from INSIDE the Spectre live region, and a
            // raw write there corrupts the task table (#145/#372). Same advisory idiom as the verifier
            // advisory — yellow, not red — because the overwatcher gates nothing: a no-verdict changes no
            // task outcome and no exit code. But it must PRINT: before #452 a billed supervisor that
            // produced nothing was byte-identical, on this surface, to one that had nothing to say.
            AnsiConsole.MarkupLine(
                $"[yellow]overwatch: no verdict[/] [grey]{Markup.Escape(taskId)}[/] — {Markup.Escape(reason)}");
        }
    }

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        lock (_gate)
        {
            // Issue #349. Written ABOVE the live region under _gate, exactly like VerifierAdvisoryFound
            // and OverwatchNoVerdict: the executor raises this from INSIDE the Spectre live region, and a
            // raw write there corrupts the task table (#145/#372). The TEXT comes from AttemptModelSummary
            // — the same formatter the plain surface renders — so the two surfaces cannot state the same
            // attempt two different ways. Both harness strings ride inside it and the whole thing is
            // escaped: a model id with a bracket would otherwise be read as markup.
            //
            // Grey for the agreeing case (a per-attempt disclosure is not news) and the advisory yellow
            // when a requested model is present — the same presence signal the formatter keys on, spent
            // here only on colour. Nothing recomputes the comparison; the fold already decided.
            string colour = requestedModel is null ? "grey" : "yellow";
            AnsiConsole.MarkupLine(
                $"[{colour}]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
                + $"[{colour}]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]");
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
    /// (Re)build the table's rows + the <see cref="_rowByTask"/> index map from the current
    /// <see cref="_completedWaves"/> set (issue #379). Called once at construction and again each time a
    /// wave settles (from <see cref="WaveFinished"/>) to collapse it. Safe because a rebuild only ever
    /// happens at a wave boundary, where the hard barrier (SSOT §14.4) guarantees every task in a
    /// not-yet-completed later wave is still <c>pending</c> — so re-seeding later rows to pending never
    /// discards live progress, and the just-completed wave's rows are intentionally replaced by its
    /// summary line. Caller holds <see cref="_gate"/> (or runs single-threaded during the ctor).
    /// </summary>
    private void RebuildRows()
    {
        _rowByKey.Clear();
        _table.Rows.Clear();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(_tasks, _waves, _completedWaves, _showAllTasks);
        for (int i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case TaskLiveRow task:
                    _rowByKey[task.TaskId] = i;
                    _table.AddRow(
                        new Markup(Markup.Escape(task.TaskId)),
                        new Markup("[grey]pending[/]"),
                        new Markup(string.Empty));
                    break;
                case WaveSummaryLiveRow wave:
                    _table.AddRow(
                        new Markup($"[green]✔ {Markup.Escape(wave.WaveDir)} — {wave.TaskCount}/{wave.TaskCount} tasks green[/]"),
                        new Markup(string.Empty),
                        new Markup(string.Empty));
                    break;
                case WavePhaseLiveRow phase:
                    // Issue #469: the row exists from RUN START, so a two-wave JIT plan is legible as a
                    // two-wave plan before anything happens, and the breakdown has a row to update when it
                    // begins — no mid-run rebuild, no new race. No new glyph: only the `—` every other row
                    // already prints.
                    _rowByKey[phase.BreakdownKey] = i;
                    _table.AddRow(
                        new Markup($"{Markup.Escape(phase.WaveDir)} — JIT breakdown"),
                        new Markup("[grey]pending[/]"),
                        new Markup("[grey]no tasks yet — authored at the barrier[/]"));
                    break;
            }
        }
    }

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
}
