using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #763: a failed session with NO terminal result used to summarize as a bare <c>"claude exited 1"</c>, so the
/// provider's own refusal ("You've hit your individual spend limit · …") reached neither the pause reason, the
/// needs-human line nor the live/status detail. The summary now quotes one line, capped at
/// <see cref="StreamJsonCliSession.NoResultExcerptMaxChars"/>: the first line the classifier recognizes as a signal,
/// else the first stderr line that is not a Node runtime warning, else the first #516-filtered stdout line, else
/// the first stderr line. The end-to-end half — a real process through the real runner — is in
/// <c>CursorPromptRunnerTests.BadModel_ExitsOneWithPlainText_IsAFailedAttempt</c> and the integration
/// <c>PromptRunnerReliabilityTests.LiveSpendLimitRefusal_…</c> and <c>ClaudeRunnerFailureTextTests</c>.
/// </summary>
public sealed class NoResultExitSummaryTests
{
    private const string SpendLimit =
        "You've hit your individual spend limit · run /usage-credits to ask your admin for a higher limit";

    private static ProcessResult Failed(string stdout, string stderr) => new()
    {
        ExitCode = 1,
        StandardOutput = stdout,
        StandardError = stderr,
        TimedOut = false,
        Duration = TimeSpan.Zero
    };

    [Fact]
    public void PlainStdoutRefusal_IsTheExcerpt()
    {
        Assert.Equal(SpendLimit, StreamJsonCliSession.NoResultExcerpt(Failed(SpendLimit + "\n", "")));
    }

    [Fact]
    public void AnUnrecognizedStderrError_WinsOverAnUnrecognizedStdoutLine_AndOnlyItsFirstNonEmptyLineIsTaken()
    {
        string? excerpt = StreamJsonCliSession.NoResultExcerpt(
            Failed("stdout text\n", "\n  \nError: first stderr line\nsecond stderr line\n"));

        Assert.Equal("Error: first stderr line", excerpt);
    }

    [Fact]
    public void ARecognizedSignal_WinsOverAnUnrelatedStderrError()
    {
        // Rule (a): the classifier names the refusal as a specific cause, so it is quoted even though stderr
        // carries a line of its own.
        Assert.Equal(SpendLimit, StreamJsonCliSession.NoResultExcerpt(Failed(SpendLimit, "Error: something else")));
    }

    [Fact]
    public void ANodeRuntimeWarning_IsSkipped_ForTheNextStderrLine()
    {
        string? excerpt = StreamJsonCliSession.NoResultExcerpt(Failed("",
            "(node:1234) [DEP0040] DeprecationWarning: The `punycode` module is deprecated.\nError: invalid API key"));

        Assert.Equal("Error: invalid API key", excerpt);
    }

    [Fact]
    public void ANodeRuntimeWarning_IsSkipped_ForAnUnrecognizedStdoutLine()
    {
        // Rule (c): stdout's line is not a known signal, but a warning is never the cause.
        string? excerpt = StreamJsonCliSession.NoResultExcerpt(Failed("Cannot use this model: nope",
            "(node:1234) Warning: something"));

        Assert.Equal("Cannot use this model: nope", excerpt);
    }

    [Fact]
    public void AWarningAlone_IsStillQuoted_RatherThanNothing()
    {
        Assert.Equal("(node:1234) Warning: something",
            StreamJsonCliSession.NoResultExcerpt(Failed("", "(node:1234) Warning: something")));
    }

    [Fact]
    public void StreamEnvelopes_AreNeverQuoted_BecauseTheyAreTheAgentsContentNotTheProvidersWords()
    {
        // The same #516 filter the classifier reads: an assistant envelope that happens to discuss a spend
        // limit must not be presented to the operator as the reason the provider refused.
        string stdout = string.Join("\n",
            "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-x\"}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"the spend limit docs\"}]}}");

        Assert.Null(StreamJsonCliSession.NoResultExcerpt(Failed(stdout, "")));
    }

    [Fact]
    public void AnOverlongLine_IsCapped_WithAnEllipsis()
    {
        string line = new('x', 1000);

        string? excerpt = StreamJsonCliSession.NoResultExcerpt(Failed(line, ""));

        Assert.NotNull(excerpt);
        Assert.Equal(StreamJsonCliSession.NoResultExcerptMaxChars, excerpt!.Length);
        Assert.EndsWith("…", excerpt, StringComparison.Ordinal);
        Assert.StartsWith(new string('x', StreamJsonCliSession.NoResultExcerptMaxChars - 1), excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineAtTheCap_IsKeptWhole()
    {
        string line = new('y', StreamJsonCliSession.NoResultExcerptMaxChars);

        Assert.Equal(line, StreamJsonCliSession.NoResultExcerpt(Failed(line, "")));
    }

    [Fact]
    public void NothingToQuote_IsNull()
    {
        Assert.Null(StreamJsonCliSession.NoResultExcerpt(Failed("", "  \n")));
    }
}
