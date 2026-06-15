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
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _liveLoop;
    private readonly Func<string, string?>? _logUrlForTask;
    private LiveDisplayContext? _context;

    /// <param name="tasks">The tasks to render, one row each.</param>
    /// <param name="logUrlForTask">
    /// Optional resolver mapping a task id to its live-log URL. When supplied, a running task's
    /// Detail cell renders a clickable <c>view log</c> link (OSC 8 hyperlink in capable terminals,
    /// plain text elsewhere). Null = no links (no log server).
    /// </param>
    public LiveRunObserver(IReadOnlyList<TaskNode> tasks, Func<string, string?>? logUrlForTask = null)
    {
        _logUrlForTask = logUrlForTask;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");

        for (int i = 0; i < tasks.Count; i++)
        {
            _rowByTask[tasks[i].Id] = i;
            _table.AddRow(
                new Markup(Markup.Escape(tasks[i].Id)),
                new Markup("[grey]pending[/]"),
                new Markup(string.Empty));
        }

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
    }

    public void TaskStarting(TaskNode task) =>
        Update(task.Id, "[yellow]running[/]", LogLinkMarkup(task.Id) ?? string.Empty);

    public void AttemptStarting(TaskNode task, int attempt, int budget)
    {
        if (attempt > 1)
        {
            string detail = "previous attempt failed";
            if (LogLinkMarkup(task.Id) is { } link)
            {
                detail += $" · {link}";
            }

            Update(task.Id, $"[yellow]retry {attempt}/{budget}[/]", detail);
        }
    }

    /// <summary>
    /// Spectre markup for a clickable "view log" link, or null when no log server is wired.
    /// <c>[link=…]</c> emits an OSC 8 hyperlink in capable terminals (Windows Terminal, VS Code,
    /// iTerm2) and degrades to plain underlined text elsewhere.
    /// </summary>
    private string? LogLinkMarkup(string taskId) =>
        _logUrlForTask?.Invoke(taskId) is { } url ? $"[link={url}]view log[/]" : null;

    public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
        Update(task.Id, null, result.Passed
            ? $"[green]{Markup.Escape(result.Name)} ✓[/]"
            : $"[red]{Markup.Escape(result.Name)} ✗ {Markup.Escape(result.Reason ?? "")}[/]");

    public void TaskFinished(TaskResult result) =>
        Update(result.TaskId, StatusMarkup(result.Outcome), Markup.Escape(result.Summary));

    public void PlanHashMismatch(string previousPlanHash)
    {
        lock (_gate)
        {
            AnsiConsole.MarkupLine(
                "[bold yellow]WARNING:[/] plan manifests changed since the last run " +
                $"(previous hash {Markup.Escape(previousPlanHash)}). Resuming anyway; use --fresh for a clean slate.");
        }
    }

    /// <summary>Stop the live region (the final summary prints after disposal).</summary>
    public async ValueTask DisposeAsync()
    {
        _done.TrySetResult();
        await _liveLoop.ConfigureAwait(false);
    }

    private void Update(string taskId, string? statusMarkup, string? detailMarkup)
    {
        lock (_gate)
        {
            int row = _rowByTask[taskId];
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

    private static string StatusMarkup(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "[green]succeeded[/]",
        TaskOutcome.Skipped => "[green]skipped[/]",
        TaskOutcome.Blocked => "[orange3]blocked[/]",
        TaskOutcome.Cancelled => "[grey]cancelled[/]",
        _ => "[red]needs human[/]"
    };
}
