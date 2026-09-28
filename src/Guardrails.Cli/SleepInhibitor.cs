using System.Diagnostics;

namespace Guardrails.Cli;

/// <summary>
/// Keeps a Mac from IDLE-sleeping while <c>guardrails run</c> is running (#810), the way <c>caffeinate -i -w &lt;pid&gt;</c>
/// does, by starting exactly that. <c>-w</c> ties the assertion to this process: caffeinate exits when the run's process
/// exits, however it exits, so a crashed run cannot leave the Mac held awake. On by default; <c>--allow-sleep</c> turns it
/// off.
///
/// <para><b>What it cannot do.</b> An idle-sleep assertion does not stop a lid-close sleep on battery, and it does not
/// stop the user choosing Sleep. Those still happen; the host-sleep monitor then reports them (#810).</para>
///
/// <para><b>Other OSes.</b> Linux (<c>systemd-inhibit</c>) and Windows (<c>SetThreadExecutionState</c>) are follow-ups:
/// <c>systemd-inhibit</c> must wrap a command rather than watch a pid, and the Windows flag is per thread, so neither is
/// a one-line equivalent. On those OSes this does nothing and says nothing.</para>
/// </summary>
public static class SleepInhibitor
{
    /// <summary>Where macOS installs caffeinate.</summary>
    public const string SystemCaffeinate = "/usr/bin/caffeinate";

    /// <summary>The line printed when the assertion is held.</summary>
    public const string HeldNotice =
        "Keeping this Mac awake while the run is in progress (caffeinate -i); pass --allow-sleep to let it idle-sleep. " +
        "Closing the lid on battery still sleeps.";

    /// <summary>
    /// Take the assertion when this is a Mac and sleep was not allowed; returns the handle to dispose at the end of the
    /// run, or null when nothing was taken.
    /// </summary>
    /// <param name="allowSleep"><c>--allow-sleep</c>.</param>
    /// <param name="output">Where the one-line notice goes.</param>
    /// <param name="isMacOS">The platform; injected so a test can decide without being on a Mac.</param>
    /// <param name="start">Starts the process; injected so a test can see the command without running it.</param>
    public static IDisposable? Acquire(
        bool allowSleep, TextWriter output, bool? isMacOS = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (allowSleep || !(isMacOS ?? OperatingSystem.IsMacOS()))
        {
            return null;
        }

        // The system binary by absolute path when it is there, so a `caffeinate` earlier on PATH cannot stand in for it.
        var info = new ProcessStartInfo(File.Exists(SystemCaffeinate) ? SystemCaffeinate : "caffeinate")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            Process? process = (start ?? Process.Start)(info);
            if (process is null)
            {
                output.WriteLine("Could not start caffeinate; this Mac may idle-sleep during the run.");
                return null;
            }

            output.WriteLine(HeldNotice);
            return new Handle(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.WriteLine($"Could not start caffeinate ({ex.Message}); this Mac may idle-sleep during the run.");
            return null;
        }
    }

    private sealed class Handle(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone: caffeinate -w exits with the run's process anyway.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
