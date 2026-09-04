using System.Reflection;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Loading;

/// <summary>
/// How the check set the RUNNING binary carries compares against the check set a Guardrails
/// SOURCE TREE declares (issue #564).
/// </summary>
public enum CheckSetComparison
{
    /// <summary>
    /// No Guardrails checkout was found to compare against, so the question "does this binary know
    /// every check the tree has?" was never asked. The overwhelmingly common case, and the honest
    /// label for it — NOT a clean bill of health.
    /// </summary>
    NotCompared,

    /// <summary>
    /// A checkout WAS found but its <c>DiagnosticCodes.cs</c> could not be read, or parsed to zero
    /// codes. Deliberately distinct from <see cref="NotCompared"/> and never folded into
    /// <see cref="Matches"/>: a scanner that silently degrades to "agrees" is the defect this whole
    /// mechanism exists to remove.
    /// </summary>
    SourceUnreadable,

    /// <summary>The binary and the tree declare exactly the same codes.</summary>
    Matches,

    /// <summary>
    /// The tree declares at least one code the binary does not carry — the false-green shape of
    /// issue #564. Drives <see cref="DiagnosticCodes.CheckSetPredatesSourceTree"/> (GR2072).
    /// Classified on the missing direction alone: a binary that is behind AND ahead is still behind.
    /// </summary>
    BinaryBehindSource,

    /// <summary>
    /// The binary carries codes the tree does not declare, and lacks none of the tree's. Running a
    /// NEWER tool against an older tree — safe (nothing is skipped), reported for orientation only.
    /// </summary>
    BinaryAheadOfSource
}

/// <summary>One diagnostic code as declared in source: its stable code and its constant name.</summary>
/// <param name="Code">The wire code, e.g. <c>GR2072</c>.</param>
/// <param name="Name">The declaring constant's name, e.g. <c>CheckSetPredatesSourceTree</c>.</param>
public sealed record DeclaredCode(string Code, string Name);

/// <summary>
/// The provenance of the check set a <c>validate</c> run actually applied: which binary ran, how
/// many codes it carries, and — when the plan sits inside a Guardrails checkout — whether that
/// matches what the tree declares.
/// </summary>
public sealed record CheckSetReport
{
    /// <summary>How many missing codes the warning enumerates before it truncates.</summary>
    public const int MaxEnumeratedCodes = 8;

    /// <summary>The running harness version, injected (never read from an attribute here).</summary>
    public required string HarnessVersion { get; init; }

    /// <summary>Every diagnostic code the running binary declares, sorted ordinal.</summary>
    public required IReadOnlyList<string> ImplementedCodes { get; init; }

    /// <summary>The comparison verdict.</summary>
    public required CheckSetComparison Comparison { get; init; }

    /// <summary>The Guardrails checkout root the comparison used; null when none was found.</summary>
    public string? SourceRoot { get; init; }

    /// <summary>
    /// The <c>DiagnosticCodes.cs</c> the comparison read; null when no checkout was found.
    /// Carried so the GR2072 diagnostic can point at the exact file.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>Codes the tree declares that the running binary does NOT carry, sorted ordinal.</summary>
    public IReadOnlyList<DeclaredCode> MissingFromBinary { get; init; } = [];

    /// <summary>Codes the running binary carries that the tree does NOT declare, sorted ordinal.</summary>
    public IReadOnlyList<string> MissingFromSource { get; init; } = [];

    /// <summary>
    /// The highest code the running binary carries, by ordinal comparison — every code is
    /// <c>GR</c> plus four digits, so ordinal ordering is numeric ordering. The single most
    /// diagnostic fact for "how new is this binary", and what makes two runs comparable at a glance.
    /// </summary>
    public string? HighestCode => ImplementedCodes.Count == 0 ? null : ImplementedCodes[^1];

    /// <summary>
    /// The one line <c>validate</c> ALWAYS prints. Unconditional by design: the comparison can only
    /// be made inside this repo, but the reader must always be told which check set produced the
    /// verdict they are about to read, and must never be left to infer that no news is good news.
    /// </summary>
    public string SummaryLine
    {
        get
        {
            string head =
                $"Check set: guardrails {HarnessVersion}, {ImplementedCodes.Count} diagnostic codes" +
                (HighestCode is null ? string.Empty : $" (highest {HighestCode})");

            string tail = Comparison switch
            {
                CheckSetComparison.NotCompared =>
                    "; no Guardrails source tree found to compare against, so this run cannot tell " +
                    "you whether newer checks exist.",

                CheckSetComparison.SourceUnreadable =>
                    $"; the source tree at {SourceRoot} was found but its DiagnosticCodes.cs could not " +
                    "be read, so no comparison was made.",

                CheckSetComparison.Matches =>
                    $"; the source tree at {SourceRoot} declares the same set.",

                CheckSetComparison.BinaryBehindSource =>
                    $"; the source tree at {SourceRoot} declares {MissingFromBinary.Count} code(s) this " +
                    $"binary does not carry (see {DiagnosticCodes.CheckSetPredatesSourceTree} above).",

                CheckSetComparison.BinaryAheadOfSource =>
                    $"; this binary is AHEAD of the source tree at {SourceRoot}, which declares " +
                    $"{MissingFromSource.Count} fewer code(s). Nothing was skipped.",

                _ => "."
            };

            return head + tail;
        }
    }

    /// <summary>
    /// The GR2072 WARNING, or null when the binary is not behind the tree. A WARNING and never an
    /// ERROR: running an older tool against a newer tree is legitimate (a release build, a pinned
    /// CI, a contributor who has not updated), and refusing to validate would be a worse cure than
    /// the disease. The goal is that a green <c>validate</c> can be trusted OR discounted — not that
    /// it becomes a failure.
    /// </summary>
    public Diagnostic? StaleBinaryWarning
    {
        get
        {
            if (Comparison != CheckSetComparison.BinaryBehindSource)
            {
                return null;
            }

            IEnumerable<string> shown = MissingFromBinary
                .Take(MaxEnumeratedCodes)
                .Select(c => $"{c.Code} ({c.Name})");

            string list = string.Join(", ", shown);
            int hidden = MissingFromBinary.Count - MaxEnumeratedCodes;
            if (hidden > 0)
            {
                list += $", and {hidden} more";
            }

            return new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Code = DiagnosticCodes.CheckSetPredatesSourceTree,
                Path = SourcePath,
                Message =
                    $"This guardrails is {HarnessVersion} and carries {ImplementedCodes.Count} diagnostic " +
                    $"codes, but the source tree at {SourceRoot} declares {MissingFromBinary.Count} more: " +
                    $"{list}. Those checks did NOT run, so a clean result here does not cover them. " +
                    "Rebuild from source (dotnet run --project src/Guardrails.Cli -- validate <folder>) " +
                    "or run: dotnet tool update -g ServantSoftware.Guardrails."
            };
        }
    }
}

/// <summary>
/// Answers "does the binary that just validated this plan know every check the tree has?" — the
/// question issue #564 measured going unanswered: the same plan and the same command produced 4
/// findings or 0 depending on which binary was on PATH, with nothing said either way.
///
/// <para><b>Scope, stated plainly.</b> The comparison is only possible when the plan (or the working
/// directory) sits inside a checkout of Guardrails ITSELF, because only then is there a source of
/// truth to compare against without a network call. That is exactly where the defect was found and
/// where it bites hardest: this repo authors a diagnostic and then immediately uses it to validate
/// the next plan. For every other user the comparison is <see cref="CheckSetComparison.NotCompared"/>,
/// which <see cref="CheckSetReport.SummaryLine"/> says out loud rather than passing off as agreement.</para>
///
/// <para><b>Declares, not implements.</b> Both sides of the comparison are the set of codes
/// <see cref="DiagnosticCodes"/> DECLARES — reflected from the running assembly's metadata on one
/// side, parsed from the checked-out source on the other. A code declared but not yet wired to a
/// check counts on both sides and cancels out, so the DIFFERENCE is sound; the absolute count is
/// "codes declared", which is how the output words it.</para>
/// </summary>
public static partial class CheckSetProbe
{
    /// <summary>
    /// The marker that identifies a Guardrails checkout, and the file the comparison reads. One
    /// path serves as both, so detection cannot succeed where the data is absent, and no other
    /// repository can produce a false positive.
    /// </summary>
    public static readonly string[] CodesRelativeSegments =
        ["src", "Guardrails.Core", "Loading", "DiagnosticCodes.cs"];

    [GeneratedRegex(
        @"^\s*public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<code>GR\d+)""\s*;",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();

    /// <summary>
    /// Every diagnostic code the RUNNING assembly declares, sorted ordinal. Reflected rather than
    /// hand-listed so a newly authored code joins the census with no second edit to forget — the
    /// maintenance failure would reintroduce the very silence being fixed.
    /// </summary>
    public static IReadOnlyList<string> ImplementedCodes { get; } = ReadCodes(typeof(DiagnosticCodes));

    /// <summary>
    /// Read the <c>GRxxxx</c> constants declared by <paramref name="codesType"/>. Exposed for tests;
    /// production callers use <see cref="ImplementedCodes"/>.
    /// </summary>
    public static IReadOnlyList<string> ReadCodes(Type codesType)
    {
        ArgumentNullException.ThrowIfNull(codesType);

        return [.. codesType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(v => v is not null && v.StartsWith("GR", StringComparison.Ordinal))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Parse the <c>GRxxxx</c> constants a <c>DiagnosticCodes.cs</c> SOURCE text declares. Anchored
    /// at line start on the real declaration form, so a doc comment (<c>///</c>), a commented-out
    /// line, or a code named only in prose cannot be counted — the reserved-by-name codes discussed
    /// in that file's comments are deliberately NOT declarations and must not appear here.
    /// </summary>
    public static IReadOnlyList<DeclaredCode> ParseSource(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        return [.. DeclarationPattern().Matches(sourceText)
            .Select(m => new DeclaredCode(m.Groups["code"].Value, m.Groups["name"].Value))
            .DistinctBy(d => d.Code, StringComparer.Ordinal)
            .OrderBy(d => d.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> looking for a Guardrails checkout — a
    /// directory carrying <c>src/Guardrails.Core/Loading/DiagnosticCodes.cs</c>. Returns the root,
    /// or null. A git worktree of this repo carries the file too and is correctly treated as the
    /// tree being worked on.
    /// </summary>
    public static string? FindCheckoutRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        while (dir is not null)
        {
            if (File.Exists(CodesPath(dir.FullName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>The <c>DiagnosticCodes.cs</c> path under a checkout <paramref name="root"/>.</summary>
    public static string CodesPath(string root) =>
        Path.Combine([root, .. CodesRelativeSegments]);

    /// <summary>
    /// Locate a checkout from the first usable start directory, read its source, and compare. The
    /// only method here that touches the filesystem.
    /// </summary>
    /// <param name="harnessVersion">The running harness version, injected by the CLI.</param>
    /// <param name="startDirectories">
    /// Candidate directories to walk up from, in order — the CLI passes the plan folder first and
    /// the working directory second, so a plan validated from elsewhere in the repo is still covered.
    /// </param>
    public static CheckSetReport Describe(string harnessVersion, params string?[] startDirectories)
    {
        ArgumentNullException.ThrowIfNull(harnessVersion);
        ArgumentNullException.ThrowIfNull(startDirectories);

        string? root = startDirectories.Select(FindCheckoutRoot).FirstOrDefault(r => r is not null);
        string? sourceText = null;

        if (root is not null)
        {
            try
            {
                sourceText = File.ReadAllText(CodesPath(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Found but unreadable: fall through to SourceUnreadable, never to "agrees".
            }
        }

        return Compare(harnessVersion, ImplementedCodes, root, sourceText);
    }

    /// <summary>
    /// The pure comparison. <paramref name="sourceRoot"/> null means no checkout was found;
    /// non-null with a null or unparseable <paramref name="sourceText"/> means one was found but
    /// could not be read.
    /// </summary>
    public static CheckSetReport Compare(
        string harnessVersion,
        IReadOnlyList<string> implemented,
        string? sourceRoot,
        string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(harnessVersion);
        ArgumentNullException.ThrowIfNull(implemented);

        IReadOnlyList<string> sorted = [.. implemented.OrderBy(c => c, StringComparer.Ordinal)];

        var baseline = new CheckSetReport
        {
            HarnessVersion = harnessVersion,
            ImplementedCodes = sorted,
            Comparison = CheckSetComparison.NotCompared
        };

        if (sourceRoot is null)
        {
            return baseline;
        }

        IReadOnlyList<DeclaredCode> declared =
            sourceText is null ? [] : ParseSource(sourceText);

        // Zero parsed codes from a file that exists is a SCANNER failure, not an empty tree: the
        // declaration form changed under the regex. Reporting "agrees" there would be this issue's
        // own defect wearing the fix's clothes, so it is called out instead.
        if (declared.Count == 0)
        {
            return baseline with
            {
                Comparison = CheckSetComparison.SourceUnreadable,
                SourceRoot = sourceRoot,
                SourcePath = CodesPath(sourceRoot)
            };
        }

        var binarySet = new HashSet<string>(sorted, StringComparer.Ordinal);
        var sourceSet = new HashSet<string>(declared.Select(d => d.Code), StringComparer.Ordinal);

        IReadOnlyList<DeclaredCode> missingFromBinary =
            [.. declared.Where(d => !binarySet.Contains(d.Code))];
        IReadOnlyList<string> missingFromSource =
            [.. sorted.Where(c => !sourceSet.Contains(c))];

        CheckSetComparison verdict = missingFromBinary.Count > 0
            ? CheckSetComparison.BinaryBehindSource
            : missingFromSource.Count > 0
                ? CheckSetComparison.BinaryAheadOfSource
                : CheckSetComparison.Matches;

        return baseline with
        {
            Comparison = verdict,
            SourceRoot = sourceRoot,
            SourcePath = CodesPath(sourceRoot),
            MissingFromBinary = missingFromBinary,
            MissingFromSource = missingFromSource
        };
    }
}
