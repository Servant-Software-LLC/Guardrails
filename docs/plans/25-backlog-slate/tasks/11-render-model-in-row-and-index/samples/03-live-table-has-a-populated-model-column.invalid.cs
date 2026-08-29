using Spectre.Console;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the Model column is DECLARED and never POPULATED. ModelCell is
/// implemented and passes its unit tests, the header renders, and every AddRow dutifully passes a
/// fourth cell — an empty one. The model never reaches the row, so the live table answers "which
/// model ran?" with a blank for the whole run, and a blank in a live table reads as "still
/// resolving". "Declaration is not behaviour" (#468) at cell granularity.
///
/// Note what this sample does NOT do wrong, so the valid/invalid diff is exactly the one defect:
/// AddColumn("Model") is present and correctly appended LAST, ModelCell is declared with the right
/// shape, AttemptModelResolved still forwards to the console, and the name ModelCell even appears in
/// a comment and in a nameof() below — the three places a name-only grep would accept and a
/// call-anchored, $scan-based, floor-of-two clause must not.
/// </summary>
public sealed class LiveRunObserver
{
    private readonly object _gate = new();
    private readonly Table _table;
    private readonly Dictionary<string, int> _rowByKey = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TaskNode> _tasks;

    public LiveRunObserver(IReadOnlyList<TaskNode> tasks)
    {
        _tasks = tasks;
        _table = new Table().Border(TableBorder.Rounded);
        _table.AddColumn("Task");
        _table.AddColumn("Status");
        _table.AddColumn("Detail");
        _table.AddColumn("Model");

        RebuildRows();
    }

    public static string ModelCell(string? model, string? requestedModel) =>
        model is null
            ? "[grey]—[/]"
            : Markup.Escape(AttemptModelSummary(model, requestedModel));

    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} — MISMATCH: the route requested {requestedModel}";

    public static string StatusMarkup(string outcome) => $"[green]{outcome}[/]";

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        string colour = requestedModel is null ? "grey" : "yellow";
        AnsiConsole.MarkupLine(
            $"[{colour}]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[{colour}]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]");

        // TODO: route this through ModelCell(...) and write it into cell 3 once the column is wired.
        AnsiConsole.MarkupLine($"[grey]note: {nameof(ModelCell)} is not wired into the row yet[/]");
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
                new Markup(string.Empty));
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
