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
    /// <param name="junctionRoot">
    /// The short Windows worktree JUNCTION root allocated for THIS run (issue #383/#419), or null. Passed to
    /// the provider as the effective root so the adopted integration worktree's cwd is RE-ALIASED short — git
    /// canonicalized the junction away, so <c>WorktreeForBranch</c> returns the real (long) path, and this
    /// keeps the terminal-gate / plan-guardrail whole-repo cwd short on resume exactly like a fresh run's.
    /// </param>
    /// <param name="worktreeMode">
    /// The run's ONE worktree-mode resolution (issue #596), threaded from the caller that owns the run so
    /// this phase reads the SAME answer the provider wiring, the junction setup and the journaled
    /// effective <c>maxParallelism</c> read. Null (a standalone revalidate, which owns no run) ⇒ resolved
    /// here.
    /// </param>
    public static string Resolve(
        PlanDefinition plan, CancellationToken cancellationToken, string? junctionRoot = null,
        WorktreeModeResolution? worktreeMode = null)
    {
        if (!(worktreeMode ?? SchedulerFactory.ResolveWorktreeMode(plan)).Enabled)
        {
            return plan.Workspace;
        }

        string realRoot = SchedulerFactory.WorktreeRootFor(plan);
        string effectiveRoot = junctionRoot is { Length: > 0 } link ? link : realRoot;
        var worktreeProvider = new GitWorktreeProvider(plan.Workspace, effectiveRoot, realRoot);
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle integ = worktreeProvider.CreateIntegration(
            planName: Path.GetFileName(plan.PlanDirectory),
            runId: runId,
            cancellationToken);

        return integ.IntegrationWorktreePath;
    }
}
