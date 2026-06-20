using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Executes a single task through its full attempt lifecycle (action → guardrails →
/// merge, with retries). The seam between the <see cref="Scheduler"/> (which owns the
/// DAG, parallelism, and blocking) and the per-task machinery — fake this to unit-test
/// scheduling without processes.
/// </summary>
public interface ITaskExecutor
{
    /// <summary>
    /// Run <paramref name="task"/> to a terminal result. Implementations own all journal
    /// transitions for the task except <c>blocked</c> (the scheduler's call).
    /// <paramref name="worktree"/> identifies the isolated segment the executor may scope all
    /// writes to (M2+); implementations that predate worktrees may ignore it.
    /// </summary>
    Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken);
}
