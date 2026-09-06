using System.Text.Json;
using Guardrails.Core.Journal;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`, despite living under Journal/ — the same
// ruling ExecutedDefinitionHashTests.cs and JudgeSpendRecordingTests.cs already record. Declaring that
// nested namespace ANYWHERE in this assembly shadows the production `Guardrails.Core.Journal` namespace
// for every unqualified `Journal.X` reference elsewhere in the assembly, and the build fails in files
// that have nothing to do with this one.
namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #538 — three sites journaled <c>guardrail-failed</c> with an <b>empty</b>
/// <c>failedGuardrails</c>: a refused <c>needsHarnessWrite</c>, a write-scope violation, and a staging-move
/// failure. No guardrail ran at any of them.
///
/// <para>
/// <b>Measured.</b> A real guardrail failure names the guardrail
/// (<c>26-guardrail-quality-gate</c>, task <c>06-record-samples-verify-in-ssot</c>):
/// <c>guardrail-failed  failedGuardrails=1  ['01-ssot-records-the-verb-and-the-step']</c>. A harness-write
/// rejection reported the same outcome and named nothing
/// (<c>27-operator-visibility</c>, task <c>08-record-visibility-surfaces-in-ssot</c>, run
/// <c>2026-08-29T16-37-39Z-fc5d</c>): six attempts, <c>needs-human</c>, $2.58, five of them
/// <c>guardrail-failed  failedGuardrails=0</c>. Every real cause was the same — an anchored
/// <c>edits[N].old</c> that was NOT FOUND — and <c>run.json</c> recorded none of it.
/// </para>
///
/// <para>
/// <b>Why the mislabel costs more than a wrong word.</b> <c>state/run.json</c> is the durable record, and
/// for that task it said "a guardrail failed" six times and named no guardrail — which is not merely
/// uninformative, it is <i>wrong</i>, and it points a reader (or a self-healing agent, #529) at the
/// guardrail set, which was never the problem. It also compounds with the fragment being cleared:
/// <c>action-out-fragment.json</c> reads <c>{}</c> for a rejected write, so neither the failure class nor
/// the failed payload survived into anything machine-readable, and the diagnosis existed only as prose in
/// a <c>feedback.md</c> under the log directory.
/// </para>
/// </summary>
public sealed class NonGuardrailOutcomesTests
{
    /// <summary>
    /// The three new outcomes round-trip through the journal's own writer and reader. A value the writer
    /// emits and the reader cannot parse would silently degrade every resumed run's history — and the
    /// reader's fallback is the quietest possible failure.
    /// </summary>
    [Theory]
    [InlineData(AttemptOutcome.HarnessWriteRejected, "harness-write-rejected")]
    [InlineData(AttemptOutcome.WriteScopeViolation, "write-scope-violation")]
    [InlineData(AttemptOutcome.StagingFailed, "staging-failed")]
    public void EachNonGuardrailOutcome_RoundTripsThroughTheJournal(AttemptOutcome outcome, string token)
    {
        Assert.Equal(token, JournalJson.OutcomeToken(outcome));

        // Through the REAL converter, not a second copy of the mapping: the writer and the reader are
        // separate switch statements, and a token one emits that the other cannot parse degrades a
        // resumed run's history silently — the reader's fallback being the quietest possible failure.
        string json = JsonSerializer.Serialize(outcome, JournalJson.Options);
        Assert.Equal($"\"{token}\"", json);
        Assert.Equal(outcome, JsonSerializer.Deserialize<AttemptOutcome>(json, JournalJson.Options));
    }

    /// <summary>
    /// The three are DISTINCT from each other and from <c>guardrail-failed</c>. Without this, a fix that
    /// mapped all three onto one new value would satisfy "not guardrail-failed" while keeping the property
    /// that actually cost the six attempts: three different causes with three different remediations
    /// reading identically in the record.
    /// </summary>
    [Fact]
    public void TheThreeCausesAreDistinguishable_NotJustNonGuardrail()
    {
        string[] tokens =
        [
            JournalJson.OutcomeToken(AttemptOutcome.HarnessWriteRejected),
            JournalJson.OutcomeToken(AttemptOutcome.WriteScopeViolation),
            JournalJson.OutcomeToken(AttemptOutcome.StagingFailed),
            JournalJson.OutcomeToken(AttemptOutcome.GuardrailFailed)
        ];

        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The enum's own convention, and it is load-bearing: <c>AttemptOutcome</c> is persisted, so inserting
    /// a member anywhere but the end renumbers every outcome after it. The file says so on
    /// <see cref="AttemptOutcome.NoRoute"/>; this asserts it, because a comment does not survive the next
    /// author sorting the list alphabetically.
    /// </summary>
    [Fact]
    public void TheNewOutcomes_WereAppended_SoNoExistingOrdinalMoved()
    {
        Assert.Equal(0, (int)AttemptOutcome.Succeeded);
        Assert.Equal(1, (int)AttemptOutcome.ActionFailed);
        Assert.Equal(2, (int)AttemptOutcome.GuardrailFailed);

        // The three additions sit after every previously-declared member.
        int highestPreExisting = (int)AttemptOutcome.NoRoute;
        Assert.True((int)AttemptOutcome.HarnessWriteRejected > highestPreExisting);
        Assert.True((int)AttemptOutcome.WriteScopeViolation > highestPreExisting);
        Assert.True((int)AttemptOutcome.StagingFailed > highestPreExisting);
    }
}
