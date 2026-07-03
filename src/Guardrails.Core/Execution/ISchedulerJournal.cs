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

    /// <summary>
    /// Reserve the next merge sequence number (advancing the counter). Default returns a dummy
    /// value for fakes that do not track sequences. <see cref="Journal.RunJournal"/> provides the
    /// real monotonic counter.
    /// </summary>
    long ReserveMergeSequence() => 0L;

    /// <summary>
    /// Record the terminal settle of a worktree task: update Status and optionally MergeSequence
    /// WITHOUT adding an AttemptRecord (the attempt was already recorded by the executor).
    /// Default is a no-op for fakes. <see cref="Journal.RunJournal"/> provides the real impl.
    /// </summary>
    void RecordSettle(string taskId, Journal.TaskStatus status, long? mergeSequence = null) { }

    /// <summary>
    /// Record the SUCCESSFUL settle of a worktree task (issue #196): append <paramref name="attempt"/>
    /// to the task's attempt list AND set Status + MergeSequence atomically. The worktree success path
    /// defers the attempt record to the settle (unlike serial mode, which records it inline), so
    /// without this the task would settle succeeded with an EMPTY <c>Attempts</c> list — contradicting
    /// SSOT §7, which shows a succeeded task with a populated <c>attempts[]</c>. Default delegates to
    /// <see cref="RecordSettle"/> for fakes that do not model attempts; <see cref="Journal.RunJournal"/>
    /// provides the real impl that also appends the attempt.
    /// </summary>
    void RecordSettleWithAttempt(
        string taskId, Journal.AttemptRecord attempt, Journal.TaskStatus status, long? mergeSequence = null) =>
        RecordSettle(taskId, status, mergeSequence);
}
