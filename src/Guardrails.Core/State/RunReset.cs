using Guardrails.Core.Journal;
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
        DeleteDirectoryIfExists(Path.Combine(stateDir, "scope-baseline"));

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

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
