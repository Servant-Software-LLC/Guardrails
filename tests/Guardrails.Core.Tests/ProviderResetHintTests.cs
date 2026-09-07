using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Resolving a provider's reset hint to an instant (issue #511). The value
/// <c>ClaudeSignalClassifier.ExtractResetHint</c> had always parsed and every door had always discarded,
/// on the stated grounds that "timezone/day ambiguity makes that unsafe".
///
/// <para>These tests pin the two things that make the discard unnecessary: the roll-over rule, which is the
/// whole of the day ambiguity; and the fact that an unresolvable hint returns null rather than a guess, so
/// the caller falls back to pure interval polling exactly as the ruling requires.</para>
/// </summary>
public sealed class ProviderResetHintTests
{
    private static readonly DateTimeOffset Evening =
        new(2026, 8, 23, 17, 51, 0, TimeSpan.FromHours(2)); // the instant on the reporting run

    [Fact]
    public void ResolvesAClockTimeLaterToday_ToTodaysInstant()
    {
        // The reported case: "resets 8:30pm" received at 17:51 means tonight.
        DateTimeOffset? resolved = ProviderResetHint.Resolve("8:30pm", Evening);

        Assert.NotNull(resolved);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 20, 30, 0, TimeSpan.FromHours(2)), resolved!.Value);
    }

    [Fact]
    public void ResolvesAClockTimeAlreadyPassed_ToTOMORROW_notToAPastInstant()
    {
        // THE roll-over case, and the one that decides whether this feature works at 3am. "8:30pm" received
        // at 23:10 means tomorrow. Resolving it to a PAST instant produces an instant retry storm; resolving
        // it to today and waiting produces a 23-hour sleep. Both are named in the ruling as the failure
        // modes to avoid, and they are the two sides of getting this single comparison backwards.
        var lateNight = new DateTimeOffset(2026, 8, 23, 23, 10, 0, TimeSpan.FromHours(2));

        DateTimeOffset? resolved = ProviderResetHint.Resolve("8:30pm", lateNight);

        Assert.NotNull(resolved);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 20, 30, 0, TimeSpan.FromHours(2)), resolved!.Value);
        Assert.True(resolved.Value > lateNight, "a resolved reset must always lie strictly ahead of now");
    }

    [Theory]
    // Meridiem is authoritative, and 12 is where a naive "+12 for pm" is wrong in BOTH directions.
    [InlineData("12am", 0)]
    [InlineData("12pm", 12)]
    [InlineData("1am", 1)]
    [InlineData("11pm", 23)]
    // 24-hour input carries no meridiem and is taken as written.
    [InlineData("20:30", 20)]
    [InlineData("07:05", 7)]
    public void ParsesTheHour(string hint, int expectedHour)
    {
        // Anchored at 00:01 so every one of these lies ahead today and no roll-over is involved — this test
        // is about the hour arithmetic alone.
        var justAfterMidnight = new DateTimeOffset(2026, 8, 23, 0, 1, 0, TimeSpan.Zero);

        DateTimeOffset? resolved = ProviderResetHint.Resolve(hint, justAfterMidnight);

        Assert.NotNull(resolved);
        Assert.Equal(expectedHour, resolved!.Value.Hour);
    }

    [Fact]
    public void ReadsTheMinutes_NotJustTheHour()
    {
        DateTimeOffset? resolved = ProviderResetHint.Resolve("11:20am", Evening);

        Assert.NotNull(resolved);
        Assert.Equal(11, resolved!.Value.Hour);
        Assert.Equal(20, resolved.Value.Minute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("later today")]
    [InlineData("13pm")]      // not a clock time — refused rather than guessed at
    public void ReturnsNullRatherThanGuessing(string? hint)
    {
        // Null is not a degraded answer, it is the CONTRACT: the ruling requires that the hint never be a
        // dependency ("never require the hint; it is an optimization"). A null sends the caller to pure
        // interval polling, which is a complete policy on its own.
        Assert.Null(ProviderResetHint.Resolve(hint, Evening));
    }

    [Fact]
    public void PreservesTheOffsetItWasGiven()
    {
        // The instant is built in `now`'s own offset. If this silently normalised to UTC, a hint of "8:30pm"
        // in a UTC+2 zone would resolve two hours late — and since the wait is capped at the probe interval
        // that error would be invisible in behaviour while being wrong in the operator-facing reset line.
        var offset = TimeSpan.FromHours(-7);
        var now = new DateTimeOffset(2026, 8, 23, 9, 0, 0, offset);

        DateTimeOffset? resolved = ProviderResetHint.Resolve("10:00am", now);

        Assert.NotNull(resolved);
        Assert.Equal(offset, resolved!.Value.Offset);
        Assert.Equal(10, resolved.Value.Hour);
    }
}
