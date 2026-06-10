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
