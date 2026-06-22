using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the Claude error-string → <see cref="PromptFailureKind"/> classification (issues
/// #114/#115/#119). This is the single fragile vendor-string surface; a vendor wording change must
/// fail HERE with a pointer, never silently regress the retry control flow. Output-cap takes
/// precedence over transient; transient over a generic error; an unrecognized error is conservatively
/// <see cref="PromptFailureKind.Error"/> (consumes the budget) — never a false Transient that loops.
/// </summary>
public sealed class ClaudeSignalClassifierTests
{
    [Theory]
    // HTTP statuses (structured signal).
    [InlineData("API Error: 429 Too Many Requests")]
    [InlineData("API Error: 503 Service Unavailable")]
    [InlineData("Error: 529 overloaded_error")]
    // Free-text phrases.
    [InlineData("Anthropic's API is temporarily overloaded")]
    [InlineData("You've hit your session limit · resets 11:20am (America/Chicago)")]
    [InlineData("usage limit reached")]
    [InlineData("rate limit exceeded; please retry")]
    [InlineData("Connection error: connection reset by peer")]
    [InlineData("the service is temporarily unavailable")]
    public void Classify_TransientSignals_AreTransient(string text)
    {
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));
        Assert.True(ClaudeSignalClassifier.IsTransient(text));
    }

    [Theory]
    [InlineData("API Error: Claude's response exceeded the 32000 output token maximum")]
    [InlineData("API Error: Claude's response exceeded the 64000 output token maximum")]
    [InlineData("response hit the maximum output token limit")]
    public void Classify_OutputCapMessage_IsOutputCap(string text) =>
        Assert.Equal(PromptFailureKind.OutputCap, ClaudeSignalClassifier.Classify(text));

    [Fact]
    public void Classify_OutputCap_TakesPrecedence_OverTransient()
    {
        // A message carrying BOTH an output-cap phrase and a transient token classifies as the
        // more-actionable output-cap (the distinct, fixable case) — precedence is explicit.
        const string text = "429-ish noise but the response exceeded the 32000 output token maximum";
        Assert.Equal(PromptFailureKind.OutputCap, ClaudeSignalClassifier.Classify(text));
    }

    [Theory]
    // The free-text message Claude emits as the result text on a turn-budget exhaustion.
    [InlineData("Reached maximum number of turns (50)")]
    [InlineData("Reached maximum number of turns (120)")]
    // The structured terminal subtype, when it flows through the classified text.
    [InlineData("error_max_turns")]
    [InlineData("subtype: error_max_turns")]
    public void Classify_MaxTurnsMessage_IsMaxTurns(string text) =>
        Assert.Equal(PromptFailureKind.MaxTurns, ClaudeSignalClassifier.Classify(text));

    [Fact]
    public void Classify_MaxTurns_IsDistinctFromOutputCap()
    {
        // "maximum number of turns" (too many tool turns) and "output token maximum" (a single
        // response too long) are categorically different budgets — they must classify distinctly so
        // the harness raises the TURN budget for one and steers "write incrementally" for the other.
        Assert.Equal(PromptFailureKind.MaxTurns,
            ClaudeSignalClassifier.Classify("Reached maximum number of turns (50)"));
        Assert.Equal(PromptFailureKind.OutputCap,
            ClaudeSignalClassifier.Classify("response exceeded the 32000 output token maximum"));
    }

    [Theory]
    [InlineData("Tool 'Bash' is not permitted")]
    [InlineData("Syntax error in the generated code")]
    [InlineData("the file already exists")]
    public void Classify_GenuineError_IsError_NeverTransient(string text)
    {
        Assert.Equal(PromptFailureKind.Error, ClaudeSignalClassifier.Classify(text));
        Assert.False(ClaudeSignalClassifier.IsTransient(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_Empty_IsNone(string? text) =>
        Assert.Equal(PromptFailureKind.None, ClaudeSignalClassifier.Classify(text));

    [Fact]
    public void StatusToken_MustBeStandalone_NotASubstringOfABiggerNumber()
    {
        // A "529" buried in a larger number (e.g. a cost like $0.5293, or 14290 tokens) must NOT trip
        // the transient classifier — only a standalone 429/503/529 token does.
        Assert.False(ClaudeSignalClassifier.IsTransient("total_cost_usd 0.5293, num_turns 4"));
        Assert.False(ClaudeSignalClassifier.IsTransient("used 14290 tokens"));
        Assert.True(ClaudeSignalClassifier.IsTransient("HTTP 429"));
    }

    [Fact]
    public void ExtractResetHint_FromSessionLimitMessage_ReturnsTheTime()
    {
        string? hint = ClaudeSignalClassifier.ExtractResetHint(
            "You've hit your session limit · resets 11:20am (America/Chicago)");
        Assert.NotNull(hint);
        Assert.Contains("11:20", hint);
    }

    [Fact]
    public void ExtractResetHint_WhenAbsent_IsNull() =>
        Assert.Null(ClaudeSignalClassifier.ExtractResetHint("overloaded, try again"));
}
