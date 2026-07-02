using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;

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
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (plan.PlanGuardrails.Count == 0)
        {
            // No <plan>/guardrails/ folder at all — the feature is not in use for this plan. Additive
            // per SSOT §7: omit the section entirely, never write a vacuous "passed" marker.
            return true;
        }

        string currentHash = PlanHash.Compute(plan);
        string evalWorkspace = PlanPhaseWorkspace.Resolve(plan, cancellationToken);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        IReVerifier reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(evalWorkspace, plan.PlanGuardrails, cancellationToken)
            .ConfigureAwait(false);

        List<FailedGuardrail> failedChecks = result.FailedGuardrails
            .Select(f => new FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList();

        var section = new PlanGuardrailsSection
        {
            Status = result.Passed ? PlanPhaseStatus.Passed : PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = currentHash,
            FailedChecks = failedChecks
        };

        WriteMarker(plan.PlanDirectory, section);

        return result.Passed;
    }

    /// <summary>
    /// Persist the <c>planGuardrails</c> section straight to <c>state/run.json</c> (the
    /// <see cref="JournalDocument.PlanGuardrails"/> shape) — same direct-write pattern as
    /// <see cref="PlanPreflightPhase"/>'s marker: the journal type exposes no mutator for this OPTIONAL
    /// top-level section, so this re-reads the current document and adds only the one field.
    /// </summary>
    private static void WriteMarker(string planDirectory, PlanGuardrailsSection section)
    {
        string path = RunJournal.PathFor(planDirectory);
        JournalDocument document = JournalReader.Read(path);
        JournalDocument updated = document with { PlanGuardrails = section };
        string json = JsonSerializer.Serialize(updated, JournalJson.Options);
        AtomicFile.WriteAllText(path, json);
    }
}
