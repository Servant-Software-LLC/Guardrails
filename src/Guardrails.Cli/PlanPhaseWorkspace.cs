using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Shared workspace resolution for the whole-plan phases that evaluate guardrail-shaped checks OUTSIDE
/// the task DAG — <see cref="PlanPreflightPhase"/>, <see cref="PlanGuardrailPhase"/>, and the
/// <c>plan:preflights</c> / <c>plan:guardrails</c> synthetic-id revalidate paths in
/// <see cref="Commands.Revalidate"/>. Resolves to the integration worktree on the plan branch in
/// worktree mode, or the plan workspace directly in serial mode — mirroring exactly the condition
/// <see cref="SchedulerFactory.Create"/> wires a <see cref="GitWorktreeProvider"/> on.
/// <para>
/// Calling <see cref="GitWorktreeProvider.CreateIntegration"/> here is always safe, whether the caller
/// runs BEFORE the Scheduler's own run (the pre-DAG phase), AFTER it (the terminal phase, or a
/// standalone revalidate against an already-settled run), or with no run in this process at all: it is
/// IDEMPOTENT — it reuses a worktree already checked out on the plan branch rather than creating a
/// second one — so every caller resolves to the SAME on-disk worktree, which always reflects the plan
/// branch's CURRENT tip.
/// </para>
/// </summary>
internal static class PlanPhaseWorkspace
{
    public static string Resolve(PlanDefinition plan, CancellationToken cancellationToken)
    {
        if (!SchedulerFactory.WouldUseWorktreeMode(plan))
        {
            return plan.Workspace;
        }

        var worktreeProvider = new GitWorktreeProvider(plan.Workspace, SchedulerFactory.WorktreeRootFor(plan));
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle integ = worktreeProvider.CreateIntegration(
            planName: Path.GetFileName(plan.PlanDirectory),
            runId: runId,
            cancellationToken);

        return integ.IntegrationWorktreePath;
    }
}
