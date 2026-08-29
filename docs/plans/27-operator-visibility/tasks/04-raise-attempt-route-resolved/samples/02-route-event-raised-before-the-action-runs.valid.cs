using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Representative CORRECT shape, reduced to the one attempt path this guardrail measures: the route
/// is resolved, the no-route branch settles and RETURNS, the route event is raised, and only THEN does
/// the action run. The attempt-model disclosure stays exactly where it was — after the action, folded
/// from what the runner reported — because the two events answer different questions at different
/// times.
/// </summary>
internal sealed class TaskExecutorSample
{
    private readonly IRunObserver _observer = IRunObserver.Null;
    private readonly IActionRunner _actionRunner = null!;
    private readonly AttemptJournaler _journaler = null!;

    private async Task<AttemptRecord> RunAttemptAsync(TaskNode task, int attemptNumber, WorktreeHandle worktree)
    {
        string logDir = AttemptLogDir(task.Id, attemptNumber);

        // THE attempt-launch resolution — once, immediately before this attempt launches.
        TierResolution? route = ResolveRoute(task);
        AttemptProvenance? provenance = BuildProvenance(task, worktree, route);
        WriteRouteDisclosure(logDir, attemptNumber, route, provenance);

        // §6.2's no-route outcome settles HERE, above everything, and RETURNS.
        if (route is { NoRoute: true })
        {
            return _journaler.NoRoute(task, attemptNumber, logDir, provenance, NoRouteReason(route));
        }

        // #524 / design §4.3: the ROUTE is resolved and the attempt is about to launch. Raised INLINE
        // here — not extracted into a helper — because this is the moment the disclosure is about, and
        // because every other `_observer.` raise in this file is written at its own site.
        //
        // The guard is the nullable contract, not defensiveness: Directory.Build.props sets
        // Nullable=enable with TreatWarningsAsErrors=true, and both `route.RunnerName` and
        // `provenance.Model` are `string?`. With nothing to name there is no route to disclose, so the
        // consuming surface keeps whatever it had rather than being handed an invented fact.
        // `route.Climbed` is the climb flag and its only correct source: re-deriving it by comparing
        // Tier to RequestedTier would be a second copy of a predicate the resolver already owns.
        if (route is { RunnerName: { } runnerName } && provenance?.Model is { } routeModel)
        {
            _observer.AttemptRouteResolved(
                task, attemptNumber, runnerName, routeModel,
                route.Tier, route.Climbed ? route.RequestedTier : null);
        }

        ActionRun action = await _actionRunner.RunAsync(task, attemptNumber, logDir);

        // #349, unchanged and still AFTER the action: this one carries BEST-KNOWN-ACTUAL, which the
        // runner cannot report until it has run. The new event announced the route; this confirms or
        // corrects it.
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
