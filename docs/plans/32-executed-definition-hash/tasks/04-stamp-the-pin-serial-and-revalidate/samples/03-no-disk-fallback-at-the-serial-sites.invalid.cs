// The ONE defect 03-no-disk-fallback-at-the-serial-sites.ps1 exists to catch: the coalescing fallback
// section 5.2 calls the cheapest wrong implementation of the entire plan. It reads like defensive
// coding, it compiles, and for every node the loader built the two branches are identical - so no
// behavioural pin in this plan can tell it from the correct version. Identical to the .valid half apart
// from the stamped expression.
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

        _journal.RecordAttempt(
            task.Id,
            record,
            JournalTaskStatus.Succeeded,
            mergeSequence,
            task.DefinitionHashAtLoad ?? Journal.TaskDefinitionHash.Compute(task));

        return new AttemptResult { Record = record, IsFinal = isFinal };
    }
}
