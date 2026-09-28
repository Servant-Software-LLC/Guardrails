using System.Globalization;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Bundle;

/// <summary>
/// The host's sleep/wake history for a run's time span (#810, SSOT §17), or why it could not be read. Only macOS keeps
/// one that a user can read without privileges (<c>pmset -g log</c>).
/// </summary>
/// <param name="Lines">The Sleep, Wake and DarkWake lines inside the window, oldest first.</param>
/// <param name="Unavailable">Why there are none to show, or null.</param>
public sealed record BundleSleepWakeLog(IReadOnlyList<string> Lines, string? Unavailable)
{
    /// <summary>The most lines the bundle carries; a DarkWake cycle writes two lines every few minutes.</summary>
    public const int MaxLines = 2000;
}

/// <summary>Filters <c>pmset -g log</c> to the lines #810 needs. Pure, so a test drives it with captured text.</summary>
public static partial class BundleSleepWake
{
    /// <summary>
    /// The Sleep, Wake and DarkWake lines of <paramref name="pmsetLog"/> whose timestamp falls in
    /// [<paramref name="from"/>, <paramref name="to"/>], capped at <see cref="BundleSleepWakeLog.MaxLines"/> (the newest
    /// are kept). A line reads <c>2026-09-26 11:03:52 -0400 Sleep  Entering Sleep state due to …</c>.
    /// </summary>
    public static IReadOnlyList<string> Filter(string pmsetLog, DateTimeOffset from, DateTimeOffset to)
    {
        var kept = new List<string>();
        foreach (string raw in pmsetLog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Match match = EventLine().Match(raw);
            if (!match.Success
                || !DateTimeOffset.TryParseExact(
                    $"{match.Groups["stamp"].Value} {Zone(match.Groups["zone"].Value)}", "yyyy-MM-dd HH:mm:ss zzz",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
                || at < from || at > to)
            {
                continue;
            }

            kept.Add(raw.TrimEnd());
        }

        return kept.Count > BundleSleepWakeLog.MaxLines ? kept[^BundleSleepWakeLog.MaxLines..] : kept;
    }

    [GeneratedRegex(@"^(?<stamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<zone>[+-]\d{2}:?\d{2})\s+(?:Sleep|Wake|DarkWake)(?!\s+Requests)\s",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventLine();

    // pmset writes the zone as -0400; the exact parse wants -04:00.
    private static string Zone(string zone) => zone.Contains(':', StringComparison.Ordinal) ? zone : $"{zone[..3]}:{zone[3..]}";
}

/// <summary>The production probe: <c>pmset -g log</c> on macOS, nothing elsewhere.</summary>
public static class SystemBundleSleepWake
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The window's history on macOS; null on every other OS (the bundle then carries no file).</summary>
    public static BundleSleepWakeLog? Capture(DateTimeOffset from, DateTimeOffset to)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        BundleProcessResult result = BundleProcess.Run("pmset", ["-g", "log"], Directory.GetCurrentDirectory(), Timeout);
        if (!result.Succeeded)
        {
            return new BundleSleepWakeLog([],
                result.NotFound ? "pmset is not on PATH" : result.TimedOut ? "pmset -g log timed out" : $"pmset exited {result.ExitCode}");
        }

        return new BundleSleepWakeLog(BundleSleepWake.Filter(result.StandardOutput, from, to), null);
    }
}
