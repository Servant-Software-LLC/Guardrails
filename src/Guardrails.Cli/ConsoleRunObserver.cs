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

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount)
    {
        lock (_gate)
        {
            // A HEALTHY task waiting out a transient limit — make it unmistakable from a failure so
            // the operator waits rather than debugging it (issue #115).
            _output.WriteLine(
                $"[paused] {task.Id}: transient — {reason}; backing off {(int)backoff.TotalSeconds}s " +
                $"(pause {pauseCount}); does NOT count against retries");
        }
    }

    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped)
    {
        lock (_gate)
        {
            // A passing guardrail's side effects (an `npm ci`, a build cache) cleaned so the commit
            // carries exactly the in-scope diff — NOT a failure (issue #280). Name them so a stripped
            // path is never a silent surprise (the #253 diagnosability posture).
            string paths = string.Join(", ", stripped.Select(o => $"{o.Status} {o.Path}"));
            _output.WriteLine(
                $"  [scope-clean] {task.Id}: stripped {stripped.Count} out-of-scope path(s) left by a " +
                $"passing guardrail (not a failure): {paths}");
        }
    }

    public void DecisionRecorded(DecisionEntry entry)
    {
        lock (_gate)
        {
            // An autonomy-policy decision (SSOT §2.1/§7). In M1 the only boundary is "drift" — a
            // provably-safe definition drift auto-resolved at the pre-DAG gate. Not a failure — the
            // pre-rendered headline says what happened; subject names the units.
            string subject = string.IsNullOrEmpty(entry.Subject) ? "" : $": {entry.Subject}";
            _output.WriteLine($"[decision:{entry.Boundary}] {entry.Headline}{subject}");
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
