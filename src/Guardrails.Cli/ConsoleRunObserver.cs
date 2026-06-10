using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Plain line-by-line console progress for a serial run (no Spectre until M4). Writes
/// task starts, guardrail outcomes, and task results as they happen.
/// </summary>
public sealed class ConsoleRunObserver : IRunObserver
{
    public void TaskStarting(TaskNode task) =>
        Console.WriteLine($"[task] {task.Id}: {task.Description}");

    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        if (result.Passed)
        {
            Console.WriteLine($"  [guardrail] {result.Name}: PASS");
        }
        else
        {
            Console.WriteLine($"  [guardrail] {result.Name}: FAIL — {result.Reason}");
        }
    }

    public void TaskFinished(TaskResult result)
    {
        string label = result.Outcome switch
        {
            TaskOutcome.Succeeded => "OK",
            TaskOutcome.ActionFailed => "ACTION FAILED",
            TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
            TaskOutcome.Blocked => "BLOCKED",
            _ => result.Outcome.ToString()
        };

        Console.WriteLine($"[{label}] {result.TaskId} — {result.Summary}");
        Console.WriteLine();
    }
}
