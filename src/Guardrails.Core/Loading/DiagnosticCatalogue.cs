using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Loading;

/// <summary>How a diagnostic is reported when it fires.</summary>
public enum DiagnosticKind
{
    /// <summary>Fails the plan.</summary>
    Error,

    /// <summary>Reported and does not fail the plan — <c>validate</c>'s exit code does not move.</summary>
    Warning,

    /// <summary>Reported either way, depending on what the check found (only <c>GR2019</c>-class rules).</summary>
    Either,

    /// <summary>A retired code, kept only so its number is never re-allocated. Nothing emits it.</summary>
    Retired
}

/// <summary>One diagnostic code, as the catalogue reports it.</summary>
/// <param name="Code">The stable code, e.g. <c>GR2042</c>.</param>
/// <param name="Name">The <see cref="DiagnosticCodes"/> constant's name, e.g. <c>StructuralOverScope</c>.</param>
/// <param name="Kind">Whether it fails the plan.</param>
/// <param name="Summary">The first sentence-ish of the doc comment — the one-line form.</param>
/// <param name="Body">
/// The whole doc comment as prose, tags stripped — INCLUDING the leading <c>GRxxxx (SEVERITY) —</c>
/// marker, deliberately. That marker is the AUTHORED source of <see cref="Kind"/>, and the test that
/// keeps severities honest asserts on it directly; stripping it here would leave that test checking
/// this class's own inference instead of the text a developer wrote. A renderer that has already
/// shown code and severity should drop it with <see cref="DiagnosticCatalogue.WithoutMarker"/>.
/// </param>
public sealed record DiagnosticEntry(
    string Code,
    string Name,
    DiagnosticKind Kind,
    string Summary,
    string Body);

/// <summary>
/// The queryable form of <see cref="DiagnosticCodes"/> (issue #558).
///
/// <para><b>Why this reads the SOURCE rather than declaring a table.</b> <c>guardrails validate</c> emits
/// codes, and until #558 nothing in the shipped tool could say what one meant: the only authoritative
/// catalogue was the XML doc comments in a source file, which a consumer of the packaged dotnet tool does
/// not have. The obvious fix — a hand-written table of code → severity → summary — is the fix that created
/// the problem. There were already two partial copies (the constants and the SSOT), and they had drifted
/// measurably: 16 defined codes the SSOT never mentioned, and two it named that no constant defined. A
/// third copy would have drifted too, and the drift would again be invisible.</para>
///
/// <para>So <c>DiagnosticCodes.cs</c> is embedded verbatim as a resource and parsed here. There is exactly
/// one place a code and its prose are authored, and it is the same place a developer already edits when
/// adding a check.</para>
///
/// <para><b>Severity is carried by a marker in the doc comment</b> — <c>GR2042 (ERROR) — …</c> — completing
/// a convention the newest codes already followed. It is not free-form: a test derives the real severity
/// from every <c>Error(...)</c> / <c>Warning(...)</c> emission site in the source tree and asserts the
/// marker matches, so a marker that lies fails the build rather than misinforming an operator. That test is
/// the actual deliverable; this class and the command are the surface.</para>
/// </summary>
public static partial class DiagnosticCatalogue
{
    private const string ResourceName = "Guardrails.Core.Resources.DiagnosticCodes.cs";

    private static readonly Lazy<ImmutableArray<DiagnosticEntry>> LazyEntries = new(Parse);

    /// <summary>Every code, ordered by code.</summary>
    public static ImmutableArray<DiagnosticEntry> Entries => LazyEntries.Value;

    /// <summary>Look up one code (case-insensitive, e.g. <c>gr2042</c>), or null when it is not defined.</summary>
    public static DiagnosticEntry? Find(string code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Entries.FirstOrDefault(e => string.Equals(e.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every code whose number starts with <paramref name="ladder"/> (e.g. <c>GR20</c>).</summary>
    public static IEnumerable<DiagnosticEntry> InLadder(string ladder) =>
        Entries.Where(e => e.Code.StartsWith(ladder.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ImmutableArray<DiagnosticEntry> Parse()
    {
        string source = ReadEmbeddedSource();
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var entries = new List<DiagnosticEntry>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match declaration = ConstantRegex().Match(lines[i]);
            if (!declaration.Success)
            {
                continue;
            }

            // Walk back over the contiguous /// block immediately above the constant. Anything else —
            // a blank line, a // comment, another declaration — ends it, so a section banner comment can
            // never be mistaken for a code's documentation.
            int start = i - 1;
            while (start >= 0 && lines[start].TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                start--;
            }

            string body = DocText(lines, start + 1, i);
            entries.Add(new DiagnosticEntry(
                declaration.Groups["code"].Value,
                declaration.Groups["name"].Value,
                KindOf(body),
                FirstLine(body),
                body));
        }

        return [.. entries.OrderBy(e => e.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The doc block as readable prose: <c>///</c> stripped, XML tags removed, paragraphs preserved as
    /// blank lines. Deliberately lossy on markup and lossless on wording — the reasoning in these comments
    /// is the reason the catalogue reads the source, so it must survive intact.
    /// </summary>
    private static string DocText(string[] lines, int from, int toExclusive)
    {
        var text = new StringBuilder();

        for (int i = from; i < toExclusive; i++)
        {
            string line = lines[i].TrimStart();
            if (!line.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            line = line[3..].Trim();

            // Structural tags become paragraph breaks; inline tags just vanish, leaving their content.
            if (line is "<summary>" or "</summary>" or "<remarks>" or "</remarks>")
            {
                continue;
            }

            bool paragraph = line.StartsWith("<para>", StringComparison.Ordinal);
            line = TagRegex().Replace(line, string.Empty);
            line = line.Replace("&lt;", "<", StringComparison.Ordinal)
                       .Replace("&gt;", ">", StringComparison.Ordinal)
                       .Replace("&amp;", "&", StringComparison.Ordinal);

            if (line.Length == 0)
            {
                continue;
            }

            if (paragraph && text.Length > 0)
            {
                text.Append("\n\n");
            }
            else if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(line);
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// The one-line form: everything up to the first sentence end, with the <c>GRxxxx (SEVERITY) —</c>
    /// marker stripped, since the caller renders code and severity in their own columns.
    /// </summary>
    private static string FirstLine(string body)
    {
        string text = MarkerRegex().Replace(body, string.Empty).Trim();

        int paragraph = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (paragraph > 0)
        {
            text = text[..paragraph];
        }

        // A period only ends the sentence when a space follows it — otherwise `action.prompt.md` and
        // `guardrails.json` truncate the summary to two words.
        // The space alone is not enough: "(e.g. guardrailMode)" is a period, a space, and a perfectly
        // ordinary continuation. Requiring a following CAPITAL costs an occasional over-long summary and
        // never produces one that stops mid-abbreviation.
        for (int i = 0; i < text.Length - 2; i++)
        {
            if (text[i] == '.' && text[i + 1] == ' ' && char.IsUpper(text[i + 2]))
            {
                return text[..(i + 1)].Trim();
            }
        }

        return text;
    }

    /// <summary>
    /// <paramref name="body"/> without its leading <c>GRxxxx (SEVERITY) —</c> marker, for a renderer that
    /// has already shown both in its own header.
    /// </summary>
    public static string WithoutMarker(string body) =>
        string.IsNullOrEmpty(body) ? body : MarkerRegex().Replace(body, string.Empty).TrimStart();

    private static DiagnosticKind KindOf(string body)
    {
        Match marker = MarkerRegex().Match(body);
        if (!marker.Success)
        {
            // Unmarked is not guessed at. The test that keeps markers honest also fails on a missing one,
            // so this is reachable only from a build that has already failed that test.
            return DiagnosticKind.Either;
        }

        return marker.Groups["sev"].Value.ToUpperInvariant() switch
        {
            "ERROR" => DiagnosticKind.Error,
            "WARNING" => DiagnosticKind.Warning,
            "RETIRED" => DiagnosticKind.Retired,
            _ => DiagnosticKind.Either
        };
    }

    private static string ReadEmbeddedSource()
    {
        using Stream? stream = typeof(DiagnosticCatalogue).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The diagnostic catalogue resource '{ResourceName}' is not embedded in this assembly. "
                + "It is declared as an EmbeddedResource in Guardrails.Core.csproj; a build that drops it "
                + "would leave 'guardrails diagnostics' silently empty (issue #558).");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"public const string (?<name>\w+)\s*=\s*""(?<code>GR\d+)""")]
    private static partial Regex ConstantRegex();

    [GeneratedRegex(@"^GR\d+\s*\((?<sev>[A-Za-z ]+)\)\s*(—|-)\s*")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
