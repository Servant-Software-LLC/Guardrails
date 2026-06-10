using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Plain line-by-line console progress for a serial run (no Spectre until M4). Writes
/// task starts, guardrail outcomes, task results, and the loud plan-hash-mismatch warning.
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
        Console.WriteLine($"[{RunCommandLabel(result.Outcome)}] {result.TaskId} — {result.Summary}");
        Console.WriteLine();
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("WARNING: plan manifests changed since the last run.");
        Console.WriteLine($"  previous planHash: {previousPlanHash}");
        Console.WriteLine("  Resuming anyway — completed tasks are still treated as done.");
        Console.WriteLine("  Run 'guardrails run --fresh' to re-run from a clean slate.");
        Console.WriteLine("================================================================");
        Console.WriteLine();
    }

    private static string RunCommandLabel(TaskOutcome outcome) => Commands.RunCommand.StatusLabel(outcome);
}
