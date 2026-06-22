using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The bounded backoff policy for a transient pause (issue #115): exponential schedule, per-pause
/// cap, cumulative wall-clock budget, and the clamp that never overshoots the remaining budget. The
/// wait is injected so these tests are sleep-free and deterministic.
/// </summary>
public sealed class TransientBackoffTests
{
    /// <summary>A delay that records each requested wait and returns immediately (no real sleep).</summary>
    private static (TransientBackoff backoff, List<TimeSpan> waited) Make(TimeSpan budget)
    {
        var waited = new List<TimeSpan>();
        var backoff = new TransientBackoff(budget, (d, _) => { waited.Add(d); return Task.CompletedTask; });
        return (backoff, waited);
    }

    [Fact]
    public async Task Delays_GrowExponentially_FromTwoSeconds_CappedAtSixty()
    {
        // A generous budget so the cap (not the budget) bounds each delay.
        (TransientBackoff backoff, List<TimeSpan> waited) = Make(TimeSpan.FromHours(1));

        for (int i = 0; i < 8; i++)
        {
            await backoff.PauseAsync(TestContext.Current.CancellationToken);
        }

        // 2, 4, 8, 16, 32, 60 (capped), 60, 60.
        Assert.Equal(2, waited[0].TotalSeconds);
        Assert.Equal(4, waited[1].TotalSeconds);
        Assert.Equal(8, waited[2].TotalSeconds);
        Assert.Equal(16, waited[3].TotalSeconds);
        Assert.Equal(32, waited[4].TotalSeconds);
        Assert.Equal(60, waited[5].TotalSeconds);   // capped
        Assert.Equal(60, waited[6].TotalSeconds);
        Assert.Equal(60, waited[7].TotalSeconds);
    }

    [Fact]
    public void Disabled_WhenBudgetNonPositive()
    {
        (TransientBackoff backoff, _) = Make(TimeSpan.Zero);
        Assert.False(backoff.IsEnabled);
        Assert.False(backoff.CanPauseAgain());
    }

    [Fact]
    public async Task CanPauseAgain_GoesFalse_OnceBudgetIsSpent()
    {
        // Budget = 10s: 2 + 4 = 6 used after two pauses, still under; the next NextDelay clamps to the
        // remaining 4s, and after that the budget is fully spent.
        (TransientBackoff backoff, List<TimeSpan> waited) = Make(TimeSpan.FromSeconds(10));

        Assert.True(backoff.CanPauseAgain());
        await backoff.PauseAsync(TestContext.Current.CancellationToken); // 2  → elapsed 2
        await backoff.PauseAsync(TestContext.Current.CancellationToken); // 4  → elapsed 6
        Assert.True(backoff.CanPauseAgain());

        // Third pause would be 8s but only 4s of budget remains → clamped to 4s, elapsed hits 10.
        Assert.Equal(4, backoff.NextDelay().TotalSeconds);
        await backoff.PauseAsync(TestContext.Current.CancellationToken); // 4  → elapsed 10
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.Elapsed);
        Assert.False(backoff.CanPauseAgain());      // budget spent
        Assert.Equal(3, backoff.PauseCount);
    }

    [Fact]
    public void NextDelay_ClampsToRemainingBudget_NeverOvershoots()
    {
        // A 3s budget against a first delay of 2s leaves 1s; the SECOND NextDelay must clamp to 1s,
        // never the unclamped 4s — the pause never overshoots the named wall-clock bound.
        var backoff = new TransientBackoff(TimeSpan.FromSeconds(3), (_, _) => Task.CompletedTask);
        Assert.Equal(2, backoff.NextDelay().TotalSeconds);
    }
}
