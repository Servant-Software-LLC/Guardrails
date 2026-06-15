using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Plain line-by-line console progress — the fallback when the terminal is not
/// interactive (redirected output, CI) or <c>--no-ui</c> is passed. Thread-safe: M4
/// workers emit events concurrently, so every write is serialized through one gate.
/// Writes to the injected <see cref="TextWriter"/> (the CLI's <see cref="IConsoleIo.Out"/>),
/// never the process-global console, so output-capturing tests do not race.
/// </summary>
public sealed class ConsoleRunObserver : IRunObserver
{
    private readonly object _gate = new();
    private readonly TextWriter _output;

    public ConsoleRunObserver(TextWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public void TaskStarting(TaskNode task)
    {
        lock (_gate)
        {
            _output.WriteLine($"[task] {task.Id}: {task.Description}");
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
            _output.WriteLine($"[retry] {task.Id}: attempt {attempt}/{budget}");
        }
    }

    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        lock (_gate)
        {
            _output.WriteLine(result.Passed
                ? $"  [guardrail] {task.Id} / {result.Name}: PASS"
                : $"  [guardrail] {task.Id} / {result.Name}: FAIL — {result.Reason}");
        }
    }

    public void TaskFinished(TaskResult result)
    {
        lock (_gate)
        {
            _output.WriteLine($"[{Commands.RunCommand.StatusLabel(result.Outcome)}] {result.TaskId} — {result.Summary}");
            _output.WriteLine();
        }
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        lock (_gate)
        {
            _output.WriteLine("================================================================");
            _output.WriteLine("WARNING: plan manifests changed since the last run.");
            _output.WriteLine($"  previous planHash: {previousPlanHash}");
            _output.WriteLine("  Resuming anyway — completed tasks are still treated as done.");
            _output.WriteLine("  Run 'guardrails run --fresh' to re-run from a clean slate.");
            _output.WriteLine("================================================================");
            _output.WriteLine();
        }
    }
}
