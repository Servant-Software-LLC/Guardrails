using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// The terminal plan-guardrail phase (preflights-impl deliverable 4, design 09-preflight-first-class,
/// SSOT §3.3/§7/§7.1). Evaluates <c>&lt;plan&gt;/guardrails/</c> ONCE, after the Scheduler's DAG drains
/// wholly green, against the run's FINAL bytes — the integration worktree on the plan branch at its
/// merged HEAD in worktree mode, or the plan workspace directly in serial mode — via the same
/// unconditional <see cref="IReVerifier"/> seam <see cref="PlanPreflightPhase"/> and the §4.3 per-union
/// re-verify use. Read-only: no task action ever runs here.
/// <para>
/// REPLACES the retired <c>integrationGate</c> task kind's terminal role (SSOT §3.3): a plan that
/// declares this folder no longer relies on the <see cref="Scheduler"/>'s own legacy terminal-gate run,
/// which now skips itself whenever <see cref="PlanDefinition.PlanGuardrails"/> is non-empty.
/// </para>
/// <para>
/// A plan with no <c>&lt;plan&gt;/guardrails/</c> folder (<see cref="PlanDefinition.PlanGuardrails"/>
/// empty) is untouched: no evaluation, no <c>planGuardrails</c> journal section written (additive per
/// SSOT §7 — the section is omitted, never null noise, for a plan that doesn't opt in).
/// </para>
/// <para>
/// <b>No passed-marker skip (unlike the pre-DAG phase's B1 rule, SSOT §7).</b> The pre-DAG phase skips a
/// matching-<c>planHash</c> passed marker because it evaluates a negative-baseline condition that only
/// holds at the very START of a plan's lifecycle. The terminal phase carries no such concern — it always
/// evaluates the CURRENT merged HEAD — so it always re-runs whenever the DAG is wholly green, whether
/// this is the run's first pass or a B2(b) terminal-only resume after a prior terminal halt: every
/// already-<c>succeeded</c> task SKIPS via the ordinary resume rule (no attempt burned), the DAG drains
/// with nothing left to do, and this phase re-fires against the (unchanged, still-merged) HEAD.
/// </para>
/// </summary>
public static class PlanGuardrailPhase
{
    /// <summary>
    /// Evaluate (or no-op) the terminal phase for <paramref name="plan"/>. Returns true when the run
    /// settles green (the gate passed, or no <c>&lt;plan&gt;/guardrails/</c> folder is declared at all);
    /// false when the run must halt — a failed <c>planGuardrails</c> section (with per-check reasons)
    /// has already been journaled by the time this returns.
    /// <para>
    /// When <paramref name="heartbeatOut"/> is supplied, a per-guardrail wall-clock heartbeat (issue
    /// #331) is written to it while each check runs — surfacing liveness for a long terminal gate (a
    /// full test suite). The terminal phase runs OUTSIDE the Spectre live region (it is disposed before
    /// this is called), so plain heartbeat lines there are #145-safe. Null ⇒ no heartbeat.
    /// </para>
    /// <para>
    /// <paramref name="runId"/> is the run's journal id — the <c>logs/&lt;runId&gt;/</c> tree this gate's
    /// per-check output is captured under (issue #432, SSOT §8). It is a REQUIRED argument (nullable, but
    /// deliberately NOT defaulted) so a new caller cannot silently drop the capture; null means "no
    /// journal to name" ⇒ capture nothing.
    /// </para>
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        ProcessRunner processRunner,
        TextWriter? heartbeatOut,
        string? runId,
        CancellationToken cancellationToken,
        string? junctionRoot = null)
    {
        if (plan.PlanGuardrails.Count == 0)
        {
            // No <plan>/guardrails/ folder at all — the feature is not in use for this plan. Additive
            // per SSOT §7: omit the section entirely, never write a vacuous "passed" marker.
            return true;
        }

        string currentHash = PlanHash.Compute(plan);
        string evalWorkspace = PlanPhaseWorkspace.Resolve(plan, cancellationToken, junctionRoot);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        var reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        using GuardrailHeartbeat? heartbeat = heartbeatOut is null ? null : GuardrailHeartbeat.StartConsole(heartbeatOut);

        // Issue #432: capture each terminal check's stdout/stderr under logs/<runId>/guardrails/<name>/.
        // The terminal gate is the LAST thing a run does; when it fails there is no retry, no attempt dir
        // and no feedback.md — the captured output here is the whole post-mortem.
        string? artifactDir = GateArtifacts.DirectoryFor(
            plan.PlanDirectory, runId, waveDir: null, GateArtifacts.GuardrailsFolder);
        string? relativeLogDir = GateArtifacts.RelativeDirectoryFor(
            runId, waveDir: null, GateArtifacts.GuardrailsFolder);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(
                evalWorkspace,
                plan.PlanGuardrails,
                // #587 check B: the plan's producible scope, so a failing check's reason can name a
                // failing test file NO task declares. Null (silence) whenever any task declares no
                // writeScope — see WriteScope.CompleteProducibleScope.
                new ReVerifyOptions
                {
                    Progress = heartbeat,
                    ArtifactDirectory = artifactDir,
                    ProducibleScope = WriteScope.CompleteProducibleScope(plan)
                },
                cancellationToken)
            .ConfigureAwait(false);

        List<FailedGuardrail> failedChecks = result.FailedGuardrails
            .Select(f => new FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList();

        // Every check, passing ones included (issue #432) — `failedChecks` alone cannot distinguish
        // "three checks ran and the third failed" from "one check ran".
        List<PlanPreflightCheck> checks = plan.PlanGuardrails
            .Select(g =>
            {
                GuardrailResult? failure = result.FailedGuardrails
                    .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
                return new PlanPreflightCheck { Name = g.Name, Passed = failure is null, Reason = failure?.Reason };
            })
            .ToList();

        // #175 merge-collision attribution (SSOT §3.3, issue #205): a terminal-gate failure on the merged
        // HEAD is frequently a merge collision — two tasks with OVERLAPPING writeScope on a shared file
        // both wrote there and an AI/3-way merge kept both (a semantic duplicate, no conflict marker). Name
        // the offending task pair(s) + shared path so a human sees "this looks like a merge collision"
        // instead of a bare build error. Ported from the legacy Scheduler.WithTerminalGateFailure path onto
        // this four-folder terminal phase via the shared WriteScope.OverlappingWriteScopeHint helper —
        // identical attribution whichever gate fires. Advisory + additive: computed ONLY on failure, and
        // null (omitted) when no two writeScopes overlap.
        string? collisionHint = result.Passed ? null : WriteScope.OverlappingWriteScopeHint(plan);

        var section = new PlanGuardrailsSection
        {
            Status = result.Passed ? PlanPhaseStatus.Passed : PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = currentHash,
            FailedChecks = failedChecks,
            CollisionHint = collisionHint,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            LogDir = relativeLogDir
        };

        // Issue #432: on FAILURE also record the uniform top-level `halt`, so post-mortem tooling has one
        // place to read "why did this run stop?" regardless of WHICH of the four gate folders halted it.
        RunHalt? halt = result.Passed
            ? null
            : new RunHalt
            {
                Kind = RunHaltKind.PlanGuardrailFailed,
                HaltedAt = DateTimeOffset.UtcNow,
                Headline = "Terminal gate FAILED on the merged HEAD: "
                           + string.Join(", ", failedChecks.Select(f => f.Name)),
                FailedChecks = failedChecks,
                LogDir = relativeLogDir
            };

        PlanPhaseJournalWriter.Update(plan.PlanDirectory, document => halt is null
            ? document with { PlanGuardrails = section }
            : document with { PlanGuardrails = section, Halt = halt });

        return result.Passed;
    }
}
