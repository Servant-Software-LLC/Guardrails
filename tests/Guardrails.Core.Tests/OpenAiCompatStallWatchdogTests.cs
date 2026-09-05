using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #517 at the runner that still had it. The suspend discrimination shipped into
/// <see cref="ClaudePromptRunner"/> alone; <see cref="OpenAiCompatPromptRunner"/>'s watchdog kept the
/// naked <c>if (SilentFor() &lt; bound) continue;</c> — so a machine asleep longer than the bound woke to
/// a watchdog that abandoned the turn instantly and reported <c>stalled</c>, naming a bound the turn never
/// violated. That runner is the LOCAL-INFERENCE one, i.e. exactly what an unattended overnight run
/// (#511's whole point) is sitting on when the laptop sleeps.
///
/// <para><b>These drive the REAL watchdog</b> — <c>OpenAiCompatPromptRunner.StartStallWatchdog</c>, the
/// production factory — over an injected clock and an injected delay. Nothing sleeps and nothing asserts
/// elapsed time: the observable is whether the watchdog CANCELLED the turn, which is the decision under
/// test. A wall-clock assertion here could not tell a wrong rule from a busy machine.</para>
/// </summary>
public sealed class OpenAiCompatStallWatchdogTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A clock and a delay in one: each "poll" advances the clock by the next scripted amount instead of
    /// waiting, and running out of script ends the loop the way a finished turn does — WITHOUT cancelling
    /// the source, so <c>IsCancellationRequested</c> stays a clean answer to "did the watchdog kill it?".
    /// </summary>
    private sealed class ScriptedClock(params TimeSpan[] gaps)
    {
        private long _ticks = DateTime.UtcNow.Ticks;
        private int _polls;

        internal long Now() => _ticks;

        internal Task Delay(TimeSpan interval, CancellationToken cancellationToken)
        {
            _ = interval;
            _ = cancellationToken;

            if (_polls >= gaps.Length)
            {
                // The turn finished. The watchdog treats this exactly as it treats a cancelled Task.Delay.
                throw new OperationCanceledException();
            }

            _ticks += gaps[_polls++].Ticks;
            return Task.CompletedTask;
        }
    }

    /// <summary>The same gap, repeated — one poll per entry.</summary>
    private static TimeSpan[] Repeat(TimeSpan gap, int count) => [.. Enumerable.Repeat(gap, count)];

    [Fact]
    public async Task ASuspendedMachineDoesNotAbandonTheTurn()
    {
        // Two hours of machine sleep between two polls of a one-minute loop, then ordinary polls. The turn
        // must survive: it had no opportunity to stream, so counting that gap as silence is measuring
        // elapsed time again with a smaller number — the thing #504 removed.
        var clock = new ScriptedClock([TimeSpan.FromHours(2), .. Repeat(TimeSpan.FromMinutes(1), 5)]);
        var watch = new StallWatch(Bound, clock.Now);
        using var cts = new CancellationTokenSource();

        await OpenAiCompatPromptRunner.StartStallWatchdog(watch, cts, clock.Delay)!;

        Assert.False(cts.IsCancellationRequested, "a suspended machine is not a stalled turn");
        Assert.False(watch.Stalled);
        Assert.Equal(1, watch.SuspendsObserved);
    }

    [Fact]
    public async Task AGenuinelySilentTurnIsStillAbandoned()
    {
        // The other half. Polls arriving ON SCHEDULE and no stream frame for longer than the bound is what
        // the bound exists for, and tolerating suspends must not cost it.
        var clock = new ScriptedClock(Repeat(TimeSpan.FromMinutes(1), 25));
        var watch = new StallWatch(Bound, clock.Now);
        using var cts = new CancellationTokenSource();

        await OpenAiCompatPromptRunner.StartStallWatchdog(watch, cts, clock.Delay)!;

        Assert.True(cts.IsCancellationRequested);
        Assert.True(watch.Stalled);
        Assert.Equal(0, watch.SuspendsObserved);
    }

    [Fact]
    public async Task AStallAfterTheResumeIsStillAbandoned()
    {
        // The residual a suspend-tolerant watchdog could quietly acquire: one sleep must not disarm the
        // bound for the rest of the turn. After the resume the turn gets a fresh FULL window, and staying
        // silent through THAT window kills it exactly as if the suspend had never happened.
        var clock = new ScriptedClock([TimeSpan.FromHours(2), .. Repeat(TimeSpan.FromMinutes(1), 25)]);
        var watch = new StallWatch(Bound, clock.Now);
        using var cts = new CancellationTokenSource();

        await OpenAiCompatPromptRunner.StartStallWatchdog(watch, cts, clock.Delay)!;

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(1, watch.SuspendsObserved);
    }

    [Fact]
    public async Task AStreamingTurnIsNeverAbandoned()
    {
        // Bounding SILENCE and not DURATION: a turn that keeps producing frames runs as long as it likes.
        // Local inference on a slow box is the case this protects.
        var clock = new ScriptedClock(Repeat(TimeSpan.FromMinutes(1), 240));
        var watch = new StallWatch(Bound, clock.Now);
        using var cts = new CancellationTokenSource();

        // Every frame beats the watch, exactly as ReadStreamedTurnAsync does per SSE line.
        Task Delay(TimeSpan interval, CancellationToken token)
        {
            Task delayed = clock.Delay(interval, token);
            watch.Beat();
            return delayed;
        }

        await OpenAiCompatPromptRunner.StartStallWatchdog(watch, cts, Delay)!;

        Assert.False(cts.IsCancellationRequested);
        Assert.False(watch.Stalled);
    }

    [Fact]
    public void NoBoundMeansNoWatchdogAtAll()
    {
        // PromptInvocation.StallBound is optional and its absence means "nothing polices silence" — the
        // field's documented contract, not a gap. A watchdog manufactured from a null bound would police
        // every caller that never asked for one.
        using var cts = new CancellationTokenSource();

        Assert.Null(OpenAiCompatPromptRunner.StartStallWatchdog(watch: null, cts));
    }
}
