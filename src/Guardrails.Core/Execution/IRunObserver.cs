using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Receives progress events as a run proceeds. Keeps the <see cref="Scheduler"/> and
/// <see cref="TaskExecutor"/> free of any console/UI dependency; the CLI supplies a
/// plain-text or live (Spectre) implementation. Implementations MUST be thread-safe —
/// M4 workers emit events concurrently. A no-op default is available via <see cref="Null"/>.
/// </summary>
public interface IRunObserver
{
    /// <summary>A task is about to run its action.</summary>
    void TaskStarting(TaskNode task);

    /// <summary>
    /// An attempt is starting: <paramref name="attempt"/> of <paramref name="budget"/>
    /// for this run (1-based; budget = 1 + retries).
    /// </summary>
    void AttemptStarting(TaskNode task, int attempt, int budget) { }

    /// <summary>A task finished (succeeded, failed, or was blocked).</summary>
    void TaskFinished(TaskResult result);

    /// <summary>A guardrail finished running.</summary>
    void GuardrailFinished(TaskNode task, GuardrailResult result);

    /// <summary>
    /// The resumed journal's plan hash differs from the current plan's — the manifests
    /// changed since the journal was written. The run continues (SSOT §7); this is a loud
    /// warning, not an error.
    /// </summary>
    void PlanHashMismatch(string previousPlanHash) { }

    /// <summary>
    /// A plan requested <paramref name="requested"/>-way parallelism but no worktree provider
    /// was available, so the scheduler clamped to serial (shared-workspace) execution to avoid
    /// an unsafe shared-workspace race (plan 08 §1 / F7). The run continues serially; this is a
    /// loud demotion notice, not an error. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void ParallelismClampedNoProvider(int requested) { }

    /// <summary>
    /// A best-effort cleanup operation (a segment-worktree <c>Discard</c> or <c>PruneOrphans</c>)
    /// failed during the M2 end-of-run sweep or a failed-task Discard. <paramref name="owner"/> is
    /// the owning task id (or a sentinel like <c>(prune-orphans)</c>). The run's verdict is
    /// unaffected — a cleanup failure must never flip a green run off-green (plan 08 §D / #126).
    /// Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void CleanupFailed(string owner, Exception error) { }

    /// <summary>
    /// A task's prompt action hit a TRANSIENT, retryable infrastructure condition (an HTTP 429/503/529,
    /// "overloaded", or a usage/session/rate limit) and the harness is PAUSING before re-running the
    /// same attempt — WITHOUT consuming the retry budget (SSOT §9, issue #115). <paramref name="reason"/>
    /// is the operator-facing cause (carrying a reset hint like "resets 11:20am" when one was present),
    /// <paramref name="backoff"/> is how long this pause waits, and <paramref name="pauseCount"/> is the
    /// 1-based pause number for this task. A distinct signal so an operator sees a HEALTHY task waiting
    /// out a rate limit, not a failing one. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) { }

    /// <summary>An observer that does nothing.</summary>
    static IRunObserver Null { get; } = new NullObserver();

    private sealed class NullObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }
    }
}
