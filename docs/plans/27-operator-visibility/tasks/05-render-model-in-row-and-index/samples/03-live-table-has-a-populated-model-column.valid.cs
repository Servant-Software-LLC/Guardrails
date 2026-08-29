using Spectre.Console;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// Representative CORRECT shape: the live table declares a Model column as its LAST column, and the
/// model actually reaches that cell — ModelCell is declared here AND called from the two places that
/// write a row (the initial build and the per-attempt update), so the column is populated rather
/// than being a header with nothing under it.
/// </summary>
public sealed class LiveRunObserver
{
    private readonly object _gate = new();
    private readonly Table _table;
    private readonly Dictionary<string, int> _rowByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _modelByTask = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TaskNode> _tasks;

    public LiveRunObserver(IReadOnlyList<TaskNode> tasks)
    {
        _tasks = tasks;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");
        // Appended LAST (#524): Update() and Tick() write hard-coded cell indices 1 and 2, so a column
        // inserted ahead of them would silently re-target every one of those writes.
        _table.AddColumn("Model");

        RebuildRows();
    }

    /// <summary>
    /// The model cell's text: the model that actually ran, and — when the route asked for a different
    /// one — both, in the SAME wording the console line and the log-site index use. One vocabulary for
    /// one fact; two formatters is how the two drift.
    /// </summary>
    public static string ModelCell(string? model, string? requestedModel) =>
        model is null
            ? "[grey]—[/]"
            : Markup.Escape(AttemptModelSummary(model, requestedModel));

    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} — MISMATCH: the route requested {requestedModel}";

    public static string StatusMarkup(string outcome) => $"[green]{outcome}[/]";

    /// <summary>
    /// The model an attempt resolved to. It still goes to the scrollback (that is what a --no-ui
    /// operator reads) AND now lands in the task's own row, where it persists after the task settles —
    /// the whole of #524: a transient line above a pinned live region cannot answer a question asked
    /// after the fact.
    /// </summary>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        string colour = requestedModel is null ? "grey" : "yellow";
        AnsiConsole.MarkupLine(
            $"[{colour}]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[{colour}]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]");

        lock (_gate)
        {
            _modelByTask[task.Id] = ModelCell(model, requestedModel);
            if (_rowByKey.TryGetValue(task.Id, out int row))
            {
                _table.UpdateCell(row, 3, new Markup(_modelByTask[task.Id]!));
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
            _table.AddRow(
                new Markup(Markup.Escape(task.Id)),
                new Markup("[grey]pending[/]"),
                new Markup(string.Empty),
                new Markup(_modelByTask.TryGetValue(task.Id, out string? cell) && cell is not null
                    ? cell
                    : ModelCell(null, null)));
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
}
