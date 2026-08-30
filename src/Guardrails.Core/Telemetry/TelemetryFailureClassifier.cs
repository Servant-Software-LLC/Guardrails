using System.Text.RegularExpressions;
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
/// <para><b>Evidence survey (task 04).</b> The feedback wording has drifted across harness releases, so
/// the patterns below were derived from the producers AND from history, never from today's source alone:
/// <list type="bullet">
/// <item><b>Producers (today):</b> <c>RetryPolicy.ForStagingFailure</c> (<c>RetryPolicy.cs:783</c>),
/// <c>ForHarnessWriteOutOfScope</c> (<c>:633</c>), <c>ForWriteScopeViolation</c> (<c>:572</c>).</item>
/// <item><b>History:</b> each of the three marker LINES was introduced by exactly one commit and has
/// never been edited since — <c>git log -S'## Write-scope violation' -- src/Guardrails.Core/Execution/RetryPolicy.cs</c>
/// (and the two equivalents) each return a single commit. Every later change to these feedbacks
/// rewrote the BODY under the marker and left the marker itself alone; that is what makes the marker,
/// rather than any sentence, the stable thing to key on.</item>
/// <item><b>Corpus on disk:</b> 10 real <c>feedback.md</c> files survive under
/// <c>docs/plans/*/logs/**/attempt-*/</c>; 6 carry a write-scope marker and they are all ONE distinct
/// wording (generation G1 below). No staging-move or <c>needsHarnessWrite</c> feedback survives on
/// disk at all, so those two patterns are verified against their producer and the authored fixtures
/// only — stated here rather than left for a reader to assume.</item>
/// </list></para>
/// </summary>
public static class TelemetryFailureClassifier
{
    /// <summary>The file <c>AttemptJournaler.FailedAttempt</c> writes into every failed attempt's log dir.</summary>
    private const string FeedbackFileName = "feedback.md";

    /// <summary>
    /// <c>RetryPolicy.ForWriteScopeViolation</c> (<c>RetryPolicy.cs:572</c>). Covers all THREE wording
    /// generations of this feedback, because each generation changed only the body beneath the marker:
    /// <list type="bullet">
    /// <item><b>G1</b> — <c>fa3f500</c> (2026-06-21, plan 08 / #123): marker, a bare <c>- `path`</c> list,
    /// then "The harness has already reverted those files to their pre-attempt state." VERIFIED against
    /// the real sample at <c>docs/plans/diagram-live-status-and-search/logs/2026-07-07T12-19-44Z-861a/04-implement-diagram-observer/attempt-1/feedback.md:8</c>
    /// (plus 5 identical siblings — the only wording that survives on disk).</item>
    /// <item><b>G2</b> — <c>c479695</c> (2026-07-07, #253/#257): every path gains an A/M/D status
    /// (<c>A: new/untracked — no history at this task's base commit</c>) and a new file gains a forensic
    /// preview fence. VERIFIED against the G2-shaped fixture in
    /// <c>TelemetryFailureClassifierTests.Classify_WriteScopeViolation_FromFeedbackText</c>; no G2 sample
    /// survives on disk.</item>
    /// <item><b>G3</b> — <c>40639d8</c> (2026-07-08, #306/#310): adds the <c>fileWritesRolledBack</c>
    /// closing paragraph and a trailing <c>## Prior attempt work is salvageable</c> section. Body only;
    /// no sample on disk.</item>
    /// </list>
    /// A future generation that RENAMES the heading is not covered and will read as
    /// <see cref="GuardrailFailureKind.Undifferentiated"/> — deliberately. This list is how a reader tells
    /// "covered" from "merely unseen".
    /// </summary>
    private static readonly Regex WriteScopeViolationMarker = StructuralMarker("Write-scope violation");

    /// <summary>
    /// <c>RetryPolicy.ForHarnessWriteOutOfScope</c> (<c>RetryPolicy.cs:633</c>). ONE generation:
    /// <c>4460637</c> (2026-07-03, #191) introduced this marker line and nothing has edited it since. No
    /// sample survives on disk; VERIFIED against the producer and the fixture in
    /// <c>TelemetryFailureClassifierTests.Classify_HarnessWriteOutOfScope_IsItsOwnKind</c>.
    ///
    /// <para>The <c>rejected</c> word is load-bearing and must never be relaxed to a
    /// <c>## needsHarnessWrite</c> prefix: three SIBLING markers were added to the same family later —
    /// <c>## needsHarnessWrite denied</c> (<c>9bd6a01</c>, #321), <c>## needsHarnessWrite could not be
    /// applied</c> (<c>d9c006d</c>, #437) and <c>## needsHarnessWrite failed</c> — and none of them is an
    /// out-of-scope rejection. A prefix match would silently bucket all four as
    /// <see cref="GuardrailFailureKind.HarnessWriteOutOfScope"/>; they correctly fall to
    /// <see cref="GuardrailFailureKind.Undifferentiated"/> until this plan gives them kinds of their own.</para>
    /// </summary>
    private static readonly Regex HarnessWriteOutOfScopeMarker = StructuralMarker("needsHarnessWrite rejected");

    /// <summary>
    /// <c>RetryPolicy.ForStagingFailure</c> (<c>RetryPolicy.cs:783</c>). ONE generation: <c>6c4ed1f</c>
    /// (2026-06-22, #130) introduced this marker line and nothing has edited it since. No sample survives
    /// on disk; VERIFIED against the producer and the fixture in
    /// <c>TelemetryFailureClassifierTests.Classify_StagingMoveFailure_IsNotWriteScope</c>.
    /// </summary>
    private static readonly Regex StagingMoveFailureMarker = StructuralMarker("Staging move failed");

    /// <summary>
    /// The marker-to-kind table, listed in the order <c>TaskExecutor</c> reaches the failure sites
    /// (staging move :975 → harness write :1040 → write-scope check :1093). The order is documentation
    /// only: <see cref="Classify"/> requires exactly one marker to match, so it cannot depend on which
    /// rule is consulted first.
    /// </summary>
    private static readonly (Regex Marker, GuardrailFailureKind Kind)[] MarkerRules =
    [
        (StagingMoveFailureMarker, GuardrailFailureKind.StagingMoveFailure),
        (HarnessWriteOutOfScopeMarker, GuardrailFailureKind.HarnessWriteOutOfScope),
        (WriteScopeViolationMarker, GuardrailFailureKind.WriteScopeViolation)
    ];

    /// <summary>
    /// Classifies one attempt. A non-empty <paramref name="failedGuardrails"/> short-circuits to
    /// <see cref="GuardrailFailureKind.GuardrailFailed"/> without reading anything — a real guardrail
    /// already answered the question. Otherwise reads <c>feedback.md</c> from
    /// <paramref name="logDir"/>; a missing log site, an unreadable file, or wording that matches no
    /// known marker all yield <see cref="GuardrailFailureKind.Undifferentiated"/> — never a guess.
    /// </summary>
    public static GuardrailFailureKind Classify(string logDir, IReadOnlyList<FailedGuardrail> failedGuardrails)
    {
        // The short-circuit is not just an optimisation. A guardrail's own stdout is echoed into the
        // feedback's "## Full output (tail)" section, so a guardrail that happens to print one of the
        // markers would otherwise re-classify a genuine guardrail failure as a harness failure.
        if (failedGuardrails is { Count: > 0 })
        {
            return GuardrailFailureKind.GuardrailFailed;
        }

        if (TryReadFeedback(logDir) is not { } feedback)
        {
            return GuardrailFailureKind.Undifferentiated;
        }

        GuardrailFailureKind classified = GuardrailFailureKind.Undifferentiated;
        foreach ((Regex marker, GuardrailFailureKind kind) in MarkerRules)
        {
            if (!marker.IsMatch(feedback))
            {
                continue;
            }

            if (classified != GuardrailFailureKind.Undifferentiated)
            {
                // Two different sites' markers in one file. No producer emits that today (each site
                // returns the moment it composes its feedback), so it would mean a shape this
                // classifier does not understand — report the honest unknown rather than pick one.
                return GuardrailFailureKind.Undifferentiated;
            }

            classified = kind;
        }

        return classified;
    }

    /// <summary>
    /// The shared "this is a USE of the marker, not a mention of it" rule, defined once: the heading must
    /// occupy a whole line, at column 0, with nothing but optional trailing blanks (and a CR, so CRLF
    /// files match) after it. Prose that merely talks about write scope, an indented copy inside a code
    /// fence, and a quoted <c>&gt; ## Write-scope violation</c> all fail this — only the line
    /// <c>RetryPolicy</c> itself emits passes. The heading text is matched verbatim and case-sensitively,
    /// because it is a fixed literal in the producer: a reworded or re-cased marker is a wording this
    /// classifier has not seen, and the plan's rule for that is <see cref="GuardrailFailureKind.Undifferentiated"/>,
    /// not a lenient guess.
    /// </summary>
    private static Regex StructuralMarker(string heading) =>
        new(@"^## " + Regex.Escape(heading) + @"[ \t\r]*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads an attempt's <c>feedback.md</c>, or returns <c>null</c> for every reason it might not be
    /// readable — a pruned or never-created log dir, a file a live run is still writing, a locked or
    /// permission-denied file, a malformed path out of an old <c>run.json</c>. Classification of a whole
    /// corpus must never be aborted by one unreadable attempt, and the caller turns every <c>null</c>
    /// into <see cref="GuardrailFailureKind.Undifferentiated"/>.
    /// </summary>
    private static string? TryReadFeedback(string logDir)
    {
        if (string.IsNullOrWhiteSpace(logDir))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(logDir, FeedbackFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            // FileShare.ReadWrite: a run that is still journaling this attempt holds the file open, and
            // a half-written feedback is worth classifying on its merits (it either carries the marker
            // yet or it does not) rather than throwing a sharing violation at the caller.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
