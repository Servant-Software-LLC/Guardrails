using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// A wall-clock liveness heartbeat for a long-running guardrail (issue #331). Wired into the plan-level
/// phases — the pre-DAG Full Flight Checks (<c>&lt;plan&gt;/preflights/</c>, SSOT §7) and the Terminal Gate
/// (<c>&lt;plan&gt;/guardrails/</c>, §3.3) — via the <see cref="IReVerifyProgress"/> seam. While a guardrail
/// runs it emits, every <see cref="IntervalSeconds"/> seconds, a line like
/// <c>guardrail 03-bats-suite: running (12m30s elapsed, expected ~15m)...</c> so an operator can tell a
/// slow-but-healthy gate (a real test suite doing I/O) from a genuine hang without OS process-tree
/// archaeology. When the guardrail's sidecar sets <c>expectedDurationSeconds</c> (§4.1) the line carries an
/// <c>expected ~Xm</c> hint; once elapsed exceeds it by <see cref="OverBudgetMultiple"/>× it flags
/// <c>over budget, may be stuck</c> — the structural "is it genuinely stuck?" cue the issue asks for.
/// <para>
/// <b>#145 safety.</b> The heartbeat writes plain lines to the injected sink. The plan-level phases run
/// OUTSIDE the Spectre <c>AnsiConsole.Live</c> region (the pre-DAG phase before it is constructed, the
/// terminal phase after it is disposed), so a plain <c>TextWriter</c> line there cannot corrupt an active
/// live table (#145). This heartbeat is deliberately NOT used inside the live region.
/// </para>
/// <para>
/// The re-verify loop is sequential, so at most one guardrail runs at a time; the heartbeat tracks a single
/// "current" guardrail. The formatting logic (<see cref="FormatLine"/>) and the tick (<see cref="Tick"/>)
/// are pure/deterministic given an injected clock, so they are unit-tested without a real timer or any
/// wall-clock wait; production drives <see cref="Tick"/> from a <see cref="Timer"/> started by
/// <see cref="StartConsole"/>.
/// </para>
/// </summary>
public sealed class GuardrailHeartbeat : IReVerifyProgress, IDisposable
{
    /// <summary>Heartbeat cadence — a running guardrail emits a liveness line this often.</summary>
    public const int IntervalSeconds = 15;

    /// <summary>
    /// Over-budget threshold: once elapsed ≥ this multiple of <c>expectedDurationSeconds</c>, the
    /// heartbeat marks the guardrail as possibly stuck (issue #331 comment).
    /// </summary>
    public const int OverBudgetMultiple = 3;

    private readonly Action<string> _emit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private Running? _current;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>The currently-running guardrail: its name, when it started, and its optional hint.</summary>
    private readonly record struct Running(string Name, DateTimeOffset Since, int? ExpectedSeconds);

    /// <param name="emit">Sink for each heartbeat line (e.g. a console <c>WriteLine</c>).</param>
    /// <param name="clock">Injectable UTC clock (defaults to <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <param name="interval">Injectable cadence (defaults to <see cref="IntervalSeconds"/>).</param>
    public GuardrailHeartbeat(Action<string> emit, Func<DateTimeOffset>? clock = null, TimeSpan? interval = null)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _interval = interval ?? TimeSpan.FromSeconds(IntervalSeconds);
    }

    /// <summary>
    /// Build a heartbeat that writes plain lines to <paramref name="output"/> and START its wall-clock
    /// timer. For the plan-level phases, which run outside the live region (#145-safe). The first tick
    /// fires after one interval, so a guardrail finishing in under <see cref="IntervalSeconds"/> seconds
    /// emits nothing. <see cref="Dispose"/> stops the timer.
    /// </summary>
    public static GuardrailHeartbeat StartConsole(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var heartbeat = new GuardrailHeartbeat(line => output.WriteLine(line));
        heartbeat._timer = new Timer(_ => heartbeat.Tick(), null, heartbeat._interval, heartbeat._interval);
        return heartbeat;
    }

    public void GuardrailStarting(GuardrailDefinition guardrail)
    {
        lock (_gate)
        {
            _current = new Running(guardrail.Name, _clock(), guardrail.ExpectedDurationSeconds);
        }
    }

    public void GuardrailCompleted(GuardrailDefinition guardrail)
    {
        lock (_gate)
        {
            // Stop the clock: no heartbeat fires between guardrails or after the last one completes.
            _current = null;
        }
    }

    /// <summary>
    /// Emit one heartbeat line for the currently-running guardrail, or nothing when none is running (or
    /// the heartbeat is disposed). Driven by the timer in production; called directly with an injected
    /// clock in tests. Held under the gate so it never races start/complete.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _current is not { } running)
            {
                return;
            }

            _emit(FormatLine(running.Name, _clock() - running.Since, running.ExpectedSeconds));
        }
    }

    /// <summary>
    /// The heartbeat line for a guardrail named <paramref name="name"/> running for
    /// <paramref name="elapsed"/>, with an optional <paramref name="expectedSeconds"/> hint (SSOT §4.1).
    /// No hint (null / non-positive) ⇒ elapsed only; with a hint ⇒ an <c>expected ~Xm</c> suffix, and once
    /// elapsed reaches <see cref="OverBudgetMultiple"/>× the hint ⇒ an <c>over budget, may be stuck</c>
    /// flag. Pure and deterministic — the unit-test seam.
    /// </summary>
    public static string FormatLine(string name, TimeSpan elapsed, int? expectedSeconds)
    {
        string clock = FormatClock(elapsed);
        if (expectedSeconds is not { } expected || expected <= 0)
        {
            return $"guardrail {name}: running ({clock})...";
        }

        bool overBudget = elapsed.TotalSeconds >= (double)expected * OverBudgetMultiple;
        string hint = overBudget
            ? $"expected ~{FormatExpected(expected)} — over budget, may be stuck"
            : $"expected ~{FormatExpected(expected)}";
        return $"guardrail {name}: running ({clock} elapsed, {hint})...";
    }

    /// <summary>Compact stopwatch text: <c>45s</c>, <c>4m32s</c>, <c>1h04m</c> (mirrors TaskExecutor.FormatDuration).</summary>
    private static string FormatClock(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        }

        return d.TotalMinutes >= 1
            ? $"{(int)d.TotalMinutes}m{d.Seconds:D2}s"
            : $"{(int)d.TotalSeconds}s";
    }

    /// <summary>
    /// Round the expected-duration hint to a legible band: <c>~45s</c> under a minute, <c>~15m</c>
    /// (nearest minute) under an hour, <c>~1h04m</c> above — a rough order-of-magnitude, not a stopwatch.
    /// </summary>
    private static string FormatExpected(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        if (seconds < 3600)
        {
            int minutes = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
            return $"{minutes}m";
        }

        var d = TimeSpan.FromSeconds(seconds);
        return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer?.Dispose();
    }
}
