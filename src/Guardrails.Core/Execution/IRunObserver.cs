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

    /// <summary>
    /// Phase-2 scope-clean (SSOT §3.4, issue #280): after a <c>writeScope</c> task's guardrails PASSED,
    /// the harness re-computed the out-of-scope changed paths a passing guardrail left as side effects
    /// (an <c>npm ci</c>, a build cache, a generated <c>dist/</c>) and STRIPPED them from the segment
    /// before the commit, so the commit carries exactly the in-scope diff. This is NOT a failure — the
    /// paths are echoed for diagnosability (the #253 "don't silently vanish files" posture), never
    /// punished. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) { }

    /// <summary>
    /// An autonomy-policy decision was recorded to the unified <c>decisions[]</c> log (SSOT §2.1/§7): the
    /// <paramref name="entry"/> carries the <c>boundary</c> (M1 emits only <c>drift</c> — a safe
    /// definition-drift auto-resolved at the pre-DAG gate, §7.2), the policy in force, how it resolved, and
    /// a human headline/subject. Surfaced under the live task table and the static log site so an operator
    /// sees exactly what a decision did — the same payload the durable <c>decisions[]</c> journal section
    /// records. NOT a failure (an auto-resolved drift then proceeds and returns the normal exit code).
    /// Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void DecisionRecorded(DecisionEntry entry) { }

    /// <summary>
    /// A WAVED plan's wave <paramref name="wave"/> (the <paramref name="index"/>-th of
    /// <paramref name="total"/>, 1-based) is about to run its DAG drain (SSOT §14.4). The harness runs
    /// waves in strict order behind a hard barrier; this lets the UI retitle/segment the task table per
    /// wave. Default no-op so non-CLI observers and FLAT plans (never emitted) need not handle it.
    /// </summary>
    void WaveStarting(Model.WaveNode wave, int index, int total) { }

    /// <summary>
    /// A WAVED plan's wave <paramref name="wave"/> finished (SSOT §14.4/§14.6): <paramref name="status"/>
    /// is <c>completed</c> (drained green + exit gate passed), or a halt state (<c>needs-human</c>/
    /// <c>blocked</c>) when the barrier stopped the run. <paramref name="skipped"/> is true when the wave
    /// was already complete on resume and was skipped without running (SSOT §14.6). Default no-op.
    /// </summary>
    void WaveFinished(Model.WaveNode wave, Journal.WaveStatus status, bool skipped) { }

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
