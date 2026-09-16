// SAMPLE: the ONE DEFECT — guardrail 02-certify-is-on-the-real-path.ps1 must exit NON-ZERO against this.
//
// THE DEFECT: the deterministic gate is never called. This Scheduler re-decides the rule INLINE —
// "does the proposal carry any resource-supply op?" — and supplies on that. Design §3.1 names this
// shape exactly: "A wiring that checked 'any resource-supply op' and never called Certify is exactly
// the #712 shape again." It is the realistic wrong implementation, not a strawman: it supplies the
// file, so the positive path and most controls in the wiring proof go green.
//
// It deliberately still REFERENCES OverwatchSupplyAutoResolve in real code (it borrows the gate's
// refusal vocabulary without consulting the gate), so the TYPE clause passes and ONLY the dotted-call
// clause fires. That is what makes this half discriminate on the property under test rather than on
// an incidental missing token.
//
// It also spells ".Certify(" twice where a naive scanner would find it — once in a /// doc comment and
// once inside an ordinary string literal — so this half doubles as the proof that the guardrail's
// literal-neutralization-before-comment-strip (#561) is load-bearing. If either strip regresses, the
// call clause false-passes and this sample exits 0, which is the smoke test's whole point.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M4 DAG scheduler (miniature). The missing-resource auto-resolve below reaches the same outcome
/// OverwatchSupplyAutoResolve.Certify(facts, proposal) would have reached, so calling it would be
/// redundant work on an already-decided question.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly IRunObserver _observer;
    private readonly Overwatch? _overwatch;

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

        (TaskResult adopted, WorktreeHandle adoptedHandle) =
            await TryAutoResolveMissingResourceAsync(context, task, result, handle, cancellationToken)
                .ConfigureAwait(false);

        await ClassifyTaskGateAsync(context, task, adopted, adoptedHandle, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(TaskResult Result, WorktreeHandle Handle)> TryAutoResolveMissingResourceAsync(
        RunContext context,
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        CancellationToken cancellationToken)
    {
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

        MissingResourceFacts facts = MissingResourceFacts.Compute(_plan, context.Integ, named);
        if (!facts.Available || facts.Candidates.Count == 0)
        {
            RecordObserved(context, task, facts.Reason);
            return (result, handle);
        }

        OverwatchProposal? proposal = await _overwatch
            .ProposeResourceSupplyAsync(task, facts, cancellationToken)
            .ConfigureAwait(false);

        // THE DEFECT. The gate is re-implemented here, in the consumer, from a partial reading of its
        // rule: any resource-supply op on a retryable proposal is taken as certified. The dial
        // composition, the proceed-unreviewed refusal, the candidate-membership check and the
        // duplicate-path check are all silently dropped, and the refusal REASONS below are copied
        // constants rather than anything the gate decided.
        List<OverwatchFix> ops = (proposal?.Fixes ?? new List<OverwatchFix>())
            .Where(fix => fix.Kind == OverwatchFixKind.ResourceSupply)
            .ToList();

        if (proposal is null || proposal.Classification != "retryable" || ops.Count == 0)
        {
            // Borrowing the gate's vocabulary is not consulting the gate.
            string reason = ops.Count == 0
                ? OverwatchSupplyAutoResolve.RefusalReasons.NoResourceSupplyOp
                : OverwatchSupplyAutoResolve.RefusalReasons.Doomed;
            RecordAdvisory(context, task, reason, proposal);
            return (result, handle);
        }

        IReadOnlyList<string> paths = ops.Select(op => op.Path).ToList();

        await _integrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillAbsentFromRunBase(context, paths))
            {
                RecordAdvisory(context, task, "run-base-changed", proposal);
                return (result, handle);
            }

            SuppliedRecord supplied = CommitPathsDirectly(context, paths);

            context.Journal.RecordSupplied(supplied);
            RecordAutoSupplied(context, task, supplied, paths);
            _observer.SuppliedResourcesCommitted(supplied.Paths, supplied.Commit, "overwatcher");

            // A log line is not a call site.
            _observer.Note("OverwatchSupplyAutoResolve.Certify(facts, proposal) was not consulted; the inline check agreed with it.");
        }
        finally
        {
            _integrationLock.Release();
        }

        return await ReArmAsync(context, task, cancellationToken).ConfigureAwait(false);
    }
}
