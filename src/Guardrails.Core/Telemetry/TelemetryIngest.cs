using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The journal-to-corpus ETL (charter §3.1 "two grains, both recorded",
/// <c>model-evidence-and-graduation</c> #535 Phase 0): turns one plan run's <see cref="JournalDocument"/>
/// into corpus rows through <see cref="TelemetryCorpusStore"/>, classifying every guardrail-failed
/// attempt through <see cref="TelemetryFailureClassifier"/> on the way.
///
/// <para><b>Two grains, one row shape.</b> <see cref="TelemetryRow"/>'s schema (task 01) has no
/// dedicated task-grain columns, so both grains are written as <see cref="TelemetryRow"/> instances
/// through the SAME <see cref="TelemetryCorpusStore.Append"/>, distinguished by
/// <see cref="TelemetryRow.Attempt"/>: the reserved sentinel <c>0</c> is the ONE task row per task per
/// run (identity, declared route, terminal outcome); <c>1..N</c> are the real attempt rows, one per
/// attempt, retries included — folding a task down to its final attempt would under-report by exactly
/// the retry spend. Sentinel <c>0</c> never collides with a real attempt number (attempts are 1-based,
/// SSOT §7), so a task row rides the store's existing <c>(runId, taskId, attempt)</c> idempotency key
/// for free: no separate dedup logic needed for the task grain, and re-ingesting the same run adds no
/// rows of either grain.</para>
///
/// <para>A task row's <see cref="TelemetryRow.Tier"/> / <see cref="TelemetryRow.TierSource"/> carry the
/// DECLARED tier and its origin (charter §3.1) — sourced from the task's FIRST attempt's
/// <see cref="AttemptProvenance"/>, because <c>run.json</c> journals tier/tierSource only per attempt
/// (<see cref="TaskJournalEntry"/> itself carries none) and a task's declared tier does not change
/// across its own retries within one run. A task with zero attempts (e.g. blocked, never started)
/// contributes no rows at all — there is no evidence to record either grain from.</para>
///
/// <para>A guardrail-failed attempt's <see cref="TelemetryRow.Outcome"/> is refined past the bare
/// <c>guardrail-failed</c> token with the <see cref="TelemetryFailureClassifier"/> verdict, so the
/// three otherwise-indistinguishable failure sites charter §6 warns about survive into the corpus: a
/// genuine guardrail failure (<see cref="GuardrailFailureKind.GuardrailFailed"/>) stays the bare
/// <c>guardrail-failed</c> token; every other kind appends <c>:&lt;kebab-kind&gt;</c> —
/// <c>guardrail-failed:write-scope-violation</c>, <c>guardrail-failed:harness-write-out-of-scope</c>,
/// <c>guardrail-failed:staging-move-failure</c>, <c>guardrail-failed:undifferentiated</c>. No new
/// column: <see cref="TelemetryRow.Outcome"/> is already documented as a wire TOKEN, not a closed enum
/// string, so a refined token is still a truthful outcome.</para>
/// </summary>
public static class TelemetryIngest
{
    /// <summary>
    /// Ingests one run's journal into <paramref name="corpusStore"/> — one task row per task, one
    /// attempt row per attempt (retries included) — using <paramref name="repo"/> for every row's
    /// required <see cref="TelemetryRow.Repo"/> (the journal does not know its own repo). Idempotent on
    /// the store's own <c>(runId, taskId, attempt)</c> key: re-ingesting the SAME journal adds no rows.
    /// </summary>
    public static void Ingest(JournalDocument journal, TelemetryCorpusStore corpusStore, string repo)
    {
        foreach ((string taskId, TaskJournalEntry task) in journal.Tasks)
        {
            if (task.Attempts.Count == 0)
            {
                continue;
            }

            AttemptRecord firstAttempt = task.Attempts[0];
            AttemptRecord lastAttempt = task.Attempts[^1];
            AttemptProvenance? declaredProvenance = firstAttempt.Provenance;

            corpusStore.Append(new TelemetryRow
            {
                SchemaVersion = TelemetryRow.CurrentSchemaVersion,
                RunId = journal.RunId,
                TaskId = taskId,
                Attempt = 0,
                StartedAt = firstAttempt.StartedAt,
                EndedAt = lastAttempt.EndedAt,
                Outcome = TaskStatusToken(task.Status),
                Tier = declaredProvenance?.Tier,
                TierSource = TierSourceToken(declaredProvenance?.TierSource),
                Repo = repo
            });

            foreach (AttemptRecord attempt in task.Attempts)
            {
                AttemptProvenance? provenance = attempt.Provenance;

                corpusStore.Append(new TelemetryRow
                {
                    SchemaVersion = TelemetryRow.CurrentSchemaVersion,
                    RunId = journal.RunId,
                    TaskId = taskId,
                    Attempt = attempt.Attempt,
                    StartedAt = attempt.StartedAt,
                    EndedAt = attempt.EndedAt,
                    Outcome = AttemptOutcomeToken(attempt),
                    Model = provenance?.Model,
                    Runner = provenance?.Runner,
                    Kind = provenance?.Kind,
                    Tier = provenance?.Tier,
                    TierSource = TierSourceToken(provenance?.TierSource),
                    Effort = provenance?.Effort,
                    CostUsd = attempt.CostUsd,
                    InputTokens = attempt.Usage?.InputTokens,
                    OutputTokens = attempt.Usage?.OutputTokens,
                    Repo = repo
                });
            }
        }
    }

    /// <summary>
    /// The entry point the CLI's backfill verb calls (task 10): reads <c>&lt;planFolderPath&gt;/state/run.json</c>
    /// via <see cref="JournalReader"/> and ingests it. A plan folder with no journal — a plan that was
    /// never run, or whose <c>state/</c> was wiped — is a reported no-op (returns <c>false</c>), never an
    /// exception: backfill is pointed at directories of plans, some of which never ran.
    /// </summary>
    /// <returns><c>true</c> if a journal was found and ingested; <c>false</c> if there was none to read.</returns>
    public static bool IngestPlanFolder(string planFolderPath, TelemetryCorpusStore corpusStore, string repo)
    {
        string journalPath = RunJournal.PathFor(planFolderPath);
        if (!File.Exists(journalPath))
        {
            return false;
        }

        Ingest(JournalReader.Read(journalPath), corpusStore, repo);
        return true;
    }

    /// <summary>
    /// The attempt row's <see cref="TelemetryRow.Outcome"/>: the bare SSOT §7 outcome token, EXCEPT a
    /// <see cref="AttemptOutcome.GuardrailFailed"/> attempt, which is refined through
    /// <see cref="TelemetryFailureClassifier"/> — see the class doc for the token shapes.
    /// </summary>
    private static string AttemptOutcomeToken(AttemptRecord attempt)
    {
        if (attempt.Outcome != AttemptOutcome.GuardrailFailed)
        {
            return JournalJson.OutcomeToken(attempt.Outcome);
        }

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(attempt.LogDir, attempt.FailedGuardrails);
        return kind switch
        {
            GuardrailFailureKind.GuardrailFailed => JournalJson.OutcomeToken(AttemptOutcome.GuardrailFailed),
            GuardrailFailureKind.Undifferentiated => "guardrail-failed:undifferentiated",
            GuardrailFailureKind.WriteScopeViolation => "guardrail-failed:write-scope-violation",
            GuardrailFailureKind.HarnessWriteOutOfScope => "guardrail-failed:harness-write-out-of-scope",
            GuardrailFailureKind.StagingMoveFailure => "guardrail-failed:staging-move-failure",
            _ => throw new InvalidOperationException($"Unhandled guardrail failure kind '{kind}'.")
        };
    }

    /// <summary>
    /// The task row's <see cref="TelemetryRow.Outcome"/>: the task's terminal <see cref="TaskStatus"/> as
    /// its SSOT §7 kebab wire token. <see cref="JournalJson"/> keeps this mapping on a private converter
    /// (it owns <c>run.json</c>'s own serialization), so it is restated here the same way
    /// <c>StatusCommand.StatusText</c> and <c>LogSiteRenderer</c> already do — one spelling, several
    /// independent readers.
    /// </summary>
    private static string TaskStatusToken(JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Pending => "pending",
        JournalTaskStatus.Running => "running",
        JournalTaskStatus.Succeeded => "succeeded",
        JournalTaskStatus.NeedsHuman => "needs-human",
        JournalTaskStatus.Blocked => "blocked",
        JournalTaskStatus.Failed => "failed",
        _ => throw new InvalidOperationException($"Unhandled task status '{status}'.")
    };

    /// <summary>Null-carrying wrapper over <see cref="JournalJson.TierSourceToken"/>: absent stays absent.</summary>
    private static string? TierSourceToken(TierSource? source) =>
        source is { } value ? JournalJson.TierSourceToken(value) : null;
}
