using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// Representative CORRECT shape of ONE transparent decorator (either of the two ships this same
/// shape): the new launch-time route event is forwarded EXPLICITLY, beside the existing attempt-model
/// forward rather than instead of it, with every argument passed through verbatim.
/// </summary>
public sealed class OnTheFlyDecoratorSample : IRunObserver
{
    private readonly IRunObserver _inner;

    public OnTheFlyDecoratorSample(IRunObserver inner) => _inner = inner;

    public void TaskStarting(TaskNode task) => _inner.TaskStarting(task);

    public void TaskFinished(TaskResult result) => _inner.TaskFinished(result);

    public void GuardrailFinished(TaskNode task, GuardrailResult result) => _inner.GuardrailFinished(task, result);

    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    // #229 §6.5: forwarded EXPLICITLY. The interface default is an empty body, so a decorator that
    // simply omits this swallows the run-start advisory with no trace at all.
    public void VerifierAdvisoryFound(string taskId, string finding) => _inner.VerifierAdvisoryFound(taskId, finding);

    // #349: forwarded EXPLICITLY, and unchanged. The pair was folded ONCE at the attempt, so
    // re-deriving or reformatting it here would make this a second owner of the rule.
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        _inner.AttemptModelResolved(task, attempt, model, requestedModel);

    // #524: forwarded EXPLICITLY, for the same reason and with the same hazard. It is raised BEFORE
    // the action runs, which is the whole of its value — and this decorator is in BOTH the live and
    // the --no-ui chain, so an omission here hides the route from every operator in every mode. This
    // observer does not ACT on it: which block served an attempt is not a shape of the DAG, so it
    // forwards and nothing else. requestedTier is non-null ONLY on a §6.2 climb, so passing null
    // through AS null is what keeps its presence meaningful.
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        _inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier);

    public void OverwatchNoVerdict(string taskId, string reason) => _inner.OverwatchNoVerdict(taskId, reason);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);
}
