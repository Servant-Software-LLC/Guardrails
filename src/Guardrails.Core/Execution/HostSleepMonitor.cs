using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Guardrails.Core.Execution;

/// <summary>Where an attempt started, on the monitor's clocks (#810): <see cref="HostSleepMonitor.Mark"/>.</summary>
/// <param name="Wall">The wall clock at the mark.</param>
/// <param name="Slept">All sleep detected before the mark.</param>
public readonly record struct HostSleepMark(DateTimeOffset Wall, TimeSpan Slept);

/// <summary>One detected host sleep (#810): it happened between two checks, <paramref name="From"/> and <paramref name="To"/>.</summary>
/// <param name="From">The wall-clock time of the last check before the sleep.</param>
/// <param name="To">The wall-clock time of the first check after it.</param>
/// <param name="SleptFor">How long the host was asleep inside that window (wall time minus awake time).</param>
/// <param name="Reported">
/// True when the sleep reached <see cref="HostSleepMonitor.Threshold"/> and is reported (the event row, the console
/// line). A shorter gap is still counted in every total, so an attempt's awake time matches what its timeout measured.
/// </param>
public sealed record HostSleepEvent(DateTimeOffset From, DateTimeOffset To, TimeSpan SleptFor, bool Reported = true);

/// <summary>
/// Detects that the HOST was asleep during a run (#810, SSOT §7 / §8.1) by comparing two clocks on a heartbeat: the
/// wall clock, which advances while the machine sleeps, and an AWAKE clock, which does not. Their difference between
/// two checks is time the machine spent asleep; a difference of <see cref="Threshold"/> or more is reported.
///
/// <para><b>Why two clocks and not #517's poll gap.</b> <c>StallWatch</c> only has to DISCRIMINATE (was that gap a
/// suspend?), and the poll gap answers that on every OS. This monitor reports HOW LONG the host slept, and that number
/// lands in <c>run.json</c>, <c>events.jsonl</c> and the attempt summary, so it is measured, not estimated from an
/// interval. The awake clock per OS (<see cref="AwakeClock"/>):</para>
/// <list type="bullet">
///   <item>macOS: <see cref="Stopwatch.GetTimestamp"/>, which .NET maps to <c>CLOCK_UPTIME_RAW</c>. It stops while the
///         Mac sleeps.</item>
///   <item>Linux: <see cref="Stopwatch.GetTimestamp"/>, <c>CLOCK_MONOTONIC</c>. It stops during suspend
///         (<c>CLOCK_BOOTTIME</c> is the one that does not).</item>
///   <item>Windows: <c>QueryUnbiasedInterruptTime</c>. The Stopwatch there (QPC) and <c>Environment.TickCount64</c>
///         both keep counting through sleep; measured on the maintainer's machine they were about 15 hours ahead of
///         unbiased time after 4.8 days of uptime (see <c>StallWatch</c>).</item>
/// </list>
///
/// <para><b>Limits.</b> A wall-clock STEP (an NTP correction, a manual clock change) of a minute or more forward reads as
/// a sleep; a backward step reads as nothing. A sleep shorter than <see cref="Threshold"/> is not reported.</para>
///
/// <para>Thread-safe. <see cref="Check"/> raises <see cref="Slept"/> while holding its lock, so when any
/// <see cref="Check"/> returns, every sleep detected before it has been fully handled — the attempt that calls it at
/// the end of its action reads a <c>run.json</c> that already carries the sleep.</para>
/// </summary>
public sealed class HostSleepMonitor
{
    /// <summary>The smallest wall-minus-awake gap reported as a sleep.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(60);

    /// <summary>A gap no larger than this is the two clocks' reading jitter, not sleep.</summary>
    public static readonly TimeSpan Jitter = TimeSpan.FromSeconds(1);

    /// <summary>How often the production heartbeat checks, in awake time.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private readonly Func<DateTimeOffset> _wall;
    private readonly Func<TimeSpan> _awake;
    private readonly object _gate = new();
    private DateTimeOffset _lastWall;
    private TimeSpan _lastAwake;
    private TimeSpan _totalSlept;

    /// <summary>A monitor over injected clocks: tests move them; production uses <see cref="ForThisMachine"/>.</summary>
    public HostSleepMonitor(Func<DateTimeOffset> wall, Func<TimeSpan> awake, TimeSpan? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(awake);

        _wall = wall;
        _awake = awake;
        Threshold = threshold ?? DefaultThreshold;
        _lastWall = wall();
        _lastAwake = awake();
    }

    /// <summary>A monitor on this machine's wall clock and its <see cref="AwakeClock"/>.</summary>
    public static HostSleepMonitor ForThisMachine() => new(() => DateTimeOffset.UtcNow, AwakeClock.Now);

    /// <summary>The smallest gap reported.</summary>
    public TimeSpan Threshold { get; }

    /// <summary>
    /// Raised on every detected sleep above <see cref="Jitter"/>, reported or not (<see cref="HostSleepEvent.Reported"/>),
    /// inside <see cref="Check"/>'s lock. A handler must not call <see cref="Check"/>.
    /// </summary>
    public event Action<HostSleepEvent>? Slept;

    /// <summary>
    /// A point to measure an attempt from: the wall clock now and the sleep detected so far. <see cref="Since"/> turns it
    /// into the attempt's wall, slept and awake time.
    /// </summary>
    public HostSleepMark Mark()
    {
        lock (_gate)
        {
            // A sleep that ended before the mark belongs to whatever was running then, never to what starts now.
            Check();
            return new HostSleepMark(_wall(), _totalSlept);
        }
    }

    /// <summary>
    /// Wall, slept and awake time since <paramref name="mark"/>, after a <see cref="Check"/> so a sleep that has just
    /// ended is counted. Awake is wall minus slept, never negative.
    /// </summary>
    public (TimeSpan Wall, TimeSpan Slept, TimeSpan Awake) Since(HostSleepMark mark)
    {
        Check();
        lock (_gate)
        {
            TimeSpan wall = _wall() - mark.Wall;
            TimeSpan slept = _totalSlept - mark.Slept;
            return (wall, slept, wall > slept ? wall - slept : TimeSpan.Zero);
        }
    }

    /// <summary>All sleep detected so far.</summary>
    public TimeSpan TotalSlept
    {
        get { lock (_gate) { return _totalSlept; } }
    }

    /// <summary>
    /// Compare the two clocks since the previous check. Any gap above <see cref="Jitter"/> is counted and raised; one of
    /// <see cref="Threshold"/> or more is also returned (the reported sleep), else null.
    /// </summary>
    public HostSleepEvent? Check()
    {
        lock (_gate)
        {
            DateTimeOffset wallNow = _wall();
            TimeSpan awakeNow = _awake();
            TimeSpan gap = (wallNow - _lastWall) - (awakeNow - _lastAwake);
            DateTimeOffset from = _lastWall;
            _lastWall = wallNow;
            _lastAwake = awakeNow;

            // Every gap above scheduling jitter is sleep and is counted (#810 review W3): several 59 s DarkWake sleeps
            // add up. Only a gap of the threshold or more is REPORTED as an event row and a console line.
            if (gap <= Jitter)
            {
                return null;
            }

            var sleep = new HostSleepEvent(from, wallNow, gap, Reported: gap >= Threshold);
            _totalSlept += gap;
            Slept?.Invoke(sleep);
            return sleep.Reported ? sleep : null;
        }
    }

    /// <summary>
    /// The heartbeat: <see cref="Check"/> every <paramref name="interval"/> until <paramref name="cancellationToken"/>
    /// ends. <paramref name="delay"/> is injected so a test drives the loop without sleeping; production passes
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. A handler fault is swallowed: reporting a sleep must
    /// never stop the run.
    /// </summary>
    public async Task WatchAsync(
        TimeSpan interval, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delay);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                Check();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A journal or observer write that failed must not end the heartbeat, still less the run.
            }
        }
    }
}

/// <summary>
/// A clock that does NOT advance while the host sleeps (#810), as elapsed time since an arbitrary origin. See
/// <see cref="HostSleepMonitor"/> for the per-OS source and why.
/// </summary>
public static class AwakeClock
{
    /// <summary>The current reading.</summary>
    public static TimeSpan Now() =>
        OperatingSystem.IsWindows() && NativeMethods.QueryUnbiasedInterruptTime(out ulong unbiased)
            ? TimeSpan.FromTicks((long)unbiased)
            : Stopwatch.GetElapsedTime(0);

    private static class NativeMethods
    {
        /// <summary>Interrupt time in 100 ns units, EXCLUDING time spent in sleep or hibernation.</summary>
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
    }
}

/// <summary>The one wording of a host sleep (#810), shared by every console surface so the live and plain views agree.</summary>
public static class HostSleepText
{
    /// <summary>
    /// <c>the host was asleep for 6h00m (between 11:03:52 and 17:04:10 UTC); in flight: 02-x/attempt-1 — sleep does not
    /// count against an attempt's timeout</c>.
    /// </summary>
    public static string Line(DateTimeOffset from, DateTimeOffset to, TimeSpan sleptFor, IReadOnlyList<string> inFlight)
    {
        string running = inFlight.Count == 0 ? "no attempt was in flight" : $"in flight: {string.Join(", ", inFlight)}";
        return $"the host was asleep for {Duration(sleptFor)} (between {from.UtcDateTime:HH:mm:ss} and " +
               $"{to.UtcDateTime:HH:mm:ss} UTC); {running} — sleep does not count against an attempt's timeout";
    }

    /// <summary>A compact duration: <c>45s</c>, <c>8m12s</c>, <c>6h04m</c>.</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:D2}m"
            : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds:D2}s"
            : $"{(int)d.TotalSeconds}s";
    }
}
