using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
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

    // The JIT breakdown's liveness heartbeat (issue #469). Under --no-ui the tailed log IS the record, and a
    // tail with no line for 30 minutes is the bug. One timer, created on Starting and disposed on Finished,
    // so the observer stays self-contained: no call-site change and no IDisposable on this type.
    private Timer? _breakdownTimer;
    private WaveBreakdownContext? _breakdownContext;
    private DateTimeOffset _breakdownSince;
    private bool _ceilingNoticed;

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

    public void AttemptFinished(TaskNode task, int attempt, Core.Journal.AttemptOutcome outcome)
    {
        lock (_gate)
        {
            // The per-attempt WHY (the gap between [retry] above and the task's final [outcome] line,
            // which fires only after the WHOLE retry loop settles): under --no-ui the tailed log IS the
            // record, so the reason a single attempt ended must be IN it.
            _output.WriteLine($"[attempt] {task.Id} attempt {attempt}: {outcome}");
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
            // The leading line is grep-anchored and CI-parsed — it stays VERBATIM.
            _output.WriteLine($"[{Commands.RunCommand.StatusLabel(result.Outcome)}] {result.TaskId} — {result.Summary}");
            if (ClaimLine(result.NeedsHumanKind) is { } claim)
            {
                _output.WriteLine(claim);
            }

            _output.WriteLine();
        }
    }

    /// <summary>
    /// The agent's needs-human classification as one indented line in this file's own <c>  [tag]</c> idiom
    /// (issue #485), or null when unclassified — in which case NOT ONE CHARACTER is added and the output is
    /// byte-identical to every pre-#485 run.
    /// <para>Not redundant with the run summary: under <c>--no-ui</c> a tailed CI log IS the record, and a
    /// run that aborts later never reaches <c>PrintSummary</c> at all.</para>
    /// <para>Public for the same reason <see cref="RunCommand.Hyperlink"/> is — the Cli assembly ships no
    /// <c>InternalsVisibleTo</c>, so a pure mapping method is the test seam.</para>
    /// </summary>
    public static string? ClaimLine(string? needsHumanKind) => NeedsHumanKinds.Parse(needsHumanKind) switch
    {
        NeedsHumanKinds.DefectiveGuardrail =>
            $"  [claim] {NeedsHumanKinds.DefectiveGuardrail} — the agent disputes the check, not the work (unverified)",
        NeedsHumanKinds.BlockedWork =>
            $"  [claim] {NeedsHumanKinds.BlockedWork} — the agent could not complete the work (unverified)",
        _ => null
    };

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

    public void WaveBreakdownStarting(WaveBreakdownContext context)
    {
        lock (_gate)
        {
            _breakdownContext = context;
            _breakdownSince = DateTimeOffset.UtcNow;
            _ceilingNoticed = false;

            // The wave banner WaveStarting cannot print here: it fires only AFTER the checkpoint, so on a
            // JIT plan this phase used to produce no line of any kind for up to half an hour (#469).
            _output.WriteLine(
                $"===== Wave {context.Index}/{context.Total}: {context.WaveDir} — JIT breakdown "
                + "(no tasks authored yet) =====");
            _output.WriteLine(
                $"[{BreakdownProgress.PlainTag}] {context.WaveDir}: authoring tasks; ceiling "
                + BreakdownProgress.FormatClock(context.Ceiling));
            _output.WriteLine($"[{BreakdownProgress.PlainTag}]   log dir: {context.BreakdownLogDir}");

            _breakdownTimer?.Dispose();
            var interval = TimeSpan.FromSeconds(BreakdownProgress.HeartbeatIntervalSeconds);
            _breakdownTimer = new Timer(_ => BreakdownHeartbeat(), null, interval, interval);
        }
    }

    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        WaveNode? authoredWave)
    {
        // Probe outside the gate — the settlement line reports what is on disk, not the last sample.
        BreakdownProgress.Snapshot snapshot = BreakdownProgress.Probe(
            context.TasksDirectory, context.StreamLogPath, context.IntentManifestPath, DateTimeOffset.UtcNow);

        Timer? stopped;
        lock (_gate)
        {
            stopped = _breakdownTimer;
            _breakdownTimer = null;
            _breakdownContext = null;
            _output.WriteLine(BreakdownProgress.PlainFinishLine(context.WaveDir, failureKind, elapsed, snapshot));
        }

        stopped?.Dispose(); // outside the gate — Dispose waits for an in-flight callback that wants the gate
    }

    /// <summary>
    /// One <see cref="BreakdownProgress.HeartbeatIntervalSeconds"/> heartbeat: the clock against the ceiling
    /// plus the same observed fragments the live table's phase row renders, so the two surfaces cannot report
    /// different counts. The 25-minute pre-announcement rides the first line past the threshold and fires
    /// ONCE — a countdown here would just be noise in a CI log.
    /// </summary>
    private void BreakdownHeartbeat()
    {
        WaveBreakdownContext? context;
        DateTimeOffset since;
        lock (_gate)
        {
            context = _breakdownContext;
            since = _breakdownSince;
        }

        if (context is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan elapsed = now - since;

        // Self-bound. A Ctrl+C propagates out of the invoker without ever reaching the Finished event, and
        // this observer is deliberately not IDisposable, so the heartbeat retires itself: no session can
        // outlive its own ceiling, and a stray [breakdown] line must not interleave with the exit summary.
        if (elapsed > context.Ceiling + TimeSpan.FromMinutes(1))
        {
            Timer? expired;
            lock (_gate)
            {
                expired = ReferenceEquals(_breakdownContext, context) ? _breakdownTimer : null;
                if (expired is not null)
                {
                    _breakdownTimer = null;
                    _breakdownContext = null;
                }
            }

            expired?.Dispose();
            return;
        }

        // Probing OUTSIDE the gate keeps filesystem latency off the event path, exactly as the live
        // observer's ticker does.
        BreakdownProgress.Snapshot snapshot = BreakdownProgress.Probe(
            context.TasksDirectory, context.StreamLogPath, context.IntentManifestPath, now);

        lock (_gate)
        {
            if (!ReferenceEquals(_breakdownContext, context))
            {
                return; // the phase settled while we were probing
            }

            string line = BreakdownProgress.PlainLine(context.WaveDir, elapsed, context.Ceiling, snapshot);
            if (!_ceilingNoticed
                && elapsed >= TimeSpan.FromMinutes(BreakdownProgress.CeilingNoticeMinutes)
                && elapsed < context.Ceiling)
            {
                _ceilingNoticed = true;
                line += $" (cut off at {BreakdownProgress.FormatClock(context.Ceiling)})";
            }

            _output.WriteLine(line);
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

    public void VerifierAdvisoryFound(string taskId, string finding)
    {
        lock (_gate)
        {
            // The DoR §6.5 run-start advisory (#229) — one line per affected task, before the DAG.
            // Tagged rather than banner-wrapped: §12.6 forbids a verifier condition from failing a
            // build, and a WARNING banner in the same stream that carries real failures costs the
            // operator a triage they do not owe. A run with no findings prints nothing here at all.
            _output.WriteLine($"[verifier-advisory] {taskId}: {finding}");
        }
    }

    public void OverwatchNoVerdict(string taskId, string reason)
    {
        lock (_gate)
        {
            // Issue #452 — the plain-output twin of the live line. Tagged like the other advisories
            // rather than banner-wrapped: the overwatcher gates nothing, so this is not a failure the
            // operator must triage. It exists so a supervisor that SPENT and produced nothing is
            // distinguishable from one that had nothing to report — previously it printed nothing at all.
            _output.WriteLine($"[overwatch] no verdict — {taskId}: {reason}");
        }
    }

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        lock (_gate)
        {
            // Issue #349 — the plain-output twin of the live line, tagged like the other per-event
            // disclosures rather than banner-wrapped: a model that agrees with the route is not a failure
            // to triage, and under --no-ui the tailed log IS the record, so it must still be IN it. The
            // wording is LiveRunObserver's shared formatter, not a second copy: one owner means the two
            // surfaces cannot report the same attempt differently, and the attempt number is here because
            // a retry may well have escalated to another model.
            _output.WriteLine(
                $"[model] {task.Id} attempt {attempt}: {LiveRunObserver.AttemptModelSummary(model, requestedModel)}");
        }
    }
}
