using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Fake <see cref="ITaskExecutor"/> for the topology-wiring scheduler tests: records the worktree
/// path each task was assigned (so a test can see whether two tasks shared a directory — the reuse
/// lever) and returns a scripted outcome. Optionally fails specific tasks. No processes, no journal.
/// </summary>
public sealed class RecordingExecutor : ITaskExecutor
{
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    /// <summary>taskId → the WorktreePath the scheduler handed this task.</summary>
    public ConcurrentDictionary<string, string> AssignedPath { get; } = new(StringComparer.Ordinal);

    /// <summary>taskId → the TaskBase on the handle this task ran in (W-2 / reset assertions).</summary>
    public ConcurrentDictionary<string, string> AssignedTaskBase { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Started { get; } = [];

    public void FailTask(string id) => _failing.Add(id);

    public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
    {
        Started.Enqueue(task.Id);
        AssignedPath[task.Id] = worktree.WorktreePath;
        AssignedTaskBase[task.Id] = worktree.TaskBase;

        bool fails = _failing.Contains(task.Id);
        return Task.FromResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = fails ? TaskOutcome.GuardrailFailed : TaskOutcome.Succeeded,
            Summary = fails ? "scripted failure" : "scripted success"
        });
    }
}
