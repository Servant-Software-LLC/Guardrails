using Guardrails.Cli.Commands;
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

    public void WaveStarting(WaveNode wave, int index, int total)
    {
        lock (_gate)
        {
            _output.WriteLine($"===== Wave {index}/{total}: {wave.Dir} — {wave.Tasks.Count} task(s) =====");

            // Regenerate the wave-scoped diagram so it reflects the now-authored tasks before
            // execution begins (issue #359). Best-effort: failures are swallowed and never change
            // the run outcome or obscure the wave banner. Uses plain-URI fallback since output
            // may be redirected (CI, --no-ui). The same render fires at the JIT checkpoint in
            // RunCommand.PrintWaveHalt.
            if (GraphCommand.RenderWaveScoped(wave.Directory, TextWriter.Null))
            {
                string diagramHtml = Path.Combine(wave.Directory, "diagram.html");
                bool linkable = !Console.IsOutputRedirected;
                string link = RunCommand.Hyperlink(diagramHtml, linkable);
                _output.WriteLine($"  Wave diagram (focused): {link}");
            }
        }
    }

    public void WaveFinished(WaveNode wave, Core.Journal.WaveStatus status, bool skipped)
    {
        lock (_gate)
        {
            string verb = skipped
                ? "already complete — skipped (resume)"
                : status == Core.Journal.WaveStatus.Completed ? "completed" : $"halted ({status.ToString().ToLowerInvariant()})";
            _output.WriteLine($"===== Wave {wave.Dir}: {verb} =====");
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
