// SAMPLE: the CORRECT shape — guardrail 02-certify-is-on-the-real-path.ps1 must exit 0 against this.
//
// A complete, representative miniature of src/Guardrails.Core/Execution/Scheduler.cs: the usings, the
// namespace, the sealed class, the integration lock, and the private TryAutoResolveMissingResourceAsync
// that OnSettledAsync calls between the green settle and the classify-then-act dispatch (design §3.3).
// Kept complete on purpose (#468): an incomplete valid half fails for a DIFFERENT reason and masks the
// real one.
//
// What makes it valid: the deterministic gate is ON THE PATH. The Scheduler hands the harness-computed
// facts and the parsed proposal to OverwatchSupplyAutoResolve.Certify and acts on what IT returned —
// it never re-decides the rule inline.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M4 DAG scheduler (miniature). Owns the integration lock, the handles and the DAG, which is why
/// design §4 puts the missing-resource auto-resolve seam here and nowhere else.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly IRunObserver _observer;
    private readonly Overwatch? _overwatch;

    // Serialize-merges lock (plan 08 §3): one integration-settle at a time.
    private readonly SemaphoreSlim _integrationLock = new(1, 1);

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        IRunObserver observer,
        Overwatch? overwatch = null)
    {
        _plan = plan;
        _executor = executor;
        _observer = observer;
        _overwatch = overwatch;
    }

    private async Task OnSettledAsync(
        RunContext context,
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        CancellationToken cancellationToken)
    {
        await SettleGreenIfWorktreeAsync(context, task, result, handle, cancellationToken)
            .ConfigureAwait(false);

        // Design §3.3, step 2: the auto-resolve runs BEFORE the classify-then-act dispatch, and the
        // ADOPTED result is what reaches it. A certified supply resolves the gate by an action, so the
        // CriticalityJudge is never consulted about a question that no longer applies.
        (TaskResult adopted, WorktreeHandle adoptedHandle) =
            await TryAutoResolveMissingResourceAsync(context, task, result, handle, cancellationToken)
                .ConfigureAwait(false);

        await ClassifyTaskGateAsync(context, task, adopted, adoptedHandle, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Design §4: propose, certify, commit under the integration lock, record, re-arm. Returns the
    /// adopted result and handle; on any non-certified outcome it returns the originals unchanged,
    /// which is the never-weaker path.
    /// </summary>
    private async Task<(TaskResult Result, WorktreeHandle Handle)> TryAutoResolveMissingResourceAsync(
        RunContext context,
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        CancellationToken cancellationToken)
    {
        // Tiers 0-2 (design §2.1). Tier 0 and tier 1 write no record; every tier-2 stop writes one
        // outcome-inert `observed` decision with its reason token, because at the dial that promised an
        // auto-resolve its absence must not be silent.
        if (!AutoResolveDialEngaged(context))
        {
            return (result, handle);
        }

        if (!MissingResourceSignal.TryReadPaths(result.NeedsHumanQuestion, out IReadOnlyList<string> named))
        {
            return (result, handle);
        }

        if (_overwatch is null)
        {
            RecordObserved(context, task, "no-runner");
            return (result, handle);
        }

        // The harness computes every fact about the file itself (design §2.2), tri-state: an error is
        // never read as absent.
        MissingResourceFacts facts = MissingResourceFacts.Compute(_plan, context.Integ, named);
        if (!facts.Available || facts.Candidates.Count == 0)
        {
            RecordObserved(context, task, facts.Reason);
            return (result, handle);
        }

        // A prompt may propose...
        OverwatchProposal? proposal = await _overwatch
            .ProposeResourceSupplyAsync(task, facts, cancellationToken)
            .ConfigureAwait(false);

        // ...only a deterministic gate may certify. THE LOAD-BEARING CALL: the pure function owns the
        // rule (design §3.1), so the dial composition, the review-gate check, the retryable
        // classification, the candidate membership and the duplicate check are all decided in ONE
        // place that a future caller cannot skip.
        SupplyCertification certification = OverwatchSupplyAutoResolve.Certify(
            _plan.Config.AutonomyPolicy,
            _plan.Config.Autonomy is not null,
            _plan.Config.Autonomy,
            facts.Candidates,
            proposal);

        if (!certification.IsCertified)
        {
            RecordAdvisory(context, task, certification.Reason, proposal);
            return (result, handle);
        }

        await _integrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The last check runs here, under the lock and just before the commit (design §3.1).
            if (!StillAbsentFromRunBase(context, certification.Paths))
            {
                RecordAdvisory(context, task, "run-base-changed", proposal);
                return (result, handle);
            }

            SuppliedRecord supplied = CommitCertifiedPaths(context, certification);

            // Design §4 step 6: the record, the decision and the event go together, in the same locked
            // step, so no later failure can leave a supply without the decision that holds its delivery.
            context.Journal.RecordSupplied(supplied);
            RecordAutoSupplied(context, task, supplied, certification);
            _observer.SuppliedResourcesCommitted(supplied.Paths, supplied.Commit, "overwatcher");
        }
        finally
        {
            _integrationLock.Release();
        }

        return await ReArmAsync(context, task, cancellationToken).ConfigureAwait(false);
    }
}
