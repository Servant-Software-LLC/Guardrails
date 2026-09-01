// A COMPLETE, representative CORRECT artifact for
// 03-both-settle-records-set-every-phase1-member.ps1 (#468/#302): the worktree-mode success settle after
// this task, with every carrier read off the deferred attempt. Reduced to that member and one neighbour
// (so the region cutter has more than one region to choose between, which is the condition under which a
// declaration can be confused with a call), and given their real signatures - a complete file of that
// shape rather than a fragment, since an incomplete valid sample fails for a DIFFERENT reason and masks
// the real one. This header quotes none of the tokens the clauses key on (taxonomy 13).
using System;
using System.Text.Json.Nodes;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

internal sealed partial class Scheduler
{
    /// <summary>
    /// Record a worktree-mode SUCCESS settle (issue #196): journal a real attempt record for the
    /// just-completed attempt TOGETHER with the reserved merge sequence, so a succeeded worktree task has
    /// a populated attempts list exactly like a succeeded serial task (SSOT §7). A result missing its
    /// deferred attempt (a fake-provider path that never went through the journaller's validate step)
    /// falls back to the attempt-less settle, so no path regresses.
    /// </summary>
    private void RecordSucceededSettle(
        TaskNode task, TaskResult result, long mergeSequence, string? definitionHash = null)
    {
        if (result.PendingAttempt is not { } pending)
        {
            _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, mergeSequence, definitionHash);
            return;
        }

        var record = new Journal.AttemptRecord
        {
            Attempt = pending.Attempt,
            StartedAt = pending.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = pending.ActionExitCode,
            Outcome = Journal.AttemptOutcome.Succeeded,
            CostUsd = pending.CostUsd,
            // #475: the tokens axis travels beside its cost sibling on THIS path too - the default one.
            Usage = pending.Usage,
            // Plan 30 §3.4: the turn count is journalled on the serial path by the journaller, which this
            // path never consults. Without this line it reaches serial runs only.
            Turns = pending.Turns,
            // Plan 30 §3.4: same for the action and guardrail segment durations.
            Segments = pending.Segments,
            LogDir = pending.LogDir,
            // Plan 30 §3.3/§3.4: the model digest and the route-warmth flag ride the provenance rather
            // than carrying their own members, so this one line delivers both of them here.
            Provenance = pending.Provenance
        };

        // Plan 30 §3.2: the bucket is a TASK-grain fact, so it rides the task entry rather than the
        // attempt record - and therefore travels through the recorder's own parameter. The value was
        // computed once, in the journaller, and is READ here; recomputing it would be a second answer.
        // NAMED, not positional: the bucket sits beside definitionHash and both are string?, so a
        // positional argument one slot out compiles, drops the bucket, and stamps a bucket string into
        // the definition hash a resume compares and a safe-suffix rewind corroborates against.
        _journal.RecordSettleWithAttempt(
            task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, definitionHash,
            bucket: pending.Bucket);
    }

    /// <summary>
    /// Shallow-merge the validated state fragment into state.json. Present here only so the region cutter
    /// has a following member to bound the settle's region on, exactly as the real file does.
    /// </summary>
    private static void MergeFragmentIntoState(string statePath, string preMergeState, string fragmentPath)
    {
        var stateObj = (JsonNode.Parse(preMergeState) as JsonObject) ?? new JsonObject();
        var fragObj = (JsonNode.Parse(System.IO.File.ReadAllText(fragmentPath)) as JsonObject) ?? new JsonObject();
        foreach (var kvp in fragObj)
        {
            stateObj[kvp.Key] = kvp.Value?.DeepClone();
        }

        AtomicFile.WriteAllText(statePath, stateObj.ToJsonString());
    }
}
