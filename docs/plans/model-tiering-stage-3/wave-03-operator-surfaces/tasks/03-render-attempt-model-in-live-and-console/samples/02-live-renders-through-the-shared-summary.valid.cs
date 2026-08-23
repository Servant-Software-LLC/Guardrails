// VALID sample for guardrails/02-live-renders-through-the-shared-summary.ps1 -> must exit 0.
// A representative correct LiveRunObserver: the member is DECLARED, and the rendered markup is BUILT
// FROM the shared AttemptModelSummary formatter, under the gate, with the harness strings escaped -
// the same idiom VerifierAdvisoryFound and OverwatchNoVerdict already use in the real file.
using Spectre.Console;

namespace Guardrails.Cli.Ui;

public sealed class LiveRunObserver : IRunObserver, IAsyncDisposable
{
    private readonly object _gate = new();

    /// <summary>
    /// The ONE wording both operator surfaces render. Public and pure because a Spectre write inside an
    /// active Live region is unobservable from a headless test, so the formatter is the only test seam.
    /// Mentions AttemptModelResolved in prose deliberately - the guardrail strips comments first.
    /// </summary>
    public static string AttemptModelSummary(string model, string? requestedModel) =>
        requestedModel is null
            ? model
            : $"{model} (route requested {requestedModel})";

    /// <summary>The attempt's best-known-actual model, and the requested one when they disagree.</summary>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        lock (_gate)
        {
            AnsiConsole.MarkupLine(
                $"[grey]model[/] [grey]{Markup.Escape(task.Id)}[/] attempt {attempt}: " +
                Markup.Escape(AttemptModelSummary(model, requestedModel)));
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
