using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.State;

/// <summary>
/// Implements the two reset modes behind <c>guardrails reset</c> and <c>run --fresh</c>:
/// a full reset that wipes runtime state and re-seeds, and a single-task reset that pushes
/// one task back to <c>pending</c> while keeping its attempt history. Kept in Core (not the
/// CLI) so it is unit-testable and reusable by <c>--fresh</c>.
/// </summary>
public static class RunReset
{
    /// <summary>
    /// Full fresh reset (SSOT §6.1 / M3 scope): delete <c>run.json</c>, <c>state.json</c>,
    /// the <c>logs/</c> tree, <c>merge-conflicts.log</c> and the <c>captured/</c> baseline store,
    /// then re-seed <c>state.json</c> from <c>seed.json</c> (or <c>{}</c>). The committed
    /// <c>seed.json</c> and task folders are left untouched.
    /// </summary>
    /// <remarks>
    /// <c>captured/</c> (issue #51, the restore-on-retry baselines) MUST be wiped: a stale baseline
    /// surviving <c>--fresh</c> would revert a legitimately re-authored file on the next run, before
    /// any task re-snapshots its current bytes. It is harness-owned runtime state like the rest of
    /// <c>state/</c>, never committed.
    /// </remarks>
    public static void Fresh(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        string stateDir = Path.Combine(planDirectory, "state");

        DeleteFileIfExists(Path.Combine(stateDir, "run.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "state.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "merge-conflicts.log"));
        DeleteDirectoryIfExists(Path.Combine(stateDir, "logs"));
        DeleteDirectoryIfExists(Path.Combine(stateDir, "captured"));

        // F3: prune stale guardrails/<runId>/* segment+fork branches and their worktrees left
        // behind by a crashed worktree-mode run. State-file deletion alone never touches git refs,
        // so a crashed run's segment branches would survive --fresh. Best-effort: a non-git
        // workspace or a load failure must not abort the reset.
        PruneStaleWorktreesAndBranches(planDirectory);

        // Re-seed immediately so a subsequent run starts from the seed-derived state.
        new StateManager(planDirectory).Initialize();
    }

    /// <summary>
    /// Reset a single task to <c>pending</c> (keeping attempt history), so the next
    /// <c>run</c> re-executes just that task. Also clears that task's captured baseline subdir
    /// (issue #51) so a re-run re-snapshots the file's current bytes rather than reverting to a
    /// stale baseline. Returns false if the task is unknown to the journal (the caller reports it).
    /// </summary>
    public static bool Task(PlanDefinition plan, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        if (!File.Exists(RunJournal.PathFor(plan.PlanDirectory)))
        {
            return false;
        }

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        if (!journal.Document.Tasks.ContainsKey(taskId))
        {
            return false;
        }

        journal.ResetTask(taskId);
        DeleteDirectoryIfExists(Path.Combine(plan.PlanDirectory, "state", "captured", taskId));
        return true;
    }

    /// <summary>
    /// Load the plan (best-effort) to resolve its workspace + worktree root, then prune any stale
    /// <c>guardrails/&lt;runId&gt;/*</c> segment/fork branches and worktrees from the workspace repo.
    /// Swallows every failure (unloadable plan, non-git workspace) so <c>--fresh</c> never aborts.
    /// </summary>
    private static void PruneStaleWorktreesAndBranches(string planDirectory)
    {
        try
        {
            PlanLoadResult load = new PlanLoader().Load(planDirectory);
            if (load.Plan is not { } plan) return;

            GitWorktreeProvider.PruneStaleSegmentBranches(
                plan.Workspace, SchedulerFactory.WorktreeRootFor(plan));
        }
        catch
        {
            // Best-effort cleanup — a fresh reset must succeed even if the prune cannot run.
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Windows-safe (issue #109): the logs/ and captured/ trees never hold git objects today, but
    // the --fresh worktree-root wipe can; route every reset delete through SafeDelete so a future
    // git-bearing path (or a read-only file dropped by a tool) cannot abort the reset.
    private static void DeleteDirectoryIfExists(string path) => SafeDelete.DeleteDirectory(path);
}
