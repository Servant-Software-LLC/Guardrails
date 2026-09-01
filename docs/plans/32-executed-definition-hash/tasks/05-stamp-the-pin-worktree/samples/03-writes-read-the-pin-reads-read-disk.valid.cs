// A COMPLETE, representative CORRECT artifact for 03-writes-read-the-pin-reads-read-disk.ps1
// (#468/#302): the six members of the scheduler this check governs, after stage 5. The four reads still
// recompute from current disk; the two worktree write sites stamp the load-time pin; nothing coalesces.
// Reduced to those six members and their real signatures - a complete file of that shape rather than a
// fragment, since an incomplete valid sample fails for a DIFFERENT reason and masks the real one. This
// header quotes none of the tokens the clauses key on (taxonomy 13).
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

internal sealed partial class Scheduler
{
    private (HashSet<string> PreSettledGreen, List<DefinitionDriftReporter.DriftInput> Drifted)
        DetectDefinitionDrift(
            IReadOnlyList<TaskNode> tasksToRun,
            IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
            TrailerTracking trailerTracking)
    {
        var preSettledGreen = new HashSet<string>(StringComparer.Ordinal);
        var drifted = new List<DefinitionDriftReporter.DriftInput>();
        foreach (TaskNode task in tasksToRun)
        {
            string? recorded = _journal.RecordedDefinitionHash(task.Id);
            if (recorded is null)
            {
                continue;
            }

            // A READ: the whole point of the comparison is that this side is CURRENT DISK. Pinning it
            // would make the pre-pass compare a pin against a pin and check nothing.
            string current = Journal.TaskDefinitionHash.Compute(task);
            if (!string.Equals(recorded, current, StringComparison.Ordinal))
            {
                drifted.Add(new DefinitionDriftReporter.DriftInput(task.Id, recorded, current));
            }
            else
            {
                preSettledGreen.Add(task.Id);
            }
        }

        return (preSettledGreen, drifted);
    }

    private IReadOnlyList<DriftResolvedTask> BuildResolvedTasks(
        PlanDefinition plan,
        IReadOnlyList<DefinitionDriftReporter.DriftInput> drifted,
        IReadOnlySet<string> safeSet)
    {
        var rows = new List<DriftResolvedTask>();
        foreach (TaskNode task in plan.Tasks)
        {
            string current;
            try
            {
                // A READ: the Part C audit rows describe the tree as it is NOW.
                current = Journal.TaskDefinitionHash.Compute(task);
            }
            catch
            {
                current = "(unreadable)";
            }

            rows.Add(new DriftResolvedTask(task.Id, current, safeSet.Contains(task.Id)));
        }

        return rows;
    }

    private void ConsumePendingAnswers(TaskNode task)
    {
        // A READ, and section 4.4 explains why it must STAY a read: #361's answer-file anti-stale
        // binding requires the answer's hash to equal both the escalation record's and the unit's
        // CURRENT hash at consumption. Both sides read disk and must stay on the same side.
        string current = Journal.TaskDefinitionHash.Compute(task);
        _answers.Consume(task.Id, current);
    }

    private async Task ClassifyTaskGateAsync(TaskNode task, TaskResult result, CancellationToken ct)
    {
        // A durable WRITE of a DISK value, deliberately (section 4.4): the escalation record's
        // anti-stale binding is a different contract with a different lifetime.
        string definitionHash = Journal.TaskDefinitionHash.Compute(task);
        await _escalations.RecordAsync(task.Id, definitionHash, result, ct).ConfigureAwait(false);
    }

    private async Task<TaskResult> SettleGreenIfWorktreeAsync(
        RunContext context,
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        CancellationToken ct)
    {
        // W3 - the trailer stamp on the non-deferred worktree path. The executed-definition record.
        handle.DefinitionHash = task.DefinitionHashAtLoad;
        return await SettleAsync(task, result, handle, context.Provider, context.Integration, ct)
            .ConfigureAwait(false);
    }

    private async Task<TaskResult> SettleAsync(
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        IWorktreeProvider provider,
        IntegrationHandle integ,
        CancellationToken ct)
    {
        // W2 - the deferred settle, and THE DEFAULT FOR A REAL RUN. It stamps both the journal entry
        // and the Guardrails-Task-Hash trailer, from the bytes this attempt actually executed.
        handle.DefinitionHash = task.DefinitionHashAtLoad;
        await provider.IntegrateAsync(handle, integ, ct).ConfigureAwait(false);
        _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, handle.MergeSequence, task.DefinitionHashAtLoad);
        return result;
    }
}
