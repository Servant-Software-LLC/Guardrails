using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

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
/// </summary>
public static class PlanPreflightPhase
{
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
