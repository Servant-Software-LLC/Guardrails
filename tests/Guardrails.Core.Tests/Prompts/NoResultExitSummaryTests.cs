using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// #763: a failed session with NO terminal result used to summarize as a bare <c>"claude exited 1"</c>, so the
/// provider's own refusal ("You've hit your individual spend limit · …") reached neither the pause reason, the
/// needs-human line nor the live/status detail. The summary now carries the first non-empty line of stderr,
/// else of the #516-filtered stdout, capped at <see cref="StreamJsonCliSession.NoResultExcerptMaxChars"/>.
/// The end-to-end half — a real process through the real runner — is in
/// <c>CursorPromptRunnerTests.BadModel_ExitsOneWithPlainText_IsAFailedAttempt</c> and the integration
/// <c>PromptRunnerReliabilityTests.LiveSpendLimitRefusal_…</c>.
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
    public void Stderr_WinsOverStdout_AndOnlyItsFirstNonEmptyLineIsTaken()
    {
        string? excerpt = StreamJsonCliSession.NoResultExcerpt(
            Failed("stdout text\n", "\n  \nError: first stderr line\nsecond stderr line\n"));

        Assert.Equal("Error: first stderr line", excerpt);
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
