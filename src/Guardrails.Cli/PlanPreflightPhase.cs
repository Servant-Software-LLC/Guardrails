using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;

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
    /// </summary>
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        RunJournal journal,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
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

        string evalWorkspace = ResolveEvalWorkspace(plan, cancellationToken);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        IReVerifier reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(evalWorkspace, plan.PlanPreflights, cancellationToken)
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
            Checks = checks
        };

        WriteMarker(plan.PlanDirectory, section);

        return result.Passed;
    }

    /// <summary>
    /// The workspace the phase evaluates against: the integration worktree on the plan branch (forked
    /// from the user's current HEAD) in worktree mode, or the plan workspace directly in serial mode —
    /// mirroring exactly the condition <see cref="SchedulerFactory.Create"/> wires a
    /// <see cref="GitWorktreeProvider"/> on. Creating the integration worktree here, ahead of the
    /// Scheduler's own run, is safe: <see cref="GitWorktreeProvider.CreateIntegration"/> is idempotent —
    /// it reuses a worktree already checked out on the plan branch — so the Scheduler's later call
    /// reuses this SAME worktree rather than creating a second one.
    /// </summary>
    private static string ResolveEvalWorkspace(PlanDefinition plan, CancellationToken cancellationToken)
    {
        if (!SchedulerFactory.WouldUseWorktreeMode(plan))
        {
            return plan.Workspace;
        }

        var worktreeProvider = new GitWorktreeProvider(plan.Workspace, SchedulerFactory.WorktreeRootFor(plan));
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle integ = worktreeProvider.CreateIntegration(
            planName: Path.GetFileName(plan.PlanDirectory),
            runId: runId,
            cancellationToken);

        return integ.IntegrationWorktreePath;
    }

    /// <summary>
    /// Persist the <c>planPreflights</c> section straight to <c>state/run.json</c> (the
    /// <see cref="JournalDocument.PlanPreflights"/> shape). Written directly to disk rather than through
    /// a <see cref="RunJournal"/> mutator — the journal type exposes none for this OPTIONAL top-level
    /// section — so this re-reads the document <see cref="RunJournal.LoadOrCreate"/> already persisted
    /// (with every task seeded pending) and adds only the one field, leaving everything else untouched.
    /// </summary>
    private static void WriteMarker(string planDirectory, PlanPreflightsSection section)
    {
        string path = RunJournal.PathFor(planDirectory);
        JournalDocument document = JournalReader.Read(path);
        JournalDocument updated = document with { PlanPreflights = section };
        string json = JsonSerializer.Serialize(updated, JournalJson.Options);
        AtomicFile.WriteAllText(path, json);
    }
}
