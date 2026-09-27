using System.Text;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Bundle;

/// <summary>
/// Pass 3 of SSOT §17.6.3, default on (<c>--keep-paths</c> disables it): the home directory becomes <c>~</c>, the
/// workspace <c>&lt;workspace&gt;</c>, the worktree root <c>&lt;worktrees&gt;</c>, the OS user name inside a path
/// <c>&lt;user&gt;</c>, and the recorded host names <c>&lt;host&gt;</c>.
/// <para>
/// Each root is matched in every spelling an artifact carries it: native separators, forward slashes, back slashes,
/// and JSON-escaped (<c>\\</c>); case-insensitively where the file system is (Windows, macOS). It runs AFTER the secret
/// passes and never inside a <c>[REDACTED:…]</c> label, so a label is never rewritten.
/// </para>
/// </summary>
public sealed partial class BundlePathAnonymizer
{
    private readonly List<(string From, string To)> _roots = [];
    private readonly Regex? _user;
    private readonly Regex? _hosts;
    private readonly StringComparison _comparison;

    /// <summary>Build the anonymizer. Null or blank inputs are skipped.</summary>
    public BundlePathAnonymizer(
        string? home, string? workspace, string? worktreeRoot, string? userName, IEnumerable<string?> hosts,
        bool caseInsensitive)
    {
        _comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = new List<(string Path, string Token)>();
        void AddRoot(string? path, string token)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string trimmed = path.TrimEnd('/', '\\');
                if (trimmed.Length > 1)
                {
                    roots.Add((trimmed, token));
                }
            }
        }

        AddRoot(worktreeRoot, "<worktrees>");
        AddRoot(workspace, "<workspace>");
        AddRoot(home, "~");

        var seen = new HashSet<string>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string path, string token) in roots)
        {
            string forward = path.Replace('\\', '/');
            string backward = path.Replace('/', '\\');
            foreach (string spelling in new[] { path, forward, backward, backward.Replace("\\", "\\\\", StringComparison.Ordinal) })
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
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 2)
        {
            _user = new Regex(@"(?<=[\\/])" + Regex.Escape(userName) + @"(?=[\\/""'\s]|$)", options, TimeSpan.FromSeconds(10));
        }

        string[] hostNames = [.. hosts.Where(h => !string.IsNullOrWhiteSpace(h) && h!.Length >= 3).Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length).ThenBy(h => h, StringComparer.Ordinal)];
        if (hostNames.Length > 0)
        {
            _hosts = new Regex(
                @"(?<![A-Za-z0-9_.-])(?:" + string.Join("|", hostNames.Select(Regex.Escape)) + @")(?![A-Za-z0-9_-])",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Anonymize <paramref name="text"/>, leaving every redaction label untouched.</summary>
    public string Apply(string text)
    {
        var output = new StringBuilder(text.Length);
        int at = 0;
        foreach (Match label in Label().Matches(text))
        {
            output.Append(ApplyPlain(text[at..label.Index])).Append(label.Value);
            at = label.Index + label.Length;
        }

        output.Append(ApplyPlain(text[at..]));
        return output.ToString();
    }

    private string ApplyPlain(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        foreach ((string from, string to) in _roots)
        {
            text = ReplaceAll(text, from, to);
        }

        if (_user is not null)
        {
            text = _user.Replace(text, "<user>");
        }

        if (_hosts is not null)
        {
            text = _hosts.Replace(text, "<host>");
        }

        return text;
    }

    private string ReplaceAll(string text, string from, string to)
    {
        int at = text.IndexOf(from, _comparison);
        if (at < 0)
        {
            return text;
        }

        var output = new StringBuilder(text.Length);
        int last = 0;
        while (at >= 0)
        {
            output.Append(text, last, at - last).Append(to);
            last = at + from.Length;
            at = text.IndexOf(from, last, _comparison);
        }

        output.Append(text, last, text.Length - last);
        return output.ToString();
    }

    [GeneratedRegex(@"\[REDACTED:[^\]\s]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex Label();
}
