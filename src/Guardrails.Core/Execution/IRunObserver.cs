using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Receives line-by-line progress as a serial run proceeds. Keeps
/// <see cref="SerialExecutor"/> free of any console/UI dependency; the CLI supplies a
/// plain-text implementation. A no-op default is available via <see cref="Null"/>.
/// </summary>
public interface IRunObserver
{
    /// <summary>A task is about to run its action.</summary>
    void TaskStarting(TaskNode task);

    /// <summary>A task finished (succeeded, failed, or was blocked).</summary>
    void TaskFinished(TaskResult result);

    /// <summary>A guardrail finished running.</summary>
    void GuardrailFinished(TaskNode task, GuardrailResult result);

    /// <summary>An observer that does nothing.</summary>
    static IRunObserver Null { get; } = new NullObserver();

    private sealed class NullObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }
}
