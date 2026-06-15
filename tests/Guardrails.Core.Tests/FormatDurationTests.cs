using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins <see cref="TaskExecutor.FormatDuration"/> — the compact duration stamped onto a task's
/// success summary so unattended/overnight runs are legible the next morning.
/// </summary>
public sealed class FormatDurationTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(3.4, "3.4s")]
    [InlineData(9.95, "10s")]   // rounds to 10.0 → whole-second branch reads cleaner
    [InlineData(42, "42s")]
    [InlineData(59, "59s")]
    public void SubMinute_ShowsSeconds(double seconds, string expected) =>
        Assert.Equal(expected, TaskExecutor.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(60, "1m00s")]
    [InlineData(133, "2m13s")]
    [InlineData(3599, "59m59s")]
    public void Minutes_ShowsMinutesAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, TaskExecutor.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(3600, "1h00m")]
    [InlineData(3840, "1h04m")]
    [InlineData(36000, "10h00m")]
    public void Hours_ShowsHoursAndMinutes(double seconds, string expected) =>
        Assert.Equal(expected, TaskExecutor.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Negative_ClampsToZero() =>
        Assert.Equal("0s", TaskExecutor.FormatDuration(TimeSpan.FromSeconds(-5)));
}
