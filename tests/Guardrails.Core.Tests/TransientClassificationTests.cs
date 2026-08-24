using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The two transient-classification defects found on the Stage 3 dogfood: #517 (the stall bound counted
/// machine SLEEP as silence) and #516 (the classifier's fallback scanned the whole teed stdout, making the
/// harness's own source a false-positive trigger for its own classifier).
/// </summary>
public sealed class TransientClassificationTests
{
    // ---- #517: suspended is not silent ------------------------------------------------------------

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);   // Bound / 20, as the runner computes

    [Fact]
    public void AMachineThatSleptIsNotAStalledSession()
    {
        // The reported case: a laptop asleep for two hours. On resume the wall clock shows far more than
        // the bound of "silence" — but the session had no opportunity to emit, and killing it here reports
        // a bound it never violated.
        ClaudePromptRunner.StallVerdict v = ClaudePromptRunner.ClassifySilence(
            silent: TimeSpan.FromHours(2), sincePreviousPoll: TimeSpan.FromHours(2), Poll, Bound);

        Assert.Equal(ClaudePromptRunner.StallVerdict.Suspended, v);
    }

    [Fact]
    public void AGenuineStallIsStillKilled()
    {
        // The other half: polls arriving on schedule, and nothing on the stream for longer than the bound.
        // That is what the bound exists for and it must still fire.
        ClaudePromptRunner.StallVerdict v = ClaudePromptRunner.ClassifySilence(
            silent: Bound + TimeSpan.FromMinutes(1), sincePreviousPoll: Poll, Poll, Bound);

        Assert.Equal(ClaudePromptRunner.StallVerdict.Stalled, v);
    }

    [Fact]
    public void AHealthySessionIsLeftAlone()
    {
        ClaudePromptRunner.StallVerdict v = ClaudePromptRunner.ClassifySilence(
            silent: TimeSpan.FromMinutes(3), sincePreviousPoll: Poll, Poll, Bound);

        Assert.Equal(ClaudePromptRunner.StallVerdict.KeepWaiting, v);
    }

    [Theory]
    [InlineData(2)]      // a slow poll — scheduling jitter, NOT a suspend
    [InlineData(3)]
    public void OrdinarySchedulingJitterIsNotMistakenForASuspend(int factor)
    {
        // The separation is orders of magnitude (a 2-hour gap in a 1-minute loop), so the factor only has
        // to clear the worst delay a RUNNING machine imposes. A poll arriving 2-3x late is still a running
        // machine, and a genuine stall underneath it must still be caught.
        ClaudePromptRunner.StallVerdict v = ClaudePromptRunner.ClassifySilence(
            silent: Bound + TimeSpan.FromMinutes(1), sincePreviousPoll: Poll * factor, Poll, Bound);

        Assert.Equal(ClaudePromptRunner.StallVerdict.Stalled, v);
    }

    [Fact]
    public void WhenTheTwoAreAmbiguous_ItWaits_BecauseKillingHealthyWorkIsTheWorseError()
    {
        // A suspend verdict costs one more bound-length window before a real stall is killed. A stall
        // verdict on a suspended machine kills work that was fine — the exact defect #504 removed. The
        // tie must break toward waiting, and this pins that direction.
        ClaudePromptRunner.StallVerdict v = ClaudePromptRunner.ClassifySilence(
            silent: TimeSpan.FromDays(1), sincePreviousPoll: TimeSpan.FromDays(1), Poll, Bound);

        Assert.NotEqual(ClaudePromptRunner.StallVerdict.Stalled, v);
    }

    // ---- #516: the classifier must not read the agent's own reading -------------------------------

    // PromptFailureKind.cs's doc comment names every pinned transient phrase, and agents read it while
    // doing observer work. The SSOT does too (measured: 9 matches), and EVERY wave's docs-sink task must
    // read the SSOT to do its job. If that content reaches the classifier, those tasks are misdiagnosed as
    // provider outages whenever they fail without a terminal result.
    private const string HarnessDocComment =
        "an HTTP 429/503/529, an \"overloaded\" response, or a usage/session/rate limit";

    private static string StreamWith(string agentText) => string.Join("\n",
        "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-x\"}",
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + agentText + "\"}]}}",
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + agentText + "\"}]}}");

    [Fact]
    public void Guard_ThatTextReallyDoesTripTheClassifier()
    {
        // Without this the test below could pass for the wrong reason — a filter that works only because
        // the sample was never transient-looking in the first place.
        Assert.True(ClaudeSignalClassifier.IsTransient(HarnessDocComment));
    }

    [Fact]
    public void AgentContentIsFilteredOutOfTheFallback_SoReadingTheHarnessSourceIsNotAnOutage()
    {
        string? classified = ClaudePromptRunner.NonStreamStdout(StreamWith(HarnessDocComment));

        // Every line was a well-formed stream envelope carrying agent content, so nothing survives to be
        // classified — which is the point: this fallback exists for output that is NOT a stream at all.
        Assert.False(
            ClaudeSignalClassifier.IsTransient(classified),
            "agent content reached the transient classifier — a task that merely READ the harness's own "
            + "source would be misdiagnosed as a provider outage (#516).");
    }

    [Fact]
    public void ARealRejectionStillReachesTheClassifier()
    {
        // The other half, and the reason a blunt "ignore stdout" fix would be wrong: #115's instant
        // rejection prints BEFORE any envelope, so it is not a stream line and must survive the filter.
        string stdout = "Overloaded: too many requests\n"
                        + "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-x\"}";

        Assert.True(ClaudeSignalClassifier.IsTransient(ClaudePromptRunner.NonStreamStdout(stdout)));
    }

    [Fact]
    public void TheTerminalResultEnvelopeSurvives_BecauseItNamesTheStopReason()
    {
        string stdout = string.Join("\n",
            "{\"type\":\"assistant\",\"message\":{\"content\":[]}}",
            "{\"type\":\"result\",\"is_error\":true,\"result\":\"API Error: 529 Overloaded\"}");

        Assert.True(ClaudeSignalClassifier.IsTransient(ClaudePromptRunner.NonStreamStdout(stdout)));
    }
}
