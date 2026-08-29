using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the decorator DECLARES AttemptRouteResolved and never forwards
/// it to the inner observer. The disclosure stops here, silently, in every mode — and because the
/// interface member has a default no-op body, nothing in the build, the type system or any
/// declares-the-member reflection sweep can tell. This is the harder of the two shapes of the same
/// bug; the cheaper one (omit the member entirely) fails the SAME clause, because it too contains no
/// _inner.AttemptRouteResolved( call.
///
/// Note what this sample does NOT do wrong, so the valid/invalid diff is exactly the one defect: the
/// existing AttemptModelResolved forward is present and correct, every other member forwards, and the
/// parameter list of the new member matches the interface exactly. The name AttemptRouteResolved
/// appears FOUR times — in the doc comment above, in the member declaration, in a nameof(), and inside
/// an operator-facing message string — which are precisely the places a name-only or declaration-only
/// check would accept and a $scan-based, receiver-and-paren-anchored clause must not (#76 / #521).
/// </summary>
public sealed class OnTheFlyDecoratorSample : IRunObserver
{
    private readonly IRunObserver _inner;

    public OnTheFlyDecoratorSample(IRunObserver inner) => _inner = inner;

    public void TaskStarting(TaskNode task) => _inner.TaskStarting(task);

    public void TaskFinished(TaskResult result) => _inner.TaskFinished(result);

    public void GuardrailFinished(TaskNode task, GuardrailResult result) => _inner.GuardrailFinished(task, result);

    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    public void VerifierAdvisoryFound(string taskId, string finding) => _inner.VerifierAdvisoryFound(taskId, finding);

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        _inner.AttemptModelResolved(task, attempt, model, requestedModel);

    // #524: declared so the type "handles" the event. THE DEFECT — nothing is passed on. The route an
    // attempt took is not a shape of the DAG, so there is nothing for this decorator to do with it,
    // and "nothing to do" was mistaken for "nothing to forward".
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)
    {
        Trace($"{nameof(AttemptRouteResolved)} seen for {task.Id}");
        Trace("would have called _inner.AttemptRouteResolved( here");
    }

    public void OverwatchNoVerdict(string taskId, string reason) => _inner.OverwatchNoVerdict(taskId, reason);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);

    private static void Trace(string message) { }
}
