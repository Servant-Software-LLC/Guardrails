// The ONE defect 03-both-settle-records-set-every-phase1-member.ps1 exists to catch, in its most
// realistic form: a settle that carries MOST of the Phase-1 members and quietly drops one of them. Here
// the turn count is missing from the record, and everything else is correct - the deferred attempt still
// carries the value (the journaller set it, and the tests on task 15 prove that half), the settle simply
// never reads it. The number is computed, carried across the settle boundary, and dropped one line
// before it would have been journalled, on the DEFAULT execution path, with every run and every test
// still green. Identical to the .valid half apart from that one omission, so the pair isolates exactly
// the clause under test.
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
