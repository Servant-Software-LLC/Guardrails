using Guardrails.Core.Journal;

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
/// across its own retries within one run.</para>
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
///
/// <para><b>STUB (#535, task 05).</b> <see cref="Ingest"/> unconditionally throws
/// <see cref="NotImplementedException"/>; <c>06-implement-journal-etl</c> fills it.</para>
/// </summary>
public static class TelemetryIngest
{
    /// <summary>
    /// Ingests one run's journal into <paramref name="corpusStore"/> — one task row per task, one
    /// attempt row per attempt (retries included) — using <paramref name="repo"/> for every row's
    /// required <see cref="TelemetryRow.Repo"/> (the journal does not know its own repo). Idempotent on
    /// the store's own <c>(runId, taskId, attempt)</c> key: re-ingesting the SAME journal adds no rows.
    /// </summary>
    public static void Ingest(JournalDocument journal, TelemetryCorpusStore corpusStore, string repo) =>
        throw new NotImplementedException();
}
