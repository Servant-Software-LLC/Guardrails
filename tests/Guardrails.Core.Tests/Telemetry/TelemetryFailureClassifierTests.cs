using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The guardrail-failed classifier (charter §6, <c>model-evidence-and-graduation</c>, #535): three
/// distinct failure sites in <c>TaskExecutor</c> — staging move, harness-write out-of-scope, and
/// write-scope violation — all journal as the SAME <c>AttemptOutcome.GuardrailFailed</c>, so
/// <c>run.json</c> alone cannot tell them apart. The only surviving distinguishing evidence is the
/// <c>feedback.md</c> <c>AttemptJournaler.FailedAttempt</c> writes into the attempt's log dir. Six
/// behaviours, each pinned to an exact method name the red-census guardrail binds to.
///
/// <para><b>TDD red.</b> Every test here calls <see cref="TelemetryFailureClassifier.Classify"/>, which
/// throws <see cref="NotImplementedException"/> until <c>04-implement-failure-classifier</c> fills it —
/// so the whole file is red, and none of it can be green by coincidence with a stub's default.</para>
///
/// <para>Every test writes its own temp log site under a fresh per-test root (never a real run's
/// <c>logs/</c>, which may be pruned) and deletes it afterwards.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryFailureClassifierTests : IDisposable
{
    private readonly string logRoot =
        Path.Combine(Path.GetTempPath(), "guardrails-failure-classifier-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(logRoot))
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    // --- 1. write-scope violation -------------------------------------------------------------------

    /// <summary>
    /// The exact structural marker <c>RetryPolicy.ForWriteScopeViolation</c> emits
    /// (<c>## Write-scope violation</c>) classifies as <see cref="GuardrailFailureKind.WriteScopeViolation"/>.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_WriteScopeViolation_FromFeedbackText()
    {
        string logDir = WriteFeedback(
            "# Task 'demo' failed its guardrails\n\n" +
            "## Write-scope violation\n\n" +
            "The following path(s) were modified but fall OUTSIDE this task's declared writeScope:\n\n" +
            "- `docs/oops.md` (A: new/untracked — no history at this task's base commit)\n\n" +
            "The harness has already reverted those files to their pre-attempt state.\n");

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, []);

        Assert.Equal(GuardrailFailureKind.WriteScopeViolation, kind);
    }

    // --- 2. staging-move failure is a distinct kind -------------------------------------------------

    /// <summary>
    /// The staging-move marker (<c>## Staging move failed</c>, <c>TaskExecutor.cs:975</c>) must NOT be
    /// bucketed with the write-scope violation — a fixture crafted so a naive "contains 'scope'-ish
    /// wording" implementation would conflate the two, since both feedbacks discuss out-of-place writes.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_StagingMoveFailure_IsNotWriteScope()
    {
        string logDir = WriteFeedback(
            "# Task 'demo' failed its guardrails\n\n" +
            "## Staging move failed\n\n" +
            "the staging move did not complete: source directory was empty\n\n" +
            "Your action completed, but the harness could not move your `.claude/` deliverable into\n" +
            "place because it was not staged. Write your deliverable to the absolute staging directory\n" +
            "under the declared `from` path(s) BEFORE you finish.\n");

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, []);

        Assert.Equal(GuardrailFailureKind.StagingMoveFailure, kind);
        Assert.NotEqual(GuardrailFailureKind.WriteScopeViolation, kind);
    }

    // --- 3. harness-write out-of-scope is its own kind ----------------------------------------------

    /// <summary>
    /// The <c>needsHarnessWrite</c> rejection marker (<c>## needsHarnessWrite rejected</c>,
    /// <c>TaskExecutor.cs:1040</c>) is a PROSPECTIVE out-of-scope rejection — distinct from the
    /// RETROSPECTIVE write-scope violation even though both feedbacks are "about" writeScope.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_HarnessWriteOutOfScope_IsItsOwnKind()
    {
        string logDir = WriteFeedback(
            "# Task 'demo' failed its guardrails\n\n" +
            "## needsHarnessWrite rejected\n\n" +
            "Your `needsHarnessWrite` request for `.claude/skills/foo/SKILL.md` was REJECTED before any\n" +
            "write happened:\n\n" +
            "> path escapes the declared writeScope\n\n" +
            "Request a path that is genuinely within this task's declared `writeScope`.\n");

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, []);

        Assert.Equal(GuardrailFailureKind.HarnessWriteOutOfScope, kind);
        Assert.NotEqual(GuardrailFailureKind.WriteScopeViolation, kind);
        Assert.NotEqual(GuardrailFailureKind.StagingMoveFailure, kind);
    }

    // --- 4. a real guardrail failure stays guardrail-failed ------------------------------------------

    /// <summary>
    /// A non-empty <c>failedGuardrails</c> means a genuine guardrail ran and failed — the classifier
    /// must return <see cref="GuardrailFailureKind.GuardrailFailed"/> WITHOUT reading anything, so the
    /// log dir here is a path that was never created; if the implementation tried to read a
    /// <c>feedback.md</c> from it, that would throw, not merely mis-classify.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_RealGuardrailFailure_StaysGuardrailFailed()
    {
        string logDir = Path.Combine(logRoot, Guid.NewGuid().ToString("N")); // deliberately never created
        FailedGuardrail[] failedGuardrails = [new FailedGuardrail { Name = "02-tests-pass", Reason = "1 test failed" }];

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, failedGuardrails);

        Assert.Equal(GuardrailFailureKind.GuardrailFailed, kind);
    }

    // --- 5. a missing log site is undifferentiated, never guessed ------------------------------------

    /// <summary>
    /// A log dir that does not exist (a pruned or never-created site) yields
    /// <see cref="GuardrailFailureKind.Undifferentiated"/> — the exact value, not merely "not
    /// write-scope". This is the honesty rule of the whole plan expressed as one test: an attempt we
    /// cannot classify must say so rather than be quietly counted as something.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_MissingLogSite_IsUndifferentiated_NeverGuessed()
    {
        string logDir = Path.Combine(logRoot, Guid.NewGuid().ToString("N")); // never created — no feedback.md

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, []);

        Assert.Equal(GuardrailFailureKind.Undifferentiated, kind);
    }

    // --- 6. unrecognized wording is undifferentiated --------------------------------------------------

    /// <summary>
    /// Plausible OLDER harness wording that MENTIONS write scope in prose but never uses today's
    /// structural <c>## Write-scope violation</c> marker. The feedback wording has changed across
    /// harness releases, so a classifier that assumes today's wording — or that matches on the bare
    /// words "write scope" anywhere in the text — would silently mis-bucket this as a violation instead
    /// of admitting it does not recognize the shape.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Classify_UnrecognizedFeedbackWording_IsUndifferentiated()
    {
        string logDir = WriteFeedback(
            "# Task 'demo' failed\n\n" +
            "Your changes touched files outside what this task is allowed to write. Please limit your\n" +
            "edits to the declared write scope and try again.\n");

        GuardrailFailureKind kind = TelemetryFailureClassifier.Classify(logDir, []);

        Assert.Equal(GuardrailFailureKind.Undifferentiated, kind);
    }

    // --- fixtures --------------------------------------------------------------------------------

    /// <summary>Creates a fresh temp log dir under <see cref="logRoot"/> containing the given <c>feedback.md</c> text.</summary>
    private string WriteFeedback(string feedback)
    {
        string dir = Path.Combine(logRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "feedback.md"), feedback);
        return dir;
    }
}
