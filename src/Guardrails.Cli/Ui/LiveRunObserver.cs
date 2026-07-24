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
    private readonly Dictionary<string, int> _rowByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningState> _running = new(StringComparer.Ordinal);
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

    /// <summary>A task currently running: when it started and the status word to prefix the clock.</summary>
    private readonly record struct RunningState(DateTimeOffset Since, string Prefix);

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
            if (!_rowByTask.ContainsKey(task.Id))
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
    /// Repaint the Status cell of every running task with its live elapsed clock. Runs under the
    /// same gate as event updates, so the table mutates from one place at a time.
    /// </summary>
    private void Tick()
    {
        lock (_gate)
        {
            if (_context is null || _running.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, RunningState> entry in _running)
            {
                // #379: skip a running task whose wave collapsed out from under it (defensive — a wave
                // only collapses once all its tasks are settled and removed from _running).
                if (!_rowByTask.TryGetValue(entry.Key, out int row))
                {
                    continue;
                }

                string elapsed = FormatElapsed(DateTimeOffset.UtcNow - entry.Value.Since);
                _table.UpdateCell(row, 1, new Markup($"[yellow]{entry.Value.Prefix} {elapsed}[/]"));
            }

            _context.Refresh();
        }
    }

    /// <summary>Stopwatch-style elapsed: <c>0:42</c>, <c>12:05</c>, <c>1:03:20</c>.</summary>
    private static string FormatElapsed(TimeSpan e)
    {
        if (e < TimeSpan.Zero)
        {
            e = TimeSpan.Zero;
        }

        return e.TotalHours >= 1
            ? $"{(int)e.TotalHours}:{e.Minutes:D2}:{e.Seconds:D2}"
            : $"{e.Minutes}:{e.Seconds:D2}";
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

        Update(result.TaskId, StatusMarkup(result.Outcome), detail);
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
        _rowByTask.Clear();
        _table.Rows.Clear();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(_tasks, _waves, _completedWaves, _showAllTasks);
        for (int i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case TaskLiveRow task:
                    _rowByTask[task.TaskId] = i;
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
            }
        }
    }

    private void Update(string taskId, string? statusMarkup, string? detailMarkup)
    {
        lock (_gate)
        {
            // #379: a task whose wave has collapsed has no row — tolerate the missing id as a no-op
            // (a resume replays completed-wave `skipped`/finish events; its logs stay on the static site).
            if (!_rowByTask.TryGetValue(taskId, out int row))
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
    /// The Spectre markup for a finished task's Status cell, keyed on its <see cref="TaskOutcome"/>.
    /// Public (not private) for the same reason <see cref="Commands.RunCommand.Hyperlink"/> is — the
    /// Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure mapping method is the test seam
    /// (issue #190: proves <see cref="TaskOutcome.RateLimited"/> renders distinctly from the generic
    /// needs-human red).
    /// </summary>
    public static string StatusMarkup(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "[green]succeeded[/]",
        TaskOutcome.Skipped => "[green]skipped[/]",
        TaskOutcome.Blocked => "[orange3]blocked[/]",
        TaskOutcome.Cancelled => "[grey]cancelled[/]",
        // Issue #190: distinct from a generic needs-human — blue matches the PromptPaused transient
        // color convention above, so a human reading the table associates blue with "provider-side,
        // re-run later", never "your task is broken" (red).
        TaskOutcome.RateLimited => "[blue]rate limited[/]",
        _ => "[red]needs human[/]"
    };
}
