// A COMPLETE, representative CORRECT artifact for 03-no-disk-fallback-at-the-serial-sites.ps1
// (#468/#302): the serial settle after stage 4. It stamps the load-time pin, it names no hash function,
// and it coalesces off nothing. Kept complete rather than a fragment - an incomplete valid sample fails
// for a DIFFERENT reason and masks the real one. This header quotes none of the banned tokens
// (taxonomy 13).
using System;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

internal sealed class AttemptJournaler
{
    private readonly RunJournal _journal;

    public AttemptJournaler(RunJournal journal) => _journal = journal;

    public AttemptResult CompleteSucceededOrInvalidFragment(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal,
        AttemptProvenance? provenance = null)
    {
        AttemptRecord record = BuildRecord(task, attemptNumber, startedAt, relativeLogDir, action, guardrails, provenance);
        long? mergeSequence = TryMergeFragment(task, fragmentOutPath);

        // The EXECUTED-definition record: the bytes this attempt actually ran against, captured by the
        // loader at plan load and held on the immutable node ever since. Never a recompute from current
        // disk, and never a fallback - a null pin records a null hash, which the resume pre-pass already
        // treats as "unknown, assume unchanged".
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, task.DefinitionHashAtLoad);

        return new AttemptResult { Record = record, IsFinal = isFinal };
    }
}
