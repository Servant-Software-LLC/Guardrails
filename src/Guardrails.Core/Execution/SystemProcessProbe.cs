using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Execution;

/// <summary>
/// The production <see cref="IProcessProbe"/> (issue #704): whether a run's recorded owner is still in the live OS
/// process table, decided from an identity no clock can move.
/// <list type="bullet">
/// <item><b>Windows and macOS</b> keep a process's creation time in the kernel's own process record, and .NET returns
///   that stored value, so every reader sees it to the tick. The identity is the pid plus that start time, compared
///   exactly.</item>
/// <item><b>Linux</b> keeps only how long after boot a process started. .NET turns that into a wall-clock time by adding
///   a boot time it computes once PER READING PROCESS as <c>CLOCK_REALTIME_COARSE</c> minus <c>CLOCK_BOOTTIME</c>
///   (<c>SystemNative_GetBootTimeTicks</c>), so the run that recorded itself and the <c>status</c> that checks it
///   disagree — by milliseconds normally, and by hours in a WSL2 / VM / devcontainer guest whose wall clock steps
///   forward on wake while its boot clock never counted the host's sleep. No tolerance survives that. So on Linux the
///   identity is what the kernel itself stores: the process's start in clock ticks since boot
///   (<c>/proc/&lt;pid&gt;/stat</c> field 22) and the boot it started in (<c>/proc/sys/kernel/random/boot_id</c>).
///   Both are fixed for the life of the process, so the comparison is exact — and a different boot id PROVES a
///   reboot, after which nothing from the old boot is alive.</item>
/// </list>
/// Anything that cannot be decided — a start time the OS will not show (access denied), or an owner recorded without
/// the identity this OS compares — is <see cref="ProcessCheck.CannotTell"/>, never a guess in either direction.
/// </summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    /// <summary>The one instance production uses; the type holds no state.</summary>
    public static SystemProcessProbe Instance { get; } = new();

    private const string LinuxBootIdPath = "/proc/sys/kernel/random/boot_id";

    /// <inheritdoc />
    public ProcessCheck Check(RunOwner owner) =>
        OperatingSystem.IsLinux() ? CheckLinux(owner) : CheckStartTime(owner);

    /// <summary>
    /// The identity THIS process records when it claims a run, read the same way <see cref="Check"/> later reads the
    /// process table — so the two can only differ by what the OS reports. Null when this platform will not give it:
    /// the claim then clears the previous owner rather than naming anyone wrongly, and never crashes the run.
    /// </summary>
    internal static RunOwner? IdentityOfThisProcess(string? host)
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            var owner = new RunOwner { Pid = self.Id, ProcessStartedAt = StartTimeOf(self), Host = host };

            return OperatingSystem.IsLinux()
                ? owner with
                {
                    ProcessStartTicks = ParseStat(File.ReadAllText($"/proc/{self.Id}/stat")).StartTicks,
                    BootId = ReadBootId()
                }
                : owner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows / macOS: the kernel-stored start time, compared exactly — no tolerance, because none is needed.
    /// <para>
    /// An owner carrying the LINUX identity is <see cref="ProcessCheck.CannotTell"/> instead, mirroring what
    /// <see cref="CompareLinux"/> does in the other direction. One plan folder is reachable from both systems —
    /// and WSL2's default host name is the Windows machine name, so the host check does not catch it — while that
    /// owner's <see cref="RunOwner.ProcessStartedAt"/> is the value .NET rebuilt from the wall clock and is
    /// documented as being for the reader only. Comparing it could report EXITED, with a resume command, for a run
    /// that is alive.
    /// </para>
    /// </summary>
    internal static ProcessCheck CompareStartTime(RunOwner recorded, DateTimeOffset observed)
    {
        if (recorded.ProcessStartTicks is not null || recorded.BootId is not null)
        {
            return ProcessCheck.CannotTell;
        }

        return observed == recorded.ProcessStartedAt ? ProcessCheck.Running : ProcessCheck.NotRunning;
    }

    /// <summary>
    /// Linux: the recorded kernel identity against the current boot id and the pid's <c>/proc/&lt;pid&gt;/stat</c> line
    /// (<paramref name="stat"/> is null when no process has the pid). Pure, so every case is exercised on every OS. The
    /// wall-clock <see cref="RunOwner.ProcessStartedAt"/> is deliberately NOT consulted: it is the value a clock step
    /// moves.
    /// </summary>
    internal static ProcessCheck CompareLinux(RunOwner recorded, string currentBootId, string? stat)
    {
        if (recorded.ProcessStartTicks is not { } recordedTicks || recorded.BootId is not { } recordedBootId)
        {
            return ProcessCheck.CannotTell; // recorded without the kernel identity — on another OS, say
        }

        if (!string.Equals(recordedBootId, currentBootId, StringComparison.Ordinal))
        {
            return ProcessCheck.NotRunning; // the machine rebooted since: nothing from that boot is alive
        }

        if (stat is null)
        {
            return ProcessCheck.NotRunning;
        }

        try
        {
            (char state, long startTicks) = ParseStat(stat);

            // A ZOMBIE has exited; the kernel only still lists it because nobody reaped it, and its start ticks are
            // unchanged. Start ticks alone would call it RUNNING — reporting a finished run as in progress, and
            // blocking a resume that has no override.
            if (state == 'Z')
            {
                return ProcessCheck.NotRunning;
            }

            return startTicks == recordedTicks ? ProcessCheck.Running : ProcessCheck.NotRunning;
        }
        catch (FormatException)
        {
            return ProcessCheck.CannotTell;
        }
    }

    /// <summary>
    /// The two fields of a <c>/proc/&lt;pid&gt;/stat</c> line this identity needs: field 3 (<c>state</c>) and field 22
    /// (<c>starttime</c>, clock ticks after boot). Field 2 is the process name in parentheses and may itself contain
    /// spaces and parentheses, so fields are counted from the LAST <c>)</c>: the next token is field 3, which puts
    /// field 22 at index 19.
    /// </summary>
    internal static (char State, long StartTicks) ParseStat(string stat)
    {
        int nameEnd = stat.LastIndexOf(')');
        string[] fields = nameEnd < 0
            ? []
            : stat[(nameEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return fields.Length > 19
            && fields[0].Length == 1
            && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            ? (fields[0][0], ticks)
            : throw new FormatException("not a /proc/<pid>/stat line with state and starttime fields");
    }

    private static ProcessCheck CheckLinux(RunOwner owner)
    {
        try
        {
            string? stat;
            try
            {
                stat = File.ReadAllText($"/proc/{owner.Pid}/stat");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                stat = null; // no process has this pid
            }

            return CompareLinux(owner, ReadBootId(), stat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProcessCheck.CannotTell;
        }
    }

    private static ProcessCheck CheckStartTime(RunOwner owner)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(owner.Pid);
        }
        catch (ArgumentException)
        {
            return ProcessCheck.NotRunning; // no process has this pid
        }

        using (process)
        {
            try
            {
                return CompareStartTime(owner, StartTimeOf(process));
            }
            catch (InvalidOperationException)
            {
                return ProcessCheck.NotRunning; // it exited between the lookup and the read
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
            {
                return ProcessCheck.CannotTell; // something holds the pid, and its start time is not ours to read
            }
        }
    }

    private static string ReadBootId() => File.ReadAllText(LinuxBootIdPath).Trim();

    private static DateTimeOffset StartTimeOf(Process process) =>
        new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
}
