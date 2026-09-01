// EXTRA CASE (not executed by `guardrails samples verify`, which matches only the exact .valid/
// .invalid pair - kept committed so a later editor can re-run it by hand): the OBVIOUS defect, a
// worktree write site left recomputing from disk. It is the half an earlier draft of section 9's grep
// could see; the .invalid half above is the half it could not.
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
        handle.DefinitionHash = Journal.TaskDefinitionHash.Compute(task);
        await provider.IntegrateAsync(handle, integ, ct).ConfigureAwait(false);
        _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, handle.MergeSequence, task.DefinitionHashAtLoad);
        return result;
    }
}
