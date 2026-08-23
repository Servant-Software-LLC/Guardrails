// INVALID sample for guardrails/02-live-renders-through-the-shared-summary.ps1 -> must exit NON-ZERO.
// The one defect the check exists to catch: the member IS declared - so the reflection test in
// AttemptModelRenderingTests goes green - but the live line is built from a SECOND, INLINED copy of the
// wording instead of the shared formatter. Character-identical to the console surface today, and free to
// drift from it tomorrow, which is the only moment the rule matters.
//
// The doc comment below deliberately NAMES AttemptModelSummary: if the guardrail did not strip comments
// first, this file would pass, and an agent that merely documented what it did not do would clear the
// check.
using Spectre.Console;

namespace Guardrails.Cli.Ui;

public sealed class LiveRunObserver : IRunObserver, IAsyncDisposable
{
    private readonly object _gate = new();

    /// <summary>The ONE wording both operator surfaces render.</summary>
    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} (route requested {requestedModel})";

    /// <summary>
    /// Renders the attempt's model. Should delegate to AttemptModelSummary(model, requestedModel);
    /// this copy does not, and that is the defect.
    /// </summary>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        lock (_gate)
        {
            AnsiConsole.MarkupLine(
                $"[grey]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: " +
                Markup.Escape(requestedModel is null ? model : model + " (route requested " + requestedModel + ")"));
        }
    }

    public void VerifierAdvisoryFound(string taskId, string finding)
    {
        lock (_gate)
        {
            AnsiConsole.MarkupLine($"[yellow]verifier advisory[/] {Markup.Escape(finding)}");
        }
    }
}
