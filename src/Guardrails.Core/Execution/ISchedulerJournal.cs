namespace Guardrails.Core.Execution;

/// <summary>
/// The minimal journal surface the <see cref="Scheduler"/> needs: resume skips
/// (<see cref="StatusOf"/>) and blocking the transitive dependents of a halted task
/// (<see cref="MarkBlocked"/>). All other journal transitions belong to
/// <see cref="TaskExecutor"/>. Faked in scheduler unit tests.
/// </summary>
public interface ISchedulerJournal
{
    /// <summary>The journaled status of a task (resume rules already applied on load).</summary>
    Journal.TaskStatus StatusOf(string taskId);

    /// <summary>Mark a task blocked because an upstream dependency cannot succeed.</summary>
    void MarkBlocked(string taskId);
}
