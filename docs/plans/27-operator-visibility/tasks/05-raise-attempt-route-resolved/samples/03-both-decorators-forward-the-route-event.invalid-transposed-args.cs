using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// MUTANT M3. The decorator DOES forward — with `runner` and `model` TRANSPOSED and `requestedTier`
/// hard-coded to null. It compiles (both are `string`), it satisfies a call-anchored
/// `_inner.AttemptRouteResolved(` clause, and it destroys the two things the event exists to carry:
/// the cell names the model id instead of the eight-character block, and the §6.2 climb signal —
/// whose PRESENCE is the signal — is gone for every attempt.
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

    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        _inner.AttemptRouteResolved(task, attempt, model, runner, tier, null);

    public void OverwatchNoVerdict(string taskId, string reason) => _inner.OverwatchNoVerdict(taskId, reason);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);
}
