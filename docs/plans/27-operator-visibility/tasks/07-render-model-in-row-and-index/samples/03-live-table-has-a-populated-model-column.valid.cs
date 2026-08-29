using Spectre.Console;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// Representative CORRECT shape. Three properties, and all three are load-bearing:
///
/// 1. The table declares a Model column as its LAST column, built as
///    <c>new TableColumn("Model").Width(8)</c> — appended, because Update() and Tick() write hard-coded
///    cell indices 1 and 2, and pinned at 8 because an auto-sized column lets one long block key steal
///    16 characters from every row for the whole run.
/// 2. The model actually reaches that cell: ModelCell is declared here AND called from the places that
///    write a row — the initial build, the LAUNCH-time route event, and the post-action correction —
///    and every one of those writes lands in cell index 3 through <c>UpdateCell</c> or the row seed.
/// 3. The cell is filled at attempt LAUNCH, from AttemptRouteResolved, not at attempt END. That is the
///    whole point: AttemptModelResolved cannot fire until the runner has reported what it ran on, and
///    attempts on this repo's own runs last 14m02s and longer. A column fed only from the post-action
///    event is a placeholder for the entire attempt.
/// 4. The route handler goes through <c>ModelCellFromRoute</c>, the pure translation seam. That is not
///    ceremony: the translation it performs — <c>climbed</c> is <c>requestedTier is not null</c>,
///    because requestedTier is written ONLY on a §6.2 climb — is the rule the whole event turns on, and
///    inline in this handler it would be unreachable from any test, since no test may construct this
///    type. Pulled out as a static, it is pinned by an agreement property test, and what remains
///    untestable here is two statements: call it, write the result into the cell.
///
/// The cell carries the promptRunners BLOCK NAME plus at most a one-character flag — never the model
/// id, never AttemptModelSummary's 61-character mismatch sentence. The full disclosure is the console
/// line above the live region; the cell is an index into that line, and `sonnet` is a literal substring
/// of it, so the two surfaces cannot be read as two different facts.
///
/// Whether ModelCell returns bare text or colour markup is an implementation choice — this sample bakes
/// the colour in, so the state that decides the text also decides the hue and the two cannot disagree.
/// Colour is redundant by construction: "(…)", "!" and the words already say it in text.
/// </summary>
public sealed class LiveRunObserver : IRunObserver
{
    private readonly object _gate = new();
    private readonly Table _table;
    private readonly Dictionary<string, int> _rowByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelByTask = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TaskNode> _tasks;

    public LiveRunObserver(IReadOnlyList<TaskNode> tasks)
    {
        _tasks = tasks;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");
        // Appended LAST (#524): Update() and Tick() write hard-coded cell indices 1 and 2, so a column
        // inserted ahead of them would silently re-target every one of those writes. Width(8) is
        // measured, not assumed; no .NoWrap(), because a truncated block name misnames the model.
        _table.AddColumn(new TableColumn("Model").Width(8));

        RebuildRows();
    }

    /// <summary>
    /// The model cell's text — the six states of the design's §4.2 table, in eight characters or fewer
    /// for every block this repo configures. It names the promptRunners BLOCK, never the model id: the
    /// id is 15–25 characters and the mismatch sentence is 61, and one such cell re-lays-out the table.
    /// The parenthesis convention is the repo's own — AttemptProvenance.Model already spells a stand-in
    /// as "(cli default)" — so "(medium)" reads as *planned, not yet actual* and the column is never
    /// blank. "!" is a pointer, not a code: one flag, one meaning, always accompanied by a full-prose
    /// line above the region.
    /// </summary>
    public static string ModelCell(string? runner, string? tier, bool climbed, bool substituted, bool isScript)
    {
        string text =
            isScript ? "(script)"
            : runner is { Length: > 0 } ? (climbed || substituted ? runner + " !" : runner)
            : tier is { Length: > 0 } ? "(" + tier + ")"
            : "—";

        string colour = climbed || substituted ? "yellow" : "grey";
        return $"[{colour}]{Markup.Escape(text)}[/]";
    }

    /// <summary>
    /// The LAUNCH-event translation, pulled out of the handler so a test can drive it. `climbed` is
    /// `requestedTier is not null` and nothing else: requestedTier is written ONLY when a §6.2 climb
    /// moved the rung, so its PRESENCE is the signal — an always-written copy would make every ordinary
    /// attempt look like a climb. It DELEGATES to ModelCell rather than re-formatting, so the two can
    /// never drift; its test asserts that agreement over the whole input domain.
    /// </summary>
    public static string ModelCellFromRoute(string runner, string? tier, string? requestedTier) =>
        ModelCell(runner, tier, climbed: requestedTier is not null, substituted: false, isScript: false);

    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} — MISMATCH: the route requested {requestedModel}";

    public static string StatusMarkup(string outcome) => $"[green]{outcome}[/]";

    /// <summary>
    /// The route an attempt is ABOUT TO LAUNCH on (#524). This is the event that makes the column
    /// useful: it fires before the action runs, so the cell names the block for the whole attempt
    /// rather than for the moment after it ends. requestedTier is non-null ONLY when a §6.2 climb moved
    /// the rung, so its presence IS the climb signal.
    /// </summary>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)
    {
        AnsiConsole.MarkupLine(
            $"[grey]route[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[grey]{Markup.Escape(runner)} -> {Markup.Escape(model)}[/]");

        WriteModelCell(task.Id, ModelCellFromRoute(runner, tier, requestedTier));
    }

    /// <summary>
    /// The model an attempt ACTUALLY ran on — the confirmation or correction of what the route event
    /// announced. It still goes to the scrollback (that is what a --no-ui operator reads) AND now
    /// updates the task's own cell, where it persists after the task settles.
    /// </summary>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        string colour = requestedModel is null ? "grey" : "yellow";
        AnsiConsole.MarkupLine(
            $"[{colour}]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[{colour}]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]");

        if (requestedModel is not null)
        {
            WriteModelCell(task.Id, ModelCell(RunnerOf(task.Id), TierOf(task), climbed: false,
                substituted: true, isScript: false));
        }
    }

    private void WriteModelCell(string taskId, string cell)
    {
        lock (_gate)
        {
            _modelByTask[taskId] = cell;
            if (_rowByKey.TryGetValue(taskId, out int row))
            {
                _table.UpdateCell(row, 3, new Markup(cell));
            }
        }
    }

    private void RebuildRows()
    {
        _table.Rows.Clear();
        _rowByKey.Clear();
        int index = 0;
        foreach (TaskNode task in _tasks)
        {
            // The pending cell is seeded from what is ALREADY known at load — the planned rung, or
            // "(script)" — so the column is never blank and never a placeholder that means nothing.
            string pending = _modelByTask.TryGetValue(task.Id, out string? cell)
                ? cell
                : ModelCell(null, TierOf(task), climbed: false, substituted: false,
                    isScript: task.Action.Kind == ActionKind.Script);

            _table.AddRow(
                new Markup(Markup.Escape(task.Id)),
                new Markup("[grey]pending[/]"),
                new Markup(string.Empty),
                new Markup(pending));
            _rowByKey[task.Id] = index++;
        }
    }

    private void Update(string taskId, string? statusMarkup, string? detailMarkup)
    {
        if (!_rowByKey.TryGetValue(taskId, out int row))
        {
            return;
        }

        if (statusMarkup is not null) { _table.UpdateCell(row, 1, new Markup(statusMarkup)); }
        if (detailMarkup is not null) { _table.UpdateCell(row, 2, new Markup(detailMarkup)); }
    }

    private static string? TierOf(TaskNode task) => task.Action.Tier;

    private string? RunnerOf(string taskId) => null;
}
