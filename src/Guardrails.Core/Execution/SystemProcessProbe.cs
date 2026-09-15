using System.ComponentModel;
using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// The production <see cref="IProcessProbe"/> (issue #704): the live OS process table, read through
/// <see cref="Process.GetProcessById(int)"/> and the process's start time — the same pid-plus-start-time identity
/// the #407 worktree lock records.
/// </summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    /// <summary>The one instance production uses; the type holds no state.</summary>
    public static SystemProcessProbe Instance { get; } = new();

    /// <summary>
    /// How far apart two readings of the SAME process's start time may be and still name that process.
    /// <para>
    /// <b>Zero on Windows and macOS.</b> Both keep the creation time in the kernel's own process record, and .NET
    /// returns that stored value, so every reader sees it to the tick.
    /// </para>
    /// <para>
    /// <b>Nonzero on Linux, and it has to be.</b> The kernel records only how long after boot a process started.
    /// .NET turns that into a wall-clock time by adding a boot time it computes once PER READING PROCESS, as
    /// <c>CLOCK_REALTIME_COARSE</c> minus <c>CLOCK_BOOTTIME</c> (<c>SystemNative_GetBootTimeTicks</c>). The run that
    /// recorded itself and the <c>status</c> that checks it are different processes computing that boot time at
    /// different moments, so the coarse clock's resolution (milliseconds) and any wall-clock correction in between
    /// (an NTP step after a resume) move the answer. Exact equality would call a live Linux run dead on almost
    /// every check.
    /// </para>
    /// <para>
    /// <b>Why a minute cannot vouch for a dead run.</b> A false "running" needs a DIFFERENT process holding the
    /// recorded pid with a start time inside this window of the owner's. Linux hands out pids in sequence, so that
    /// takes a full lap of <c>pid_max</c> (at least 32,768) within a minute of the owner starting, with the owner
    /// dying inside that same minute. A minute comfortably absorbs a post-resume clock correction. It does not
    /// absorb a larger wall-clock step while the run is alive — a Linux VM whose clock is corrected by hours after
    /// its host slept, say — and that run reads as exited: the loud direction, stated here rather than left to be
    /// discovered.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance =
        OperatingSystem.IsLinux() ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;

    /// <inheritdoc />
    public bool IsRunning(int pid, DateTimeOffset startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return IsSameStart(StartTimeOf(process), startedAt);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // no such process / it exited under us / its start time is unreadable — not the owner
        }
    }

    /// <summary>
    /// A process's start time in the ONE shape both halves of the identity use — what a run stamps about itself
    /// (<see cref="Journal.RunLiveness.OwnerForThisProcess"/>) and what a later check reads back — so the two can
    /// only differ by what the OS reports, never by how this code converted it.
    /// </summary>
    internal static DateTimeOffset StartTimeOf(Process process) =>
        new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>
    /// The start-time half of the identity: within <see cref="StartTimeTolerance"/>, inclusive. Pure, so the
    /// tolerance is pinned as a decision on every OS rather than raced against a real clock.
    /// </summary>
    internal static bool IsSameStart(DateTimeOffset observed, DateTimeOffset recorded) =>
        (observed - recorded).Duration() <= StartTimeTolerance;
}
