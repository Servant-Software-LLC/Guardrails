using Guardrails.Core.Journal;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// What a guardrail-failed attempt actually was (charter §6, <c>model-evidence-and-graduation</c>,
/// #535) — recovered from an attempt's <c>feedback.md</c> because <c>run.json</c> cannot distinguish
/// these three sites (all journaled as <c>AttemptOutcome.GuardrailFailed</c>):
/// <see cref="Guardrails.Core.Execution.TaskExecutor"/> staging move (~line 975), harness-write
/// out-of-scope (~line 1040), and write-scope violation (~line 1093).
///
/// <para><see cref="Undifferentiated"/> is the honest "we don't know" value — a missing/unreadable
/// <c>feedback.md</c>, or wording the classifier does not (yet) recognize, must land here rather than
/// be guessed into one of the named kinds.</para>
/// </summary>
public enum GuardrailFailureKind
{
    /// <summary>
    /// Could not be classified: the attempt's log dir (or its <c>feedback.md</c>) does not exist, or the
    /// feedback text did not match any known structural marker. Never a guess.
    /// </summary>
    Undifferentiated,

    /// <summary>A genuine guardrail ran and failed — the attempt's journaled <c>failedGuardrails</c> is non-empty.</summary>
    GuardrailFailed,

    /// <summary><c>TaskExecutor.cs:1093</c> — the write-scope check rejected a write outside the task's declared <c>writeScope</c>.</summary>
    WriteScopeViolation,

    /// <summary><c>TaskExecutor.cs:1040</c> — a <c>needsHarnessWrite</c> request was rejected as out of scope.</summary>
    HarnessWriteOutOfScope,

    /// <summary><c>TaskExecutor.cs:975</c> — the post-action staging move failed (empty source or an IO error).</summary>
    StagingMoveFailure
}

/// <summary>
/// Classifies one guardrail-failed attempt into the <see cref="GuardrailFailureKind"/> it actually was
/// (charter §6, #535). <see cref="Classify"/> is the whole surface: given the attempt's journaled
/// <c>failedGuardrails</c> and its log dir, it recovers the distinction <c>run.json</c> alone cannot
/// make, by reading the <c>feedback.md</c> <see cref="Guardrails.Core.Execution.AttemptJournaler.FailedAttempt"/>
/// already writes there.
///
/// <para><b>STUB (#535, task 03).</b> Task 04 fills the real pattern matching, derived from surveying the
/// wordings <c>RetryPolicy</c> has actually emitted across harness releases — not written here, so that
/// no implementation exists to accidentally make one of the tests below pass for the wrong reason.</para>
/// </summary>
public static class TelemetryFailureClassifier
{
    /// <summary>
    /// Classifies one attempt. A non-empty <paramref name="failedGuardrails"/> short-circuits to
    /// <see cref="GuardrailFailureKind.GuardrailFailed"/> without reading anything — a real guardrail
    /// already answered the question. Otherwise reads <c>feedback.md</c> from
    /// <paramref name="logDir"/>; a missing log site, an unreadable file, or wording that matches no
    /// known marker all yield <see cref="GuardrailFailureKind.Undifferentiated"/> — never a guess.
    /// </summary>
    public static GuardrailFailureKind Classify(string logDir, IReadOnlyList<FailedGuardrail> failedGuardrails)
    {
        throw new NotImplementedException();
    }
}
