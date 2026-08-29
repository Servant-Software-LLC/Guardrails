using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Cli;

/// <summary>
/// The pre-DAG plan-preflight phase (preflights-impl deliverable 3, design 09-preflight-first-class,
/// SSOT §7). Evaluates <c>&lt;plan&gt;/preflights/</c> ONCE, before the Scheduler builds any wave,
/// against the run's STARTING bytes — the integration worktree on the plan branch at the user's HEAD
/// in worktree mode, or the plan workspace directly in serial mode — via the unconditional
/// <see cref="IReVerifier"/> seam (deliverable 1). Read-only: no task action ever runs here.
/// <para>
/// A plan with no <c>preflights/</c> folder (<see cref="PlanDefinition.PlanPreflights"/> empty) is
/// untouched: no evaluation, no <c>planPreflights</c> journal section written (SSOT §7 — the section is
/// additive and OMITTED, never null noise, for a plan that doesn't opt in).
/// </para>
/// <para>
/// <b>Resume SKIP (the B1 fix, SSOT §7).</b> When the journal already carries a
/// <c>planPreflights.status == "passed"</c> marker whose <c>planHash</c> matches the CURRENT plan hash,
/// the phase is skipped — the marker (and its <c>evaluatedAt</c>) is left byte-for-byte untouched. A
/// negative-baseline check (true only at the very start of a plan's lifecycle) must be evaluated exactly
/// ONCE across the whole run, or a resume after a mid-DAG crash would re-run it against
/// partially-merged bytes and false-halt a run that is actually fine. The phase re-evaluates only when
/// the marker is absent, its status is failed, or its planHash is stale — or after <c>--fresh</c>, which
/// deletes <c>run.json</c> (and so the marker) before this phase ever runs.
/// </para>
/// <para>
/// <b>Committed sample pairs (plan of record 26 §3/§7, issue #510).</b> BEFORE either short-circuit above,
/// every <c>tasks/&lt;id&gt;/samples/</c> pair is executed against its guardrail through the SHARED
/// <see cref="SampleVerifier"/> — the same type <c>guardrails samples verify</c> drives, so the verb and
/// this phase can never disagree about whether a pair is sound. Any finding halts the run here, before the
/// Scheduler builds a wave and before any task spends a token.
/// </para>
/// </summary>
public static class PlanPreflightPhase
{
    /// <summary>
    /// Per-sample wall clock, matching <c>guardrails samples verify</c>. A guardrail that hangs on a sample
    /// yields no usable exit code, so the verifier reports it rather than treating it as a silent pass.
    /// </summary>
    private static readonly TimeSpan PerSampleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Evaluate (or skip) the pre-DAG phase for <paramref name="plan"/>, whose journal
    /// <paramref name="journal"/> was just loaded/seeded by <see cref="RunJournal.LoadOrCreate"/>.
    /// Returns true when scheduling may proceed (passed, skipped, or no preflights declared at all);
    /// false when the run must halt BEFORE any task is scheduled — a failed <c>planPreflights</c>
    /// section (with per-check reasons) has already been journaled by the time this returns.
    /// <para>
    /// When <paramref name="heartbeatOut"/> is supplied, a per-guardrail wall-clock heartbeat (issue
    /// #331) is written to it while each Full Flight Check runs. This phase runs BEFORE the Spectre live
    /// region is constructed, so plain heartbeat lines are #145-safe. Null ⇒ no heartbeat.
    /// </para>
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        RunJournal journal,
        ProcessRunner processRunner,
        TextWriter? heartbeatOut,
        CancellationToken cancellationToken,
        string? junctionRoot = null)
    {
        // Committed sample pairs come FIRST — before BOTH short-circuits below — and this placement is
        // the whole point of the step (plan of record 26 §3/§7, issue #510).
        //
        //  * After `PlanPreflights.Count == 0` it would gate only the plans that already opted into Full
        //    Flight Checks, i.e. the plans least likely to need it, while most plans in the repo declare
        //    no preflights/ folder at all and would keep a reversed pair indistinguishable from a sound one.
        //  * After the B1 resume SKIP it would be skippable through the resume door. That marker exists
        //    because a NEGATIVE-BASELINE check is true only at the very start of a plan's lifecycle, so
        //    re-running it against partially-merged bytes would false-halt a healthy run. Samples are plan
        //    INPUTS, not run outputs — re-verifying them mid-run can never false-halt — so the reasoning
        //    does not transfer, and skipping them would reintroduce "recorded but never executed".
        //
        // Cost, which §7 states as a CONDITION rather than a preference: the verifier DISCOVERS pairs
        // before it runs anything, so a plan carrying none pays one Directory.Exists per task and launches
        // no process. That path returns true below having written nothing and printed nothing — byte-
        // identical to the behaviour before this step existed.
        if (!await SamplePairsPassAsync(plan, journal, processRunner, heartbeatOut, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        if (plan.PlanPreflights.Count == 0)
        {
            // No <plan>/preflights/ folder at all — the feature is not in use for this plan. Additive
            // per SSOT §7: omit the section entirely, never write a vacuous "passed" marker.
            return true;
        }

        string currentHash = journal.Document.PlanHash;

        if (journal.Document.PlanPreflights is { } marker
            && marker.Status == PlanPhaseStatus.Passed
            && string.Equals(marker.PlanHash, currentHash, StringComparison.Ordinal))
        {
            return true;
        }

        string evalWorkspace = PlanPhaseWorkspace.Resolve(plan, cancellationToken, junctionRoot);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        var reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        using GuardrailHeartbeat? heartbeat = heartbeatOut is null ? null : GuardrailHeartbeat.StartConsole(heartbeatOut);

        // Issue #432: capture each Full Flight Check's stdout/stderr under logs/<runId>/preflights/<name>/.
        // A failing check halts the run before ANY task is scheduled, so there is no attempt dir anywhere —
        // without this the only trace of WHY is the operator's scrollback.
        string runId = journal.Document.RunId;
        string? artifactDir = GateArtifacts.DirectoryFor(
            plan.PlanDirectory, runId, waveDir: null, GateArtifacts.PreflightsFolder);
        string? relativeLogDir = GateArtifacts.RelativeDirectoryFor(
            runId, waveDir: null, GateArtifacts.PreflightsFolder);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(
                evalWorkspace,
                plan.PlanPreflights,
                new ReVerifyOptions { Progress = heartbeat, ArtifactDirectory = artifactDir },
                cancellationToken)
            .ConfigureAwait(false);

        List<PlanPreflightCheck> checks = plan.PlanPreflights
            .Select(g =>
            {
                GuardrailResult? failure = result.FailedGuardrails
                    .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
                return new PlanPreflightCheck
                {
                    Name = g.Name,
                    Passed = failure is null,
                    Reason = failure?.Reason
                };
            })
            .ToList();

        var section = new PlanPreflightsSection
        {
            Status = result.Passed ? PlanPhaseStatus.Passed : PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = currentHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            LogDir = relativeLogDir
        };

        // Issue #432: on FAILURE also record the uniform top-level `halt` — the one field a post-mortem
        // reader can consult without knowing the four-folder model, so a halted run's journal never reads
        // as a wall of silent pending tasks.
        RunHalt? halt = result.Passed ? null : BuildHalt(result, relativeLogDir);

        PlanPhaseJournalWriter.Update(plan.PlanDirectory, document => halt is null
            ? document with { PlanPreflights = section }
            : document with { PlanPreflights = section, Halt = halt });

        return result.Passed;
    }

    /// <summary>
    /// Execute every committed <c>tasks/&lt;id&gt;/samples/</c> pair against its guardrail and return whether
    /// scheduling may proceed. True — with NOTHING journaled and NOTHING printed — both when the plan carries
    /// no pairs at all (the overwhelmingly common case) and when every pair it does carry is sound. False,
    /// with the failure recorded in the phase's existing shapes, as soon as one finding is reported.
    /// </summary>
    private static async Task<bool> SamplePairsPassAsync(
        PlanDefinition plan,
        RunJournal journal,
        ProcessRunner processRunner,
        TextWriter? consoleOut,
        CancellationToken cancellationToken)
    {
        SampleVerifyResult result = await SampleVerifier
            .VerifyAsync(plan, processRunner, PerSampleTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.Passed)
        {
            // Nothing to verify, or nothing wrong: no journal section, no console line, no marker touched.
            return true;
        }

        List<PlanPreflightCheck> checks = result.Findings
            .Select(f => new PlanPreflightCheck
            {
                // NAME the offending pair (issue #432). A pre-DAG halt settles no task, so tasks{} is a wall
                // of silent `pending` entries; "a sample pair failed" cannot tell a post-mortem reader which
                // of a plan's pairs to open.
                Name = SampleCheckName(f.SamplePath),
                Passed = false,
                Reason = $"{f.Kind}: {f.Message}"
            })
            .ToList();

        // The existing failure posture, unchanged in shape: a plan-preflight-failed section carrying one
        // check entry per finding, plus the uniform top-level `halt` (#432). Additive — no new section, no
        // new field. The section REPLACES any earlier passed marker on purpose: this run did not pass the
        // pre-DAG phase, and a journal that still reads `passed` beside a halt is the "recorded but not
        // true" failure this whole feature exists to end. It is also what makes the next resume re-evaluate
        // rather than skip, exactly as a failed Full Flight Check already does.
        var section = new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = journal.Document.PlanHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks
        };

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = DateTimeOffset.UtcNow,
            Headline = "Sample-pair verification FAILED — halting before scheduling any task: "
                       + string.Join(", ", checks.Select(c => c.Name).Distinct(StringComparer.Ordinal)),
            FailedChecks = checks
                .Select(c => new FailedGuardrail { Name = c.Name, Reason = c.Reason! })
                .ToList()
        };

        PlanPhaseJournalWriter.Update(
            plan.PlanDirectory, document => document with { PlanPreflights = section, Halt = halt });

        WriteSampleFailureReport(result, consoleOut);
        return false;
    }

    /// <summary>
    /// The journal/console name of a bad pair: its base name (the sample filename with its
    /// <c>.valid</c>/<c>.invalid</c> and content extensions stripped), which is also the name of the
    /// guardrail it is matched to.
    /// </summary>
    private static string SampleCheckName(string samplePath) =>
        "sample pair '"
        + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(samplePath))
        + "'";

    /// <summary>
    /// The operator-facing halt report. This phase runs BEFORE the Spectre live region is constructed, so
    /// plain lines are #145-safe; null <paramref name="consoleOut"/> ⇒ silent (the direct-call and
    /// revalidate paths supply their own writer).
    /// <para>
    /// It says WHY the check exists, not just that it failed, and that is deliberate. The harness already
    /// lints the guardrail that can never PASS (GR2055); running the <c>.invalid</c> half is the ONLY
    /// detector for the opposite and far more dangerous polarity — the guardrail that can never FAIL, which
    /// certifies every implementation including no implementation at all. An operator who understands that
    /// fixes the pair; one who reads only "sample mismatch" deletes it and restores the blind spot.
    /// </para>
    /// </summary>
    private static void WriteSampleFailureReport(SampleVerifyResult result, TextWriter? consoleOut)
    {
        if (consoleOut is null)
        {
            return;
        }

        consoleOut.WriteLine();
        consoleOut.WriteLine(
            $"Sample-pair verification FAILED — {result.Findings.Count} finding(s) over "
            + $"{result.PairsVerified} executed pair(s). Halting before scheduling any task.");

        foreach (SampleFinding finding in result.Findings)
        {
            consoleOut.WriteLine(
                $"  {finding.Kind}: {finding.SamplePath} against {finding.GuardrailPath ?? "(no matching guardrail)"} → "
                + $"exit {finding.ObservedExitCode?.ToString() ?? "(none)"}");
            consoleOut.WriteLine($"    {finding.Message}");
        }

        consoleOut.WriteLine(
            "  A tasks/<id>/samples/ pair asserts exactly two facts — the .valid half's guardrail exits 0, "
            + "the .invalid half's exits non-zero. The harness already lints the guardrail that can never "
            + "PASS (GR2055); running the .invalid half is the only detector for the opposite and far more "
            + "dangerous polarity, the guardrail that can never FAIL — one that certifies every "
            + "implementation, including no implementation at all. Fix the pair or the guardrail; deleting "
            + "the pair only restores the blind spot. Re-check with `guardrails samples verify`.");
    }

    /// <summary>
    /// The machine-readable stop reason for a failed pre-DAG phase (SSOT §7 <c>halt</c>): the same headline
    /// the console prints, the failing check names + reasons, and where their captured output landed.
    /// </summary>
    private static RunHalt BuildHalt(ReVerifyResult result, string? relativeLogDir) => new()
    {
        Kind = RunHaltKind.PlanPreflightFailed,
        HaltedAt = DateTimeOffset.UtcNow,
        Headline = "Plan preflight FAILED — halting before scheduling any task: "
                   + string.Join(", ", result.FailedGuardrails.Select(f => f.Name)),
        FailedChecks = result.FailedGuardrails
            .Select(f => new FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList(),
        LogDir = relativeLogDir
    };
}
