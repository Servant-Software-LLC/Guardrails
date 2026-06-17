using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Guardrails.Core.Breakdown;

/// <summary>The outcome of checking the GitGuardian baseline exclusion (issue #67).</summary>
public enum GitGuardianSuggestOutcome
{
    /// <summary>No enclosing git repository was found; nothing was printed.</summary>
    SkippedNoGitRepo,

    /// <summary>A config already excludes the baseline glob; nothing was printed.</summary>
    AlreadyExcluded,

    /// <summary>A config existed but could not be parsed; a GENERIC suggestion was printed.</summary>
    SkippedUnparseable,

    /// <summary>A targeted (or create) suggestion was printed; the user's config was NOT modified.</summary>
    SuggestionPrinted
}

/// <summary>The result of <see cref="GitGuardianConfig.SuggestBaselineExclusion"/>.</summary>
public sealed record GitGuardianSuggestResult(GitGuardianSuggestOutcome Outcome, string? ConfigPath);

/// <summary>
/// Detects whether the repository's GitGuardian/ggshield config excludes <c>guardrails.baseline</c>
/// files from secret scanning and, when it does not, PRINTS a copy-pasteable suggestion (issue #67).
///
/// <para><c>guardrails.baseline</c> is a committed, machine-generated <c>relativePath → SHA-256</c>
/// manifest (see <see cref="BreakdownManifest"/>). A SHA-256 hex digest is a high-entropy string
/// indistinguishable from an API key, so generic secret detectors flag it as a false positive and
/// block commits. The baseline MUST stay committed (it is the BASE for <c>guardrails merge</c>),
/// so the fix is a scanner path-exclude, not a gitignore.</para>
///
/// <para>This is <b>read-only and advisory</b>: it never writes, edits, or creates the user's
/// scanner config — it only inspects and suggests. It is called whenever the tool writes a baseline
/// (<c>guardrails lock</c> and the regeneration <c>merge --apply</c>), AFTER the baseline is written.
/// It walks up from the plan folder to the enclosing git repo root and inspects
/// <c>.gitguardian.yaml</c> (or an existing <c>.gitguardian.yml</c>), checking the v2
/// <c>secret.ignored-paths</c> and v1 top-level <c>paths-ignore</c> keys for the baseline path.
/// A read/parse error never escapes into <c>lock</c>/<c>merge</c> — it degrades to a generic
/// suggestion.</para>
/// </summary>
public static class GitGuardianConfig
{
    /// <summary>The glob that matches baseline files at any depth.</summary>
    public const string BaselineGlob = "**/guardrails.baseline";

    private const string PrimaryFileName = ".gitguardian.yaml";
    private const string AltFileName = ".gitguardian.yml";

    /// <summary>v2 ggshield key path: <c>secret.ignored-paths</c>.</summary>
    private const string SecretKey = "secret";
    private const string IgnoredPathsKey = "ignored-paths";

    /// <summary>v1 ggshield key: top-level <c>paths-ignore</c>.</summary>
    private const string PathsIgnoreKey = "paths-ignore";

    /// <summary>The bare baseline filename, used for conservative already-excluded matching.</summary>
    private const string BaselineFileName = "guardrails.baseline";

    /// <summary>
    /// Spellings we treat as already covering the baseline, so we don't nag when the user has it under
    /// any reasonable form. Compared ordinally after a conservative normalization (see <see cref="Covers"/>).
    /// </summary>
    private static readonly string[] CoveredForms =
    [
        BaselineGlob,                  // **/guardrails.baseline
        BaselineFileName,              // guardrails.baseline
        "./" + BaselineFileName,       // ./guardrails.baseline
    ];

    /// <summary>
    /// Inspect the git repo enclosing <paramref name="startDirectory"/> and, when its GitGuardian
    /// config does not already exclude <see cref="BaselineGlob"/>, PRINT a copy-pasteable suggestion to
    /// <paramref name="output"/>. This NEVER writes or modifies the user's config and NEVER throws:
    /// any read/parse error degrades to a generic suggestion. Returns a descriptive outcome:
    /// <list type="bullet">
    /// <item><see cref="GitGuardianSuggestOutcome.SkippedNoGitRepo"/> — no enclosing git repo; prints nothing.</item>
    /// <item><see cref="GitGuardianSuggestOutcome.AlreadyExcluded"/> — the config already covers the baseline; prints nothing.</item>
    /// <item><see cref="GitGuardianSuggestOutcome.SkippedUnparseable"/> — the config could not be read/parsed; prints a generic suggestion.</item>
    /// <item><see cref="GitGuardianSuggestOutcome.SuggestionPrinted"/> — a targeted (config present) or create (config absent) suggestion was printed.</item>
    /// </list>
    /// </summary>
    public static GitGuardianSuggestResult SuggestBaselineExclusion(string startDirectory, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            return Inspect(startDirectory, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
        {
            // Failure coupling is forbidden: a config we couldn't read must never break lock/merge.
            // Degrade to the generic suggestion so the user still learns the baseline may be flagged.
            PrintGenericSuggestion(output);
            return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.SkippedUnparseable, null);
        }
    }

    private static GitGuardianSuggestResult Inspect(string startDirectory, TextWriter output)
    {
        string? gitRoot = FindGitRoot(Path.GetFullPath(startDirectory));
        if (gitRoot is null)
        {
            // The baseline-as-secret problem only exists inside a scanned git repo — say nothing.
            return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.SkippedNoGitRepo, null);
        }

        string? configPath = ResolveExistingConfigPath(gitRoot);

        if (configPath is null)
        {
            // No scanner config at all → suggest the minimal file to create (but never create it).
            PrintCreateSuggestion(gitRoot, output);
            return new GitGuardianSuggestResult(
                GitGuardianSuggestOutcome.SuggestionPrinted, Path.Combine(gitRoot, PrimaryFileName));
        }

        object? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<object?>(File.ReadAllText(configPath));
        }
        catch (YamlException)
        {
            PrintGenericSuggestion(output);
            return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.SkippedUnparseable, configPath);
        }

        // An empty/whitespace-only or non-mapping file (scalar/sequence at root) is not a shape we can
        // reason about precisely. It plainly does not exclude the baseline, so suggest the v2 add-line.
        if (root is not IDictionary<object, object> map)
        {
            PrintV2AddSuggestion(configPath, output);
            return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.SuggestionPrinted, configPath);
        }

        if (AlreadyExcludes(map))
        {
            return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.AlreadyExcluded, configPath);
        }

        // Present but missing the exclusion → a targeted suggestion naming the file and the exact key
        // for that config's schema. A v1 file (top-level paths-ignore) gets the v1 line; otherwise v2.
        if (HasV1PathsIgnore(map))
        {
            PrintV1AddSuggestion(configPath, output);
        }
        else
        {
            PrintV2AddSuggestion(configPath, output);
        }

        return new GitGuardianSuggestResult(GitGuardianSuggestOutcome.SuggestionPrinted, configPath);
    }

    // --- already-excluded detection ---------------------------------------------------

    /// <summary>
    /// True when the config already excludes the baseline under either schema's ignored-paths key.
    /// Reads v2 <c>secret.ignored-paths</c> and v1 top-level <c>paths-ignore</c> only — read-only.
    /// </summary>
    private static bool AlreadyExcludes(IDictionary<object, object> map)
    {
        if (TryGetValue(map, SecretKey, out object? secretObj) &&
            secretObj is IDictionary<object, object> secretMap &&
            TryGetValue(secretMap, IgnoredPathsKey, out object? v2Ignored) &&
            SequenceCoversBaseline(v2Ignored))
        {
            return true;
        }

        return TryGetValue(map, PathsIgnoreKey, out object? v1Ignored) && SequenceCoversBaseline(v1Ignored);
    }

    private static bool HasV1PathsIgnore(IDictionary<object, object> map) =>
        TryGetValue(map, PathsIgnoreKey, out _);

    private static bool SequenceCoversBaseline(object? value) =>
        value is IEnumerable<object> seq && seq.Any(item => item is string s && Covers(s));

    /// <summary>
    /// Conservative already-covered check: normalize the entry (trim, strip a leading <c>./</c>, drop a
    /// trailing slash) and accept any of the reasonable spellings — <c>**/guardrails.baseline</c>,
    /// <c>guardrails.baseline</c>, <c>./guardrails.baseline</c> — plus any glob that ends in the baseline
    /// filename (e.g. <c>**/guardrails.baseline</c>, <c>*/guardrails.baseline</c>). The goal is to avoid
    /// nagging a user who has already excluded it under a form we'd accept; we err toward "covered".
    /// </summary>
    private static bool Covers(string entry)
    {
        string normalized = entry.Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (CoveredForms.Any(form => string.Equals(NormalizeForm(form), normalized, StringComparison.Ordinal)))
        {
            return true;
        }

        // Any glob whose final path segment is the baseline filename also covers it (e.g. */ or **/).
        return normalized.EndsWith("/" + BaselineFileName, StringComparison.Ordinal)
            || string.Equals(normalized, BaselineFileName, StringComparison.Ordinal);
    }

    private static string NormalizeForm(string form) =>
        form.StartsWith("./", StringComparison.Ordinal) ? form[2..] : form;

    // --- suggestion printing ----------------------------------------------------------

    private static void PrintCreateSuggestion(string gitRoot, TextWriter output)
    {
        string path = Path.Combine(gitRoot, PrimaryFileName);
        output.WriteLine(
            $"Suggestion: {BaselineFileName} is a high-entropy SHA-256 manifest that secret scanners " +
            "(ggshield/GitGuardian) may flag. No scanner config was found; if you use one, create " +
            $"{path} with:");
        output.WriteLine();
        output.WriteLine("    version: 2");
        output.WriteLine("    secret:");
        output.WriteLine("      ignored-paths:");
        output.WriteLine($"        - \"{BaselineGlob}\"");
        output.WriteLine();
    }

    private static void PrintV2AddSuggestion(string configPath, TextWriter output)
    {
        output.WriteLine(
            $"Suggestion: {configPath} does not exclude {BaselineFileName} from secret scanning. " +
            $"Add \"{BaselineGlob}\" under secret.ignored-paths:");
        output.WriteLine();
        output.WriteLine("    secret:");
        output.WriteLine("      ignored-paths:");
        output.WriteLine($"        - \"{BaselineGlob}\"");
        output.WriteLine();
    }

    private static void PrintV1AddSuggestion(string configPath, TextWriter output)
    {
        output.WriteLine(
            $"Suggestion: {configPath} does not exclude {BaselineFileName} from secret scanning. " +
            $"Add \"{BaselineGlob}\" under paths-ignore:");
        output.WriteLine();
        output.WriteLine("    paths-ignore:");
        output.WriteLine($"      - \"{BaselineGlob}\"");
        output.WriteLine();
    }

    private static void PrintGenericSuggestion(TextWriter output) =>
        output.WriteLine(
            $"Suggestion: couldn't read .gitguardian.yaml; if your scanner flags {BaselineFileName}, " +
            $"add \"{BaselineGlob}\" to its ignored paths.");

    // --- repo / config discovery ------------------------------------------------------

    /// <summary>Walk up from <paramref name="startDir"/> to the nearest ancestor containing <c>.git</c>.</summary>
    private static string? FindGitRoot(string startDir)
    {
        for (DirectoryInfo? dir = new(startDir); dir is not null; dir = dir.Parent)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            // A directory for a normal repo; a file for worktrees/submodules.
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Find an existing scanner config at the repo root, or <c>null</c> when none exists. When BOTH
    /// <c>.gitguardian.yaml</c> and <c>.gitguardian.yml</c> are present, prefer <c>.yaml</c> — that is
    /// ggshield's own precedence.
    /// </summary>
    private static string? ResolveExistingConfigPath(string gitRoot)
    {
        string yaml = Path.Combine(gitRoot, PrimaryFileName);
        if (File.Exists(yaml))
        {
            return yaml;
        }

        string yml = Path.Combine(gitRoot, AltFileName);
        return File.Exists(yml) ? yml : null;
    }

    private static bool TryGetValue(IDictionary<object, object> map, string key, out object? value)
    {
        foreach (KeyValuePair<object, object> pair in map)
        {
            if (pair.Key is string s && string.Equals(s, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
