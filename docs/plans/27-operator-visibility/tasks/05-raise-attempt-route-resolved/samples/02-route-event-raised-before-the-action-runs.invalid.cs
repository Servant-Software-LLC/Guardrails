using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the route event is raised AFTER `_actionRunner.RunAsync`
/// returns. Everything else is right — the member exists, the guard is correct, the arguments are
/// correct, `route.Climbed` is the climb source, and both decorators (not shown) forward it. And it
/// delivers exactly nothing: it is AttemptModelResolved wearing a different name, so the live Model
/// cell still reads its placeholder for the whole attempt — MEASURED at 14m02s and longer on
/// docs/plans/24-plan-source-provenance/state/run.json. Presence is not timing.
///
/// Note what this sample does NOT do wrong, so the valid/invalid diff is exactly the one defect: the
/// no-route branch still returns first, the attempt-model raise is still present and unchanged, and
/// the name `AttemptRouteResolved` appears THREE times above the RunAsync call — in a doc comment, in
/// a `nameof(...)`, and inside an operator-facing message string. Those are the three places a
/// position-only or name-only check would accept, and a $scan-based, `_observer.`-and-paren-anchored
/// clause must not (#76 / #521).
/// </summary>
internal sealed class TaskExecutorSample
{
    private readonly IRunObserver _observer = IRunObserver.Null;
    private readonly IActionRunner _actionRunner = null!;
    private readonly AttemptJournaler _journaler = null!;

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

        // The route is settled here; _observer.AttemptRouteResolved( is raised further down, once we
        // know how the attempt went. (A comment is not a call.)
        Trace(nameof(IRunObserver.AttemptRouteResolved));
        Trace("about to launch; will raise _observer.AttemptRouteResolved( shortly");

        ActionRun action = await _actionRunner.RunAsync(task, attemptNumber, logDir);

        if (provenance is { } launched && action.ObservedModel is { } observedModel)
        {
            provenance = Fold(launched, observedModel);
            _observer.AttemptModelResolved(task, attemptNumber, observedModel, provenance.RequestedModel);
        }

        // THE DEFECT: raised only now, beside the attempt-model disclosure it was supposed to precede.
        if (route is { RunnerName: { } runnerName } && provenance?.Model is { } routeModel)
        {
            _observer.AttemptRouteResolved(
                task, attemptNumber, runnerName, routeModel,
                route.Tier, route.Climbed ? route.RequestedTier : null);
        }

        return _journaler.Succeeded(task, attemptNumber, logDir, provenance);
    }

    private static void Trace(string message) { }

    private static string AttemptLogDir(string taskId, int attempt) => $"logs/{taskId}/attempt-{attempt}";

    private static TierResolution? ResolveRoute(TaskNode task) =>
        task.Action.Kind == ActionKind.Prompt ? new TierResolution() : null;

    private static AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree, TierResolution? route) =>
        route is null ? null : new AttemptProvenance { Model = route.Model, Runner = route.RunnerName };

    private static void WriteRouteDisclosure(string logDir, int attempt, TierResolution? route, AttemptProvenance? p) { }

    private static string NoRouteReason(TierResolution route) => "no route";

    private static AttemptProvenance Fold(AttemptProvenance launched, string observedModel) => launched;
}
