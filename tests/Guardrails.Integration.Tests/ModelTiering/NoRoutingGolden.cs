using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// Shared machinery for the two DoR Invariant 7 proofs — the committed no-<c>routing</c> golden
/// (<see cref="NoRoutingGoldenTests"/>) and the negative assertions
/// (<see cref="NoRoutingNegativeAssertionTests"/>). Everything here is deliberately mechanism-neutral:
/// both proofs run the SAME scan over the SAME bytes, so a leak cannot be visible to one and invisible
/// to the other.
/// </summary>
internal static class NoRoutingGolden
{
    /// <summary>
    /// Env var naming a folder produced by a REAL <c>/plan-breakdown</c> run over
    /// <c>input/hello-greeting.md</c> against <c>input/guardrails.json</c>, laid out like
    /// <c>expected/</c> (a <c>hello-greeting/</c> task folder beside a <c>breakdown-report.md</c>).
    /// When it is set, both mechanisms run against that fresh output as well as the committed golden.
    /// Unset (CI and every default local run) ⇒ the live halves skip and only the committed halves run.
    /// </summary>
    internal const string FreshBreakdownDirVariable = "GUARDRAILS_FRESH_BREAKDOWN_DIR";

    /// <summary>The fixture root: <c>tests/Guardrails.Integration.Tests/Fixtures/no-routing-golden</c>.</summary>
    internal static string FixtureRoot { get; } =
        Path.GetFullPath(Path.Combine(ThisDir(), "..", "Fixtures", "no-routing-golden"));

    /// <summary>The repo root, walked up from this source file.</summary>
    internal static string RepoRoot { get; } =
        Path.GetFullPath(Path.Combine(ThisDir(), "..", "..", ".."));

    /// <summary>The inputs the golden was generated FROM — the plan and the governing config.</summary>
    internal static string InputDir => Path.Combine(FixtureRoot, "input");

    /// <summary>The governing config: the one that must carry no <c>routing</c> and no <c>tiering</c>.</summary>
    internal static string InputConfigPath => Path.Combine(InputDir, "guardrails.json");

    /// <summary>The golden itself — everything the breakdown emitted.</summary>
    internal static string ExpectedDir => Path.Combine(FixtureRoot, "expected");

    /// <summary>The emitted task folder inside <see cref="ExpectedDir"/>.</summary>
    internal static string GoldenPlanDir => Path.Combine(ExpectedDir, "hello-greeting");

    /// <summary>The captured Step 7 report — the artefact a "classification report line" would land in.</summary>
    internal static string ReportPath => Path.Combine(ExpectedDir, "breakdown-report.md");

    /// <summary>The byte seal over <see cref="ExpectedDir"/>.</summary>
    internal static string ManifestPath => Path.Combine(FixtureRoot, "manifest.sha256");

    /// <summary>The folder a live run wrote, or null when the live halves are to skip.</summary>
    internal static string? FreshBreakdownDir =>
        Environment.GetEnvironmentVariable(FreshBreakdownDirVariable) is { Length: > 0 } dir ? dir : null;

    // ---- Bytes -------------------------------------------------------------------------------

    /// <summary>
    /// Every file under <paramref name="dir"/> as a relative, forward-slashed path, ordinal-sorted —
    /// the same enumeration and the same order the manifest records, so a comparison is a plain
    /// document comparison rather than a set intersection that could quietly drop a file.
    /// </summary>
    internal static IReadOnlyList<string> FilesUnder(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Line-ending normalization: CRLF and lone CR both become LF. See the fixture README.</summary>
    internal static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>Reads a file with line endings normalized.</summary>
    internal static string ReadNormalized(string path) => Normalize(File.ReadAllText(path));

    /// <summary>Reads a file addressed by a forward-slashed relative path.</summary>
    internal static string ReadNormalized(string dir, string relative) =>
        ReadNormalized(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Renders the seal for <paramref name="dir"/>: <c>&lt;sha256&gt;  &lt;path&gt;</c> per file,
    /// ordinal-sorted, LF-terminated. Hashes are over line-ending-normalized content — this fixture
    /// path carries no <c>.gitattributes</c> <c>eol=lf</c> pin, so raw bytes legitimately differ
    /// between a Windows and a Linux checkout while nothing the invariant cares about has moved.
    /// </summary>
    internal static string RenderManifest(string dir)
    {
        StringBuilder rendered = new();
        foreach (string relative in FilesUnder(dir))
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ReadNormalized(dir, relative)));
            rendered.Append(Convert.ToHexString(hash).ToLowerInvariant())
                    .Append("  ")
                    .Append(relative)
                    .Append('\n');
        }

        return rendered.ToString();
    }

    // ---- The tier-artefact scan -------------------------------------------------------------

    /// <summary>
    /// A single forbidden occurrence: which file, which line, which marker matched, and the line
    /// itself — a failure names the byte rather than reporting that some byte exists somewhere.
    /// </summary>
    internal sealed record TierLeak(string RelativePath, int Line, string Marker, string Text)
    {
        /// <summary>One human-readable line, for an assertion message.</summary>
        public override string ToString() => $"  {RelativePath}:{Line}  [{Marker}]  {Text.Trim()}";
    }

    /// <summary>
    /// The substrings that must not appear ANYWHERE in an unconfigured breakdown, matched
    /// case-insensitively.
    ///
    /// <para><c>tier</c> is deliberately blunt: it subsumes <c>action.tier</c>, <c>"tier": null</c>,
    /// <c>tiering</c>, <c>defaultTier</c>, <c>tiers</c> and <c>tiered</c> in one marker, so a leak in a
    /// spelling nobody enumerated is still caught. SKILL.md Step 7.0e states the obligation in exactly
    /// these terms — "ZERO tier bytes" — and this is that sentence, executable.</para>
    ///
    /// <para><c>classif</c> covers classify/classification/classified: the report line the gate forbids
    /// need not contain the word "tier" at all to be one.</para>
    /// </summary>
    private static readonly string[] ForbiddenSubstrings = ["tier", "classif"];

    /// <summary>
    /// Every forbidden occurrence under <paramref name="dir"/>, in file then line order.
    /// </summary>
    internal static IReadOnlyList<TierLeak> ScanForTierArtefacts(string dir)
    {
        List<TierLeak> leaks = [];
        foreach (string relative in FilesUnder(dir))
        {
            string[] lines = ReadNormalized(dir, relative).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (string marker in ForbiddenSubstrings)
                {
                    if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        leaks.Add(new TierLeak(relative, i + 1, marker, lines[i]));
                    }
                }

                if (IsRubricLine(lines[i]))
                {
                    leaks.Add(new TierLeak(relative, i + 1, "easy|medium|hard rubric", lines[i]));
                }
            }
        }

        return leaks;
    }

    /// <summary>
    /// A line naming all three rubric tokens at once — the shape a classification legend or a tier
    /// column key takes even when it never spells the word "tier".
    /// </summary>
    private static bool IsRubricLine(string line) =>
        line.Contains("easy", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("medium", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("hard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Formats leaks for an assertion message.</summary>
    internal static string Describe(IReadOnlyList<TierLeak> leaks) =>
        string.Join(Environment.NewLine, leaks.Select(l => l.ToString()));

    // ---- Structural (JSON) helpers ------------------------------------------------------------

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses a manifest the way the loader does — jsonc, comments skipped.</summary>
    internal static JsonDocument ParseJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path), JsonOptions);

    /// <summary>
    /// Dotted paths to every property named <paramref name="name"/> anywhere in the document,
    /// case-insensitively. Recursive on purpose: "no <c>tier</c> key" means nowhere, not merely
    /// "not directly under <c>action</c>".
    /// </summary>
    internal static IReadOnlyList<string> FindPropertyPaths(JsonElement element, string name)
    {
        List<string> found = [];
        Walk(element, "$", name, found);
        return found;
    }

    private static void Walk(JsonElement element, string path, string name, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string child = $"{path}.{property.Name}";
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(child);
                    }

                    Walk(property.Value, child, name, found);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]", name, found);
                }

                break;

            default:
                break;
        }
    }

    // ---- Population census (anti-vacuity) ------------------------------------------------------

    /// <summary>Every <c>task.json</c> under a plan folder, ordinal-sorted.</summary>
    internal static IReadOnlyList<string> TaskManifests(string planDir) =>
        Directory.EnumerateFiles(planDir, "task.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every <c>guardrails.json</c> under a plan folder, ordinal-sorted.</summary>
    internal static IReadOnlyList<string> RunConfigs(string planDir) =>
        Directory.EnumerateFiles(planDir, "guardrails.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Prompt task actions — the population that WOULD carry an <c>action.tier</c>.</summary>
    internal static IReadOnlyList<string> PromptActions(string planDir) =>
        Directory.EnumerateFiles(planDir, "action.prompt.md", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Script task actions — the population that must stay untagged on BOTH sides of the gate.</summary>
    internal static IReadOnlyList<string> ScriptActions(string planDir) =>
        Directory.EnumerateFiles(planDir, "action.*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Surviving prompt-judge checks — a <c>NN-name.prompt.md</c> inside a <c>guardrails/</c> or
    /// <c>preflights/</c> folder. Step 4c.2 classifies and REPORTS this population, so its presence is
    /// what stops the "no report line" assertion from being vacuous.
    /// </summary>
    internal static IReadOnlyList<string> PromptJudges(string planDir) =>
        Directory.EnumerateFiles(planDir, "*.prompt.md", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(p) is string parent &&
                        (Path.GetFileName(parent).Equals("guardrails", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(parent).Equals("preflights", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every plan folder under <paramref name="root"/> — a directory holding a <c>guardrails.json</c>.
    /// </summary>
    internal static IReadOnlyList<string> PlanFoldersUnder(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "guardrails.json", SearchOption.AllDirectories)
                .Select(p => Path.GetDirectoryName(p)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// True when <paramref name="configPath"/> declares NO tiering metadata — no <c>routing</c> block on
    /// any prompt runner and no top-level <c>tiering</c> block. This is Step 0.9's gate condition, read
    /// from the config rather than assumed, so a config that legitimately opts IN is excluded from the
    /// negative sweep instead of failing it.
    /// </summary>
    internal static bool IsUnconfiguredForTiering(string configPath)
    {
        using JsonDocument config = ParseJson(configPath);
        return FindPropertyPaths(config.RootElement, "routing").Count == 0 &&
               FindPropertyPaths(config.RootElement, "tiering").Count == 0;
    }

    private static string ThisDir([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}

/// <summary>
/// An <see cref="IExecutableProbe"/> for which every command resolves, so validating a committed
/// fixture never depends on whether this machine happens to have <c>pwsh</c> or <c>claude</c> on PATH.
/// </summary>
internal sealed class EveryCommandResolves : IExecutableProbe
{
    /// <inheritdoc/>
    public bool Exists(string command) => true;
}
