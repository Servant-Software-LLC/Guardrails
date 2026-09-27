using System.Text;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Bundle;

/// <summary>The identity values pass 3 replaces besides paths (SSOT §17.6.3). Each is optional.</summary>
/// <param name="UserName">The OS user name.</param>
/// <param name="GitUserName">git's <c>user.name</c>, read at bundle time.</param>
/// <param name="GitEmail">git's <c>user.email</c>, read at bundle time.</param>
public sealed record BundleIdentity(string? UserName, string? GitUserName = null, string? GitEmail = null);

/// <summary>
/// Pass 3 of SSOT §17.6.3, default on (<c>--keep-paths</c> disables it): the home directory becomes <c>~</c>, the
/// workspace <c>&lt;workspace&gt;</c>, the worktree root <c>&lt;worktrees&gt;</c>, the OS user name <c>&lt;user&gt;</c>, git's
/// <c>user.name</c> / <c>user.email</c> <c>&lt;git-user&gt;</c> / <c>&lt;git-email&gt;</c>, and the recorded host names
/// <c>&lt;host&gt;</c>.
/// <para>
/// It scans the same decoded views as passes 1 and 2 (<see cref="BundleRedactor"/>): the text, its JSON-unescaped forms
/// and their percent-decoded forms. So <c>C:\\Users\\Jos\u00e9</c> written by System.Text.Json, <c>O\u0027Brien</c>,
/// and <c>file:///c%3A/Users/…</c> are all found, and each hit is replaced in the original characters it came from,
/// escape-aligned. Roots are matched with native, forward and back slashes and in the <c>-</c>-encoded form Claude Code
/// names a project directory with; case-insensitively where the file system is. A <c>[REDACTED:…]</c> label is never
/// rewritten.
/// </para>
/// </summary>
public sealed partial class BundlePathAnonymizer
{
    private readonly List<(string From, string To)> _roots = [];
    private readonly List<(Regex Regex, string To)> _patterns = [];
    private readonly StringComparison _comparison;

    /// <summary>Build the anonymizer for paths and the OS user name alone.</summary>
    public BundlePathAnonymizer(
        string? home, string? workspace, string? worktreeRoot, string? userName, IEnumerable<string?> hosts,
        bool caseInsensitive)
        : this(home, workspace, worktreeRoot, new BundleIdentity(userName), hosts, caseInsensitive)
    {
    }

    /// <summary>Build the anonymizer. Null or blank inputs are skipped.</summary>
    public BundlePathAnonymizer(
        string? home, string? workspace, string? worktreeRoot, BundleIdentity identity, IEnumerable<string?> hosts,
        bool caseInsensitive)
    {
        _comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seen = new HashSet<string>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string? path, string token) in new[] { (worktreeRoot, "<worktrees>"), (workspace, "<workspace>"), (home, "~") })
        {
            if (string.IsNullOrWhiteSpace(path) || path.TrimEnd('/', '\\') is not { Length: > 1 } trimmed)
            {
                continue;
            }

            foreach (string spelling in new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\'), Encode(trimmed) })
            {
                if (seen.Add(spelling))
                {
                    _roots.Add((spelling, token));
                }
            }
        }

        // Longest first, so a workspace under the home directory becomes <workspace>, not ~/….
        _roots.Sort((a, b) => b.From.Length.CompareTo(a.From.Length));

        RegexOptions options = RegexOptions.CultureInvariant | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        if (identity.GitEmail is { Length: >= 3 } email)
        {
            _patterns.Add((new Regex(Regex.Escape(email), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, timeout), "<git-email>"));
        }

        if (identity.GitUserName is { Length: >= 3 } gitUser && gitUser.Trim().Length >= 3)
        {
            string words = string.Join(@"\s+", gitUser.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            _patterns.Add((new Regex(@"(?<![A-Za-z0-9_])" + words + @"(?![A-Za-z0-9_])",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, timeout), "<git-user>"));
        }

        if (identity.UserName is { Length: >= 2 } user)
        {
            // The user name and, for a dotted name, its '-'-encoded form (david.maltby → david-maltby).
            string names = string.Join("|", new[] { user, Encode(user) }.Distinct(StringComparer.Ordinal).Select(Regex.Escape));

            // Inside a path, any length; inside a '-'-encoded path (`C--…`, `-home-…`); and bare, word-bounded, from
            // 4 characters (a shorter bare name is too likely to be an ordinary word).
            _patterns.Add((new Regex(@"(?<=[\\/])(?:" + names + @")(?=[\\/""'\s]|$)", options, timeout), "<user>"));
            _patterns.Add((new Regex(@"(?<=(?:^|[^A-Za-z0-9-])[A-Za-z]?-[A-Za-z0-9-]*)(?:" + names + @")(?=-|$|[^A-Za-z0-9])",
                options, timeout), "<user>"));
            if (user.Length >= 4)
            {
                _patterns.Add((new Regex(@"(?<![A-Za-z0-9_])(?:" + names + @")(?![A-Za-z0-9_])",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, timeout), "<user>"));
            }
        }

        string[] hostNames = [.. hosts.Where(h => !string.IsNullOrWhiteSpace(h) && h!.Length >= 3).Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length).ThenBy(h => h, StringComparer.Ordinal)];
        if (hostNames.Length > 0)
        {
            _patterns.Add((new Regex(
                @"(?<![A-Za-z0-9_.-])(?:" + string.Join("|", hostNames.Select(Regex.Escape)) + @")(?![A-Za-z0-9_-])",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, timeout), "<host>"));
        }
    }

    /// <summary>A path with every character outside <c>[A-Za-z0-9]</c> turned into <c>-</c>, as Claude Code names a project directory.</summary>
    public static string Encode(string path) => new([.. path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    /// <summary>Anonymize <paramref name="text"/>, leaving every redaction label untouched.</summary>
    public string Apply(string text)
    {
        if (text.Length == 0 || (_roots.Count == 0 && _patterns.Count == 0))
        {
            return text;
        }

        var spans = new List<(int Start, int End, string To, int Rank)>();
        foreach (RedactionView view in BundleRedactor.Views(text))
        {
            for (int rank = 0; rank < _roots.Count; rank++)
            {
                (string from, string to) = _roots[rank];
                int at = view.Text.IndexOf(from, _comparison);
                while (at >= 0)
                {
                    AddSpans(spans, view, at, at + from.Length, to, rank);
                    at = view.Text.IndexOf(from, at + from.Length, _comparison);
                }
            }

            for (int index = 0; index < _patterns.Count; index++)
            {
                (Regex regex, string to) = _patterns[index];
                foreach (Match match in regex.Matches(view.Text))
                {
                    AddSpans(spans, view, match.Index, match.Index + match.Length, to, _roots.Count + index);
                }
            }
        }

        if (spans.Count == 0)
        {
            return text;
        }

        List<(int Start, int End)> labels = [.. Label().Matches(text).Select(m => (m.Index, m.Index + m.Length))];
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : b.End.CompareTo(a.End));

        var output = new StringBuilder(text.Length);
        int written = 0;
        foreach ((int start, int end, string to, _) in spans)
        {
            if (start < written || labels.Any(l => start < l.End && end > l.Start))
            {
                continue; // overlaps a span already replaced (a longer or higher-ranked one), or a label
            }

            output.Append(text, written, start - written).Append(to);
            written = end;
        }

        output.Append(text, written, text.Length - written);
        return output.ToString();
    }

    private static void AddSpans(List<(int, int, string, int)> spans, RedactionView view, int from, int to, string token, int rank)
    {
        foreach ((int start, int end) in view.OriginalRanges(from, to))
        {
            spans.Add((start, end, token, rank));
        }
    }

    [GeneratedRegex(@"\[REDACTED:[^\]\s]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex Label();
}
