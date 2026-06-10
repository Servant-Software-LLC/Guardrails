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
    /// the <c>logs/</c> tree and <c>merge-conflicts.log</c>, then re-seed <c>state.json</c>
    /// from <c>seed.json</c> (or <c>{}</c>). The committed <c>seed.json</c> and task folders
    /// are left untouched.
    /// </summary>
    public static void Fresh(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        string stateDir = Path.Combine(planDirectory, "state");

        DeleteFileIfExists(Path.Combine(stateDir, "run.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "state.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "merge-conflicts.log"));
        DeleteDirectoryIfExists(Path.Combine(stateDir, "logs"));

        // Re-seed immediately so a subsequent run starts from the seed-derived state.
        new StateManager(planDirectory).Initialize();
    }

    /// <summary>
    /// Reset a single task to <c>pending</c> (keeping attempt history), so the next
    /// <c>run</c> re-executes just that task. Returns false if the task is unknown to the
    /// journal (the caller reports it).
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
