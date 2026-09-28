using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// #723: the output drain's deadline, as a pure decision over fixed times, with no process and no clock. The drain
/// gives up <see cref="DrainPolicy.IdleGrace"/> after the last line (so output still arriving keeps it waiting),
/// never later than <see cref="DrainPolicy.AbsoluteCap"/> after it began, and within
/// <see cref="DrainPolicy.CancelledBound"/> of a cancellation.
/// </summary>
public sealed class DrainPolicyTests
{
    private static readonly DrainPolicy Policy = new(
        IdleGrace: TimeSpan.FromSeconds(30),
        AbsoluteCap: TimeSpan.FromSeconds(120),
        CancelledBound: TimeSpan.FromSeconds(2),
        Clock: () => throw new InvalidOperationException("the decision must not read a clock"));

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void WithNoOutput_TheDeadlineIsTheIdleGraceFromTheStart()
    {
        Assert.Equal(S(130), Policy.Deadline(start: S(100), lastActivity: S(0), cancelledAt: null));
    }

    [Fact]
    public void ALineArriving_ResetsTheIdleGrace()
    {
        Assert.Equal(S(155), Policy.Deadline(start: S(100), lastActivity: S(125), cancelledAt: null));
    }

    [Fact]
    public void OutputThatNeverStops_IsStillCappedFromTheStart()
    {
        Assert.Equal(S(220), Policy.Deadline(start: S(100), lastActivity: S(219), cancelledAt: null));
    }

    [Fact]
    public void ACancellation_PullsTheDeadlineIn()
    {
        Assert.Equal(S(112), Policy.Deadline(start: S(100), lastActivity: S(105), cancelledAt: S(110)));
    }

    [Fact]
    public void ACancellation_NeverPushesTheDeadlineOut()
    {
        Assert.Equal(S(130), Policy.Deadline(start: S(100), lastActivity: S(0), cancelledAt: S(129)));
    }

    [Fact]
    public void TheProductionBounds_AreThirtySecondsIdle_TwoMinutesInTotal_TwoSecondsOnCancellation()
    {
        Assert.Equal(S(30), DrainPolicy.Default.IdleGrace);
        Assert.Equal(S(120), DrainPolicy.Default.AbsoluteCap);
        Assert.Equal(S(2), DrainPolicy.Default.CancelledBound);
    }

    [Theory]
    [InlineData((int)ProcessRunner.ProcessEnd.Exited, "the process exited,")]
    [InlineData((int)ProcessRunner.ProcessEnd.TimedOut, "the process timed out,")]
    [InlineData((int)ProcessRunner.ProcessEnd.KilledOnCancellation, "the process was killed on cancellation,")]
    public void TheTruncationNote_StatesHowTheProcessEnded_AndClaimsNoCause(int end, string how)
    {
        string note = ProcessRunner.DrainIncompleteNote((ProcessRunner.ProcessEnd)end, S(29.2));

        Assert.Contains($"[guardrails] output truncated: {how} and its output pipes were not closed within 30s.", note, StringComparison.Ordinal);
        Assert.Contains("A process it started may still hold them.", note, StringComparison.Ordinal);
        Assert.Contains("An unterminated last line and any later output were not captured.", note, StringComparison.Ordinal);
    }
}
