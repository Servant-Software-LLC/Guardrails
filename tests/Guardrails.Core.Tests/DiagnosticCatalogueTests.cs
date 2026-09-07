using System.Reflection;
using System.Text.RegularExpressions;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// The catalogue that makes GR codes lookup-able (issue #558) — and, more importantly, the tests that keep
/// it from becoming a fourth partial copy.
///
/// <para><b>The problem was never "there is no command".</b> It was that the catalogue had no single
/// queryable form, so every consumer hand-maintained a partial copy and the copies drifted without anyone
/// being able to see it: the SSOT cited 62 codes while 78 were defined, omitted the whole foundational
/// <c>GR2001</c>–<c>GR2008</c> block, and named two codes (<c>GR2013</c>/<c>GR2014</c>) that no constant
/// defined. Adding a third copy would have reproduced that exactly.</para>
///
/// <para>So <c>DiagnosticCodes.cs</c> is embedded and parsed, and these tests assert the parse is total:
/// every constant is in the catalogue, no two share a code, every one carries a severity marker, and
/// <b>every marker matches what the source tree actually does with that code</b>. The last is the one that
/// matters — a marker is prose, and prose that lies to an operator about whether a diagnostic fails their
/// plan is worse than no catalogue at all.</para>
/// </summary>
public sealed partial class DiagnosticCatalogueTests
{
    /// <summary>Codes kept only so their numbers are never re-allocated. Nothing emits them, by design.</summary>
    private static readonly HashSet<string> Retired = ["MissingIntegrationGate", "IntegrationGateEmpty"];

    private static IReadOnlyDictionary<string, string> ConstantsFromReflection()
    {
        return typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .Where(p => p.Value.StartsWith("GR", StringComparison.Ordinal))
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void EveryConstantIsInTheCatalogue_AndTheCatalogueInventsNothing()
    {
        // Reflection is the independent witness: the catalogue parses the SOURCE, this reads the COMPILED
        // constants, and the two must agree. A regex that silently stopped matching — a reformat, a nullable
        // annotation, a line break in the wrong place — would otherwise produce a catalogue quietly missing
        // codes, which is the failure mode of every hand-maintained copy this replaces.
        IReadOnlyDictionary<string, string> declared = ConstantsFromReflection();

        Assert.NotEmpty(declared);

        var catalogued = DiagnosticCatalogue.Entries.ToDictionary(e => e.Name, e => e.Code, StringComparer.Ordinal);

        Assert.Equal(declared.Count, DiagnosticCatalogue.Entries.Length);

        foreach ((string name, string code) in declared)
        {
            Assert.True(catalogued.ContainsKey(name), $"{name} ({code}) is defined but absent from the catalogue");
            Assert.Equal(code, catalogued[name]);
        }

        foreach ((string name, string code) in catalogued)
        {
            Assert.True(declared.ContainsKey(name), $"the catalogue reports {name} ({code}), which no constant defines");
        }
    }

    [Fact]
    public void NoTwoConstantsShareACode()
    {
        // This retires the hand-maintained "next-free code" comment block as the ONLY collision defense
        // (#320). A comment is exactly the kind of thing a merge drops silently, and two rules answering to
        // one code is unfalsifiable from a run's output.
        List<IGrouping<string, KeyValuePair<string, string>>> collisions =
        [
            .. ConstantsFromReflection()
                .GroupBy(p => p.Value, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
        ];

        Assert.True(
            collisions.Count == 0,
            "two constants share a diagnostic code: "
            + string.Join("; ", collisions.Select(g => $"{g.Key} = {string.Join(" and ", g.Select(p => p.Key))}")));
    }

    [Fact]
    public void EveryCodeCarriesASeverityMarkerAndAUsableSummary()
    {
        // A catalogue entry with no severity cannot answer the question an operator actually has — "does
        // this fail my plan?" — and one with an empty summary is a row of code numbers.
        foreach (DiagnosticEntry entry in DiagnosticCatalogue.Entries)
        {
            Assert.True(
                entry.Body.StartsWith(entry.Code, StringComparison.Ordinal),
                $"{entry.Code} ({entry.Name}) has no 'GRxxxx (SEVERITY) — ' marker at the start of its doc "
                + $"comment; the catalogue cannot report its severity. Body began: "
                + $"{entry.Body[..Math.Min(60, entry.Body.Length)]}");

            Assert.False(
                string.IsNullOrWhiteSpace(entry.Summary),
                $"{entry.Code} ({entry.Name}) has an empty summary");

            // The marker is stripped from the summary — the caller renders code and severity in their own
            // columns, so a leftover marker prints the code twice on every row. Asserted as "does not START
            // with", not "does not contain": several summaries legitimately mention their own code in prose
            // ("emits ONE GR2037 per match"), and a contains-check calls that a bug.
            Assert.False(
                entry.Summary.StartsWith(entry.Code, StringComparison.OrdinalIgnoreCase),
                $"{entry.Code}'s summary still carries its severity marker: {entry.Summary}");
        }
    }

    [Fact]
    public void EverySeverityMarkerMatchesWhatTheSourceTreeActuallyDoes()
    {
        // THE test. Everything else here checks that the catalogue is complete; this checks that it is
        // TRUE. The marker is prose sitting in a comment, and prose drifts from code — that is the entire
        // lesson of the SSOT copy this replaces. So the severity is re-derived from every emission site in
        // src/ and compared.
        //
        // Deliberately a source scan rather than a reflection or runtime probe: severity is decided at the
        // CALL SITE (`Error(...)` vs `Warning(...)`, or a `Severity =` initializer), and no amount of
        // inspecting the constants can see it. Reaching for the real evidence is the point.
        string srcRoot = Path.Combine(RepoRoot(), "src");
        Assert.True(Directory.Exists(srcRoot), $"source tree not found at {srcRoot}");

        var observed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            foreach (Match m in CallSiteRegex().Matches(text))
            {
                Record(observed, m.Groups["name"].Value, m.Groups["sev"].Value);
            }

            foreach (Match m in InitialiserForwardRegex().Matches(text))
            {
                Record(observed, m.Groups["name"].Value, m.Groups["sev"].Value);
            }

            foreach (Match m in InitialiserReverseRegex().Matches(text))
            {
                Record(observed, m.Groups["name"].Value, m.Groups["sev"].Value);
            }
        }

        var mismatches = new List<string>();

        foreach (DiagnosticEntry entry in DiagnosticCatalogue.Entries)
        {
            bool retired = Retired.Contains(entry.Name);
            observed.TryGetValue(entry.Name, out HashSet<string>? sites);

            DiagnosticKind actual = retired
                ? DiagnosticKind.Retired
                : sites is null
                    ? DiagnosticKind.Retired      // emitted nowhere: same evidence a retired code leaves
                    : sites.Count > 1
                        ? DiagnosticKind.Either
                        : sites.Single() == "Error" ? DiagnosticKind.Error : DiagnosticKind.Warning;

            if (actual != entry.Kind)
            {
                string where = sites is null ? "no emission site in src/" : string.Join("+", sites.Order());
                mismatches.Add($"{entry.Code} ({entry.Name}) is marked {entry.Kind} but the source says {actual} [{where}]");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "a severity marker disagrees with the code that emits it — the catalogue would tell an operator "
            + "the wrong thing about whether their plan fails:\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void ARealCodeResolves_AndAnUnknownOneReturnsNullRatherThanAnEmptyEntry()
    {
        DiagnosticEntry? overScope = DiagnosticCatalogue.Find("GR2042");

        Assert.NotNull(overScope);
        Assert.Equal("StructuralOverScope", overScope!.Name);

        // Case-insensitive, because an operator retypes a code off a terminal, not off the source.
        Assert.Equal(overScope, DiagnosticCatalogue.Find("gr2042"));
        Assert.Equal(overScope, DiagnosticCatalogue.Find("  GR2042 "));

        Assert.Null(DiagnosticCatalogue.Find("GR9999"));
        Assert.Null(DiagnosticCatalogue.Find(""));
    }

    [Fact]
    public void TheLaddersAreBothPopulated_AndFilterCleanly()
    {
        // GR10xx (plan-load) and GR20xx (validation) advance independently, and the filter is what makes a
        // 78-row listing usable.
        Assert.NotEmpty(DiagnosticCatalogue.InLadder("GR10"));
        Assert.NotEmpty(DiagnosticCatalogue.InLadder("GR20"));
        Assert.All(DiagnosticCatalogue.InLadder("GR10"), e => Assert.StartsWith("GR10", e.Code, StringComparison.Ordinal));
        Assert.Empty(DiagnosticCatalogue.InLadder("GR99"));
    }

    private static void Record(Dictionary<string, HashSet<string>> into, string name, string severity)
    {
        if (severity is not ("Error" or "Warning"))
        {
            return;
        }

        if (!into.TryGetValue(name, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            into[name] = set;
        }

        set.Add(severity);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (.git) not found above the test binary");
    }

    [GeneratedRegex(@"\b(?<sev>Error|Warning)\s*\(\s*DiagnosticCodes\.(?<name>\w+)")]
    private static partial Regex CallSiteRegex();

    [GeneratedRegex(@"Code\s*=\s*DiagnosticCodes\.(?<name>\w+)\s*,.{0,400}?Severity\s*=\s*\w*Severity\.(?<sev>\w+)", RegexOptions.Singleline)]
    private static partial Regex InitialiserForwardRegex();

    [GeneratedRegex(@"Severity\s*=\s*\w*Severity\.(?<sev>\w+)\s*,.{0,400}?Code\s*=\s*DiagnosticCodes\.(?<name>\w+)", RegexOptions.Singleline)]
    private static partial Regex InitialiserReverseRegex();
}
