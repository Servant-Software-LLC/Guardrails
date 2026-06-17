using Guardrails.Core.Breakdown;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests for the GitGuardian baseline exclusion (issue #67): when the tool writes a
/// <c>guardrails.baseline</c>, it ensures the enclosing git repo's <c>.gitguardian.yaml</c> excludes
/// baseline files from secret scanning — merging into any existing config, never overwriting, and
/// idempotently. A SHA-256 manifest is high-entropy and trips generic scanners as a false positive.
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

    [Fact]
    public void NoGitRepo_ReturnsSkipped_AndWritesNothing()
    {
        // _root has no .git anywhere above it (temp dir) → nothing to do.
        string planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(planDir);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.SkippedNoGitRepo, result.Outcome);
        Assert.False(File.Exists(ConfigYaml));
        Assert.False(File.Exists(ConfigYml));
    }

    [Fact]
    public void CreatesConfig_AtGitRoot_WhenNoneExists()
    {
        string planDir = MakeRepoWithPlanDir();

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.Created, result.Outcome);
        Assert.Equal(ConfigYaml, result.ConfigPath);
        Assert.True(File.Exists(ConfigYaml), "config must be written at the git root, not the plan folder");
        string content = File.ReadAllText(ConfigYaml);
        Assert.Contains(GitGuardianConfig.BaselineGlob, content);
        Assert.Contains("ignored-paths", content);
        Assert.Contains("version: 2", content);
    }

    [Fact]
    public void Idempotent_SecondRunIsAlreadyPresent_AndByteIdentical()
    {
        string planDir = MakeRepoWithPlanDir();

        GitGuardianConfig.EnsureBaselineExclusion(planDir);
        byte[] first = File.ReadAllBytes(ConfigYaml);

        GitGuardianEnsureResult second = GitGuardianConfig.EnsureBaselineExclusion(planDir);
        byte[] after = File.ReadAllBytes(ConfigYaml);

        Assert.Equal(GitGuardianEnsureOutcome.AlreadyPresent, second.Outcome);
        Assert.Equal(first, after); // unchanged — no churn
    }

    [Fact]
    public void MergesIntoExistingV2_PreservingOtherEntriesAndKeys()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            version: 2
            secret:
              show-secrets: true
              ignored-paths:
                - "**/node_modules"
            """);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.Updated, result.Outcome);
        string content = File.ReadAllText(ConfigYaml);
        Assert.Contains("guardrails.baseline", content); // added
        Assert.Contains("node_modules", content);        // existing entry preserved
        Assert.Contains("show-secrets", content);         // unrelated sibling key preserved
    }

    [Fact]
    public void AlreadyPresent_WhenExistingConfigContainsGlob()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            version: 2
            secret:
              ignored-paths:
                - "**/guardrails.baseline"
            """);
        byte[] before = File.ReadAllBytes(ConfigYaml);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.AlreadyPresent, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(ConfigYaml)); // untouched
    }

    [Fact]
    public void MergesIntoExistingV1_TopLevelPathsIgnore()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            paths-ignore:
              - "**/vendor"
            """);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.Updated, result.Outcome);
        string content = File.ReadAllText(ConfigYaml);
        Assert.Contains("guardrails.baseline", content);
        Assert.Contains("vendor", content);          // v1 entry preserved
        Assert.DoesNotContain("ignored-paths", content); // stayed in the v1 key, didn't add a v2 block
    }

    [Fact]
    public void PrefersExistingYmlExtension_OverCreatingYaml()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYml,
            """
            version: 2
            secret:
              ignored-paths: []
            """);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.Updated, result.Outcome);
        Assert.Equal(ConfigYml, result.ConfigPath);
        Assert.False(File.Exists(ConfigYaml), "must not create a second .yaml when .yml exists");
        Assert.Contains("guardrails.baseline", File.ReadAllText(ConfigYml));
    }

    [Fact]
    public void IntroducesV2Block_WhenConfigHasNeitherSecretNorPathsIgnore()
    {
        string planDir = MakeRepoWithPlanDir();
        File.WriteAllText(ConfigYaml,
            """
            # an unrelated existing setting
            instances:
              - https://dashboard.example.com
            """);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.Updated, result.Outcome);
        string content = File.ReadAllText(ConfigYaml);
        Assert.Contains("guardrails.baseline", content);
        Assert.Contains("ignored-paths", content);    // a v2 secret block was introduced
        Assert.Contains("dashboard.example.com", content); // unrelated key preserved
    }

    [Fact]
    public void Unparseable_LeavesFileUntouched()
    {
        string planDir = MakeRepoWithPlanDir();
        const string malformed = "secret: [ this is : not valid yaml";
        File.WriteAllText(ConfigYaml, malformed);

        GitGuardianEnsureResult result = GitGuardianConfig.EnsureBaselineExclusion(planDir);

        Assert.Equal(GitGuardianEnsureOutcome.SkippedUnparseable, result.Outcome);
        Assert.Equal(malformed, File.ReadAllText(ConfigYaml));
    }
}
