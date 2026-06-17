using Guardrails.Core.Breakdown;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests for the GitGuardian baseline-exclusion SUGGESTION (issue #67): when the tool writes a
/// <c>guardrails.baseline</c>, it DETECTS whether the enclosing git repo's <c>.gitguardian.yaml</c>
/// excludes baseline files from secret scanning and, if not, PRINTS a copy-pasteable suggestion. It
/// is read-only and advisory — it never writes, edits, or creates the user's scanner config. A
/// SHA-256 manifest is high-entropy and trips generic scanners as a false positive.
/// </summary>
public sealed class GitGuardianConfigTests : IDisposable
{
    private readonly string _root;

    public GitGuardianConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-ggc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Create a fake git repo root (a `.git` dir) with a nested plan folder; return the plan folder.</summary>
    private string MakeRepoWithPlanDir()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        string planDir = Path.Combine(_root, "plans", "0003-plan");
        Directory.CreateDirectory(planDir);
        return planDir;
    }

    private string ConfigYaml => Path.Combine(_root, ".gitguardian.yaml");
    private string ConfigYml => Path.Combine(_root, ".gitguardian.yml");

    private static (GitGuardianSuggestResult Result, string Output) Suggest(string planDir)
    {
        var writer = new StringWriter();
        GitGuardianSuggestResult result = GitGuardianConfig.SuggestBaselineExclusion(planDir, writer);
        return (result, writer.ToString());
    }

    [Fact]
    public void NoGitRepo_ReturnsSkipped_AndPrintsNothing()
    {
        // _root has no .git anywhere above it (temp dir) → the baseline-as-secret problem can't apply.
        string planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(planDir);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.SkippedNoGitRepo, result.Outcome);
        Assert.Equal("", output);
        Assert.False(File.Exists(ConfigYaml));
        Assert.False(File.Exists(ConfigYml));
    }

    [Fact]
    public void ConfigAbsent_PrintsCreateSuggestion_AndWritesNoFile()
    {
        string planDir = MakeRepoWithPlanDir();

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.SuggestionPrinted, result.Outcome);
        Assert.Contains(GitGuardianConfig.BaselineGlob, output, StringComparison.Ordinal);
        Assert.Contains("version: 2", output, StringComparison.Ordinal);
        Assert.Contains("ignored-paths", output, StringComparison.Ordinal);
        // Advisory only: it suggests creating the file but must NOT create it.
        Assert.False(File.Exists(ConfigYaml), "must not create .gitguardian.yaml — suggestion only");
        Assert.False(File.Exists(ConfigYml));
    }

    [Fact]
    public void ConfigPresentWithoutExclusion_V2_PrintsV2AddLine_AndLeavesFileUntouched()
    {
        string planDir = MakeRepoWithPlanDir();
        const string original =
            """
            version: 2
            secret:
              show-secrets: true
              ignored-paths:
                - "**/node_modules"
            """;
        File.WriteAllText(ConfigYaml, original);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.SuggestionPrinted, result.Outcome);
        Assert.Equal(ConfigYaml, result.ConfigPath);
        Assert.Contains("secret.ignored-paths", output, StringComparison.Ordinal); // names the v2 key
        Assert.Contains(GitGuardianConfig.BaselineGlob, output, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(ConfigYaml)); // read-only: file untouched
    }

    [Fact]
    public void ConfigPresentWithoutExclusion_V1_PrintsV1PathsIgnoreLine_AndLeavesFileUntouched()
    {
        string planDir = MakeRepoWithPlanDir();
        const string original =
            """
            paths-ignore:
              - "**/vendor"
            """;
        File.WriteAllText(ConfigYaml, original);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.SuggestionPrinted, result.Outcome);
        Assert.Contains("paths-ignore", output, StringComparison.Ordinal); // the v1 key, not secret.ignored-paths
        Assert.DoesNotContain("secret.ignored-paths", output, StringComparison.Ordinal);
        Assert.Contains(GitGuardianConfig.BaselineGlob, output, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(ConfigYaml)); // read-only: file untouched
    }

    [Fact]
    public void AlreadyExcluded_V2_IsQuiet()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            version: 2
            secret:
              ignored-paths:
                - "**/guardrails.baseline"
            """);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.AlreadyExcluded, result.Outcome);
        Assert.Equal("", output);
    }

    [Fact]
    public void AlreadyExcluded_ViaSpellingVariant_IsQuiet()
    {
        // The conservative normalization treats `guardrails.baseline` (no **/ prefix) as already
        // covered, so a user who excluded it under that spelling is not nagged.
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            version: 2
            secret:
              ignored-paths:
                - "guardrails.baseline"
            """);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.AlreadyExcluded, result.Outcome);
        Assert.Equal("", output);
    }

    [Fact]
    public void Unparseable_PrintsGenericSuggestion_AndLeavesFileUntouched()
    {
        string planDir = MakeRepoWithPlanDir();
        const string malformed = "secret: [ this is : not valid yaml";
        File.WriteAllText(ConfigYaml, malformed);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.SkippedUnparseable, result.Outcome);
        Assert.Contains("couldn't read", output, StringComparison.Ordinal);
        Assert.Contains(GitGuardianConfig.BaselineGlob, output, StringComparison.Ordinal);
        Assert.Equal(malformed, File.ReadAllText(ConfigYaml)); // read-only: file untouched
    }

    [Fact]
    public void BothYamlAndYmlPresent_PrefersYaml()
    {
        // ggshield precedence: .yaml wins over .yml. The .yaml here already excludes the baseline
        // (quiet); the .yml does NOT — proving we read .yaml, not .yml.
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            version: 2
            secret:
              ignored-paths:
                - "**/guardrails.baseline"
            """);
        File.WriteAllText(ConfigYml,
            """
            version: 2
            secret:
              ignored-paths: []
            """);

        (GitGuardianSuggestResult result, string output) = Suggest(planDir);

        Assert.Equal(GitGuardianSuggestOutcome.AlreadyExcluded, result.Outcome);
        Assert.Equal(ConfigYaml, result.ConfigPath);
        Assert.Equal("", output);
    }
}
