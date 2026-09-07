using System.Globalization;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Execution;

/// <summary>
/// Turns the operator-facing reset hint a provider limit carries ("resets 8:30pm") into an absolute
/// INSTANT the harness can schedule against (issue #511).
///
/// <para><b>Why this exists at all.</b> <see cref="Prompts.ClaudeSignalClassifier.ExtractResetHint"/> has
/// always parsed this string out of the 429 message, and every door then threw it away — its own comment
/// said so: <i>"advisory only — never parsed into a sleep duration (timezone/day ambiguity makes that
/// unsafe)"</i>. That caution was right about the ambiguity and wrong about the conclusion, because it
/// assumed the instant would be used as a SLEEP. It is not. It is used as one half of</para>
///
/// <code>nextProbe = min(resetInstant, now + probeInterval)</code>
///
/// <para><b>and that <c>min</c> is what makes imperfect parsing safe by construction.</b> The wait can
/// never exceed <c>probeInterval</c>, so an instant resolved too LATE (wrong timezone, wrong day) is
/// capped and costs nothing; an instant resolved too EARLY merely probes sooner, and a probe into a live
/// limit was measured on the reporting run at 1 second and $0.00. The hint can therefore only ever make
/// the harness wait LESS — which is also exactly what it is FOR: the short rate-limit that clears in three
/// minutes, where sleeping a full probe interval would waste the difference.</para>
///
/// <para>So this resolver is deliberately permissive and never throws: an unparseable hint returns
/// <see langword="null"/> and the caller falls back to pure interval polling, which the ruling requires
/// ("never require the hint; it is an optimization, not a dependency").</para>
/// </summary>
public static class ProviderResetHint
{
    // The shapes the hint actually arrives in, all anchored on a clock time: "8:30pm", "11:20 am",
    // "20:30", "8pm". ExtractResetHint has already stripped the "resets" prefix and any trailing zone
    // parenthetical, so this parses the remainder rather than re-scanning the message.
    private static readonly Regex ClockTime = new(
        // The two-digit 24-hour branch MUST come first: .NET alternation is ordered, and with "[01]?[0-9]"
        // leading, "20:30" matches the empty "[01]?" plus "2", leaving ":30" unconsumed and yielding hour 2
        // — an 18-hour-early probe from a hint that parsed "successfully".
        @"^(?<h>2[0-3]|[01]?[0-9])(?::(?<m>[0-5][0-9]))?\s*(?<mer>[ap]\.?m\.?)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolve <paramref name="hint"/> to the NEXT instant naming that clock time at or after
    /// <paramref name="now"/>, or null when the hint carries no parseable time.
    ///
    /// <para><b>Roll-over is the whole difficulty.</b> A hint of "8:30pm" received at 23:10 means
    /// TOMORROW. Getting that backwards produces either an instant retry storm (resolving to a past
    /// instant) or a 23-hour sleep (resolving a past instant to today and waiting a full day) — the two
    /// failure modes the maintainer's ruling names explicitly. So the rule is simply: build the instant on
    /// <paramref name="now"/>'s own date, and if it does not lie strictly ahead, add one day.</para>
    ///
    /// <para><b>Zone.</b> The hint is resolved in <paramref name="now"/>'s offset — the provider renders
    /// these times in the reader's own zone, and the harness runs on the reader's machine. When that
    /// assumption is wrong the <c>min</c> above absorbs it (see the type remarks); it is not worth a
    /// timezone database to be occasionally right about a value that is capped either way.</para>
    /// </summary>
    public static DateTimeOffset? Resolve(string? hint, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        Match m = ClockTime.Match(hint.Trim());
        if (!m.Success)
        {
            return null;
        }

        if (!int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hour))
        {
            return null;
        }

        int minute = 0;
        if (m.Groups["m"].Success
            && !int.TryParse(m.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minute))
        {
            return null;
        }

        // Meridiem, when present, is authoritative: "12am" is hour 0 and "12pm" is hour 12, which is the
        // one place a naive +12 is wrong in both directions.
        if (m.Groups["mer"].Success)
        {
            bool pm = m.Groups["mer"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            if (hour > 12)
            {
                return null; // "13pm" — not a clock time; treat as no hint rather than guess.
            }

            hour = hour % 12 + (pm ? 12 : 0);
        }

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        return candidate > now ? candidate : candidate.AddDays(1);
    }
}
