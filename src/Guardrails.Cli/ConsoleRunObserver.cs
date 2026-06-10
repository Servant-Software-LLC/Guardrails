using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Plain line-by-line console progress — the fallback when the terminal is not
/// interactive (redirected output, CI) or <c>--no-ui</c> is passed. Thread-safe: M4
/// workers emit events concurrently, so every write is serialized through one gate.
/// </summary>
public sealed class ConsoleRunObserver : IRunObserver
{
    private readonly object _gate = new();

    public void TaskStarting(TaskNode task)
    {
        lock (_gate)
        {
            Console.WriteLine($"[task] {task.Id}: {task.Description}");
        }
    }

    public void AttemptStarting(TaskNode task, int attempt, int budget)
    {
        if (attempt == 1)
        {
            return; // first attempts are implied by TaskStarting; only retries are news
        }

        lock (_gate)
        {
            Console.WriteLine($"[retry] {task.Id}: attempt {attempt}/{budget}");
        }
    }

    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        lock (_gate)
        {
            Console.WriteLine(result.Passed
                ? $"  [guardrail] {task.Id} / {result.Name}: PASS"
                : $"  [guardrail] {task.Id} / {result.Name}: FAIL — {result.Reason}");
        }
    }

    public void TaskFinished(TaskResult result)
    {
        lock (_gate)
        {
            Console.WriteLine($"[{Commands.RunCommand.StatusLabel(result.Outcome)}] {result.TaskId} — {result.Summary}");
            Console.WriteLine();
        }
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        lock (_gate)
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("WARNING: plan manifests changed since the last run.");
            Console.WriteLine($"  previous planHash: {previousPlanHash}");
            Console.WriteLine("  Resuming anyway — completed tasks are still treated as done.");
            Console.WriteLine("  Run 'guardrails run --fresh' to re-run from a clean slate.");
            Console.WriteLine("================================================================");
            Console.WriteLine();
        }
    }
}
