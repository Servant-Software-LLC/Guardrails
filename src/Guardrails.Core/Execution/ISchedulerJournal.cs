namespace Guardrails.Core.Execution;

/// <summary>
/// The minimal journal surface the <see cref="Scheduler"/> needs: resume skips
/// (<see cref="StatusOf"/>), blocking the transitive dependents of a halted task
/// (<see cref="MarkBlocked"/>), and the run's cumulative cost for the per-run cost cap
/// (<see cref="CurrentCostUsd"/>). All other journal transitions belong to
/// <see cref="TaskExecutor"/>. Faked in scheduler unit tests.
/// </summary>
public interface ISchedulerJournal
{
    /// <summary>The journaled status of a task (resume rules already applied on load).</summary>
    Journal.TaskStatus StatusOf(string taskId);

    /// <summary>Mark a task blocked because an upstream dependency cannot succeed.</summary>
    void MarkBlocked(string taskId);

    /// <summary>
    /// The run's cumulative journaled cost in USD, used to enforce the per-run cost cap
    /// (<see cref="Model.RunConfig.MaxCostUsd"/>). Defaults to 0 — a journal that records no cost
    /// never trips a cap, so existing implementations need not change; <see cref="Journal.RunJournal"/>
    /// overrides it with the real total (<see cref="Journal.JournalCost.Total"/>).
    /// </summary>
    decimal CurrentCostUsd() => 0m;
}
