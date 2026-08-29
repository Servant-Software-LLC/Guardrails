using Spectre.Console;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the launch-time route handler is DECLARED, its body is
/// NON-EMPTY, and it is INERT. It writes a console line and a TODO, and the Model cell is fed from
/// <c>AttemptModelResolved</c> — the post-action event — exactly as it would have been if
/// <c>AttemptRouteResolved</c> had never been added.
///
/// This is docs/plans/29-model-visibility-ux.md §1.1 shipped VERBATIM. AttemptModelResolved cannot fire
/// until the runner has reported what it ran on: MEASURED at 14m02s and longer per attempt on
/// docs/plans/24-plan-source-provenance/state/run.json. So the cell reads its <c>(medium)</c>
/// placeholder for the WHOLE attempt and fills in at the moment the row settles — precisely when the
/// operator no longer needs it live. That is the entire defect the launch event was introduced to fix,
/// and this file is what it looks like when the event is added and then not used.
///
/// It is committed because the guardrail's previous version PASSED it at exit 0. Everything else here
/// is right, and deliberately so, because the valid/invalid diff must be exactly the one defect:
///   * AddColumn(new TableColumn("Model").Width(8)) is present and appended LAST  → clause 1 passes;
///   * ModelCell is declared AND called twice (from the row seed and from the model event)
///                                                                                → clause 2 passes;
///   * AttemptRouteResolved is declared with a NON-EMPTY body                      → clause 3 passes;
///   * UpdateCell(row, 3, …) is present, so the cell really is written             → clause 5 passes.
/// Only clause 4 rejects it: <c>ModelCellFromRoute</c> is implemented (its agreement test is green) and
/// CALLED NOWHERE — one occurrence, its own declaration, against a floor of two. The seam exists for
/// exactly one caller, and that caller does not call it.
///
/// The name <c>ModelCellFromRoute</c> also appears in a comment and in a <c>nameof()</c> below, which
/// are the two places a name-only grep would accept and a $scan-based, call-anchored, floor-of-two
/// clause must not.
/// </summary>
public sealed class LiveRunObserver : IRunObserver
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
        _table.AddColumn(new TableColumn("Model").Width(8));

        RebuildRows();
    }

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

    // Implemented, and its agreement property test is green — it is simply never called. A test proving
    // this function correct says nothing about whether anything uses it (#120, at seam granularity).
    public static string ModelCellFromRoute(string runner, string? tier, string? requestedTier) =>
        ModelCell(runner, tier, climbed: requestedTier is not null, substituted: false, isScript: false);

    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} — MISMATCH: the route requested {requestedModel}";

    public static string StatusMarkup(string outcome) => $"[green]{outcome}[/]";

    /// <summary>
    /// THE DEFECT. Declared, non-empty, and it does nothing the cell can see. "Nothing to do here yet"
    /// was mistaken for "nothing this event is for".
    /// </summary>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)
    {
        AnsiConsole.MarkupLine(
            $"[grey]route[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[grey]{Markup.Escape(runner)} -> {Markup.Escape(model)}[/]");

        // TODO: feed the cell from here via ModelCellFromRoute(runner, tier, requestedTier).
        AnsiConsole.MarkupLine($"[grey]note: {nameof(ModelCellFromRoute)} is not wired to the row yet[/]");
    }

    /// <summary>
    /// The cell IS written — but only from here, after the action has already returned. Every clause
    /// about columns, calls and cell writes is satisfied, and the operator still stares at a placeholder
    /// for the entire attempt.
    /// </summary>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        string colour = requestedModel is null ? "grey" : "yellow";
        AnsiConsole.MarkupLine(
            $"[{colour}]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: "
            + $"[{colour}]{Markup.Escape(AttemptModelSummary(model, requestedModel))}[/]");

        WriteModelCell(
            task.Id,
            ModelCell(null, TierOf(task), climbed: false, substituted: requestedModel is not null,
                isScript: false));
    }

    private void WriteModelCell(string taskId, string cell)
    {
        lock (_gate)
        {
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
            _table.AddRow(
                new Markup(Markup.Escape(task.Id)),
                new Markup("[grey]pending[/]"),
                new Markup(string.Empty),
                new Markup(ModelCell(null, TierOf(task), climbed: false, substituted: false,
                    isScript: task.Action.Kind == ActionKind.Script)));
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
}
