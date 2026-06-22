using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the SSOT §7 kebab-case spelling of every attempt outcome and its JSON round-trip — including
/// the two added for issues #114/#115: <c>output-cap</c> and <c>rate-limited</c>. The journal is a
/// contract; a token rename breaks resume of an in-flight run, so it must be deliberate.
/// </summary>
public sealed class AttemptOutcomeTokenTests
{
    [Theory]
    [InlineData(AttemptOutcome.Succeeded, "succeeded")]
    [InlineData(AttemptOutcome.ActionFailed, "action-failed")]
    [InlineData(AttemptOutcome.GuardrailFailed, "guardrail-failed")]
    [InlineData(AttemptOutcome.Timeout, "timeout")]
    [InlineData(AttemptOutcome.OutputCap, "output-cap")]
    [InlineData(AttemptOutcome.RateLimited, "rate-limited")]
    [InlineData(AttemptOutcome.Cancelled, "cancelled")]
    [InlineData(AttemptOutcome.InvalidFragment, "invalid-fragment")]
    [InlineData(AttemptOutcome.NeedsHuman, "needs-human")]
    public void OutcomeToken_MatchesSsotSpelling(AttemptOutcome outcome, string token) =>
        Assert.Equal(token, JournalJson.OutcomeToken(outcome));

    [Theory]
    [InlineData(AttemptOutcome.OutputCap)]
    [InlineData(AttemptOutcome.RateLimited)]
    public void NewOutcomes_RoundTripThroughJson(AttemptOutcome outcome)
    {
        string json = JsonSerializer.Serialize(outcome, JournalJson.Options);
        AttemptOutcome back = JsonSerializer.Deserialize<AttemptOutcome>(json, JournalJson.Options);
        Assert.Equal(outcome, back);
    }
}
