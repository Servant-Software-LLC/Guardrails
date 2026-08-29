using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// MUTANT M2. The raise lives in an unrelated private method DEFINED EARLIER IN THE FILE and never
/// reached from the attempt path. It compiles, it sits textually above _actionRunner.RunAsync, and no
/// attempt ever raises it. This is the residual the positional clause itself declared.
/// </summary>
internal sealed class TaskExecutorSample
{
    private readonly IRunObserver _observer = IRunObserver.Null;
    private readonly IActionRunner _actionRunner = null!;
    private readonly AttemptJournaler _journaler = null!;

    private void ReplayRouteForDiagnostics(TaskNode task, int attemptNumber, TierResolution route, AttemptProvenance provenance)
    {
        if (route is { RunnerName: { } runnerName } && provenance.Model is { } routeModel)
        {
            _observer.AttemptRouteResolved(
                task, attemptNumber, runnerName, routeModel,
                route.Tier, route.Climbed ? route.RequestedTier : null);
        }
    }

    private async Task<AttemptRecord> RunAttemptAsync(TaskNode task, int attemptNumber, WorktreeHandle worktree)
    {
        string logDir = AttemptLogDir(task.Id, attemptNumber);

        TierResolution? route = ResolveRoute(task);
        AttemptProvenance? provenance = BuildProvenance(task, worktree, route);
        WriteRouteDisclosure(logDir, attemptNumber, route, provenance);

        if (route is { NoRoute: true })
        {
            return _journaler.NoRoute(task, attemptNumber, logDir, provenance, NoRouteReason(route));
        }

        ActionRun action = await _actionRunner.RunAsync(task, attemptNumber, logDir);

        if (provenance is { } launched && action.ObservedModel is { } observedModel)
        {
            provenance = Fold(launched, observedModel);
            _observer.AttemptModelResolved(task, attemptNumber, observedModel, provenance.RequestedModel);
        }

        return _journaler.Succeeded(task, attemptNumber, logDir, provenance);
    }

    private static string AttemptLogDir(string taskId, int attempt) => $"logs/{taskId}/attempt-{attempt}";

    private static TierResolution? ResolveRoute(TaskNode task) =>
        task.Action.Kind == ActionKind.Prompt ? new TierResolution() : null;

    private static AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree, TierResolution? route) =>
        route is null ? null : new AttemptProvenance { Model = route.Model, Runner = route.RunnerName };

    private static void WriteRouteDisclosure(string logDir, int attempt, TierResolution? route, AttemptProvenance? p) { }

    private static string NoRouteReason(TierResolution route) => "no route";

    private static AttemptProvenance Fold(AttemptProvenance launched, string observedModel) => launched;
}
