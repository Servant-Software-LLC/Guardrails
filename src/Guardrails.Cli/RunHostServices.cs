using Guardrails.Core.Execution;

namespace Guardrails.Cli;

/// <summary>
/// The host-facing services <c>guardrails run</c> composes (#810): the macOS keep-awake assertion and the host-sleep
/// monitor with its heartbeat. Injected so the composition-root tests can prove each one is wired, without a real
/// <c>caffeinate</c> or a real sleep.
/// </summary>
public sealed record RunHostServices
{
    /// <summary>The production services.</summary>
    public static readonly RunHostServices Production = new();

    /// <summary>Takes the keep-awake assertion (<c>--allow-sleep</c>, the notice writer); returns what to dispose, or null.</summary>
    public Func<bool, TextWriter, IDisposable?> KeepAwake { get; init; } = (allowSleep, output) => SleepInhibitor.Acquire(allowSleep, output);

    /// <summary>The run's host-sleep monitor.</summary>
    public Func<HostSleepMonitor> HostSleep { get; init; } = HostSleepMonitor.ForThisMachine;

    /// <summary>How often the heartbeat checks, in awake time.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = HostSleepMonitor.DefaultInterval;

    /// <summary>The heartbeat's wait between checks; a test observes that the heartbeat is running through it.</summary>
    public Func<TimeSpan, CancellationToken, Task> HeartbeatDelay { get; init; } = Task.Delay;
}
