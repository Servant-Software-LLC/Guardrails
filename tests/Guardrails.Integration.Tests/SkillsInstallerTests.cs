using Guardrails.Cli;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the copy-and-stamp logic behind <c>guardrails skills install</c>: copying every
/// bundled skill folder from a fake source base dir into a temp target, the <c>--force</c>
/// overwrite path, the no-force skip-and-report path, and the install-time stamping of
/// <c>metadata.guardrails-version</c> into each installed <c>SKILL.md</c> frontmatter
/// (issue #169 — the bundled/published source is UNSTAMPED, so the version must be written at
/// install). A SKIPPED (non-force) skill is left untouched; a <c>--force</c> install removes a
/// leftover preview.26 <c>.guardrails-skill-version</c> sidecar. No packaged tool is needed —
/// the helper takes (sourceSkillsDir, targetDir, force, toolVersion) directly.
/// </summary>
public sealed class SkillsInstallerTests : IDisposable
{
    private const string Version = "1.0.0-preview.30";

    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public SkillsInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "guardrails-skills-test-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "skills");
        _target = Path.Combine(_root, "target");

        // A fake bundle: two skills, one with a nested references/ subfolder. Each SKILL.md
        // carries a minimal frontmatter fence (no metadata block) — the unstamped state the
        // published tool ships; install must add metadata.guardrails-version.
        WriteFile(Path.Combine(_source, "plan-breakdown", "SKILL.md"), Frontmatter("plan-breakdown"));
        WriteFile(Path.Combine(_source, "plan-breakdown", "references", "catalogue.md"), "# catalogue");
        WriteFile(Path.Combine(_source, "guardrails-review", "SKILL.md"), Frontmatter("guardrails-review"));
    }

    [Fact]
    public void InstallAll_CopiesEveryBundledSkill_PreservingNestedStructure()
    {
        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(SkillsInstaller.SkillOutcome.Installed, r.Outcome));

        Assert.True(File.Exists(Path.Combine(_target, "plan-breakdown", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_target, "plan-breakdown", "references", "catalogue.md")));
        Assert.True(File.Exists(Path.Combine(_target, "guardrails-review", "SKILL.md")));
    }

    [Fact]
    public void InstallAll_StampsToolVersionIntoEachInstalledSkillMd()
    {
        SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        Assert.Equal(Version, ReadInstalledVersion("plan-breakdown"));
        Assert.Equal(Version, ReadInstalledVersion("guardrails-review"));
    }

    [Fact]
    public void InstallAll_WithForce_ReStampsToTheNewVersion()
    {
        // First install at X stamps X; a --force reinstall at Y must re-stamp to Y.
        SkillsInstaller.InstallAll(_source, _target, force: false, "1.0.0-preview.29");
        Assert.Equal("1.0.0-preview.29", ReadInstalledVersion("plan-breakdown"));

        SkillsInstaller.InstallAll(_source, _target, force: true, "1.0.0-preview.30");
        Assert.Equal("1.0.0-preview.30", ReadInstalledVersion("plan-breakdown"));
    }

    [Fact]
    public void InstallAll_SourceSkillMdLacksMetadataBlock_GetsBlockAdded()
    {
        // The source SKILL.md has a frontmatter fence but no metadata: block; the stamper adds it.
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(
            File.ReadAllText(Path.Combine(_source, "plan-breakdown", "SKILL.md"))));

        SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        Assert.Equal(Version, ReadInstalledVersion("plan-breakdown"));
    }

    [Fact]
    public void InstallAll_SkillFolderWithoutSkillMd_DoesNotThrow()
    {
        // A bundled folder may legitimately have no top-level SKILL.md (e.g. assets-only); the
        // install copies it but skips stamping rather than throwing.
        WriteFile(Path.Combine(_source, "no-skill-md", "notes.md"), "# notes");

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        SkillsInstaller.SkillResult noSkillMd = results.Single(r => r.Name == "no-skill-md");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, noSkillMd.Outcome);
        Assert.True(File.Exists(Path.Combine(_target, "no-skill-md", "notes.md")));
        Assert.False(File.Exists(Path.Combine(_target, "no-skill-md", "SKILL.md")));
    }

    [Fact]
    public void InstallAll_WritesNoSidecarMarker()
    {
        // The version lives in SKILL.md frontmatter now; install must NOT create a sidecar.
        SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        Assert.False(File.Exists(Path.Combine(
            _target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName)));
        Assert.False(File.Exists(Path.Combine(
            _target, "guardrails-review", SkillsInstaller.LegacySidecarFileName)));
    }

    [Fact]
    public void InstallAll_WithForce_OverwritesExistingSkillFolder()
    {
        // Pre-seed a stale copy with extra cruft that a clean overwrite must remove.
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "STALE");
        WriteFile(Path.Combine(_target, "plan-breakdown", "stale-extra.md"), "remove me");

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: true, Version);

        SkillsInstaller.SkillResult planBreakdown = results.Single(r => r.Name == "plan-breakdown");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, planBreakdown.Outcome);

        // Content was refreshed from source (and stamped) and the stale extra file is gone.
        Assert.Equal(Version, ReadInstalledVersion("plan-breakdown"));
        Assert.False(File.Exists(Path.Combine(_target, "plan-breakdown", "stale-extra.md")));
    }

    [Fact]
    public void InstallAll_WithForce_RemovesLeftoverPreview26Sidecar()
    {
        // A prior preview.26 install left a sidecar; --force must not leave it lingering
        // (issue #156 migration), since a fresh copy carries no sidecar.
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "OLD");
        WriteFile(
            Path.Combine(_target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName),
            "1.0.0-preview.26");

        SkillsInstaller.InstallAll(_source, _target, force: true, Version);

        Assert.False(File.Exists(Path.Combine(
            _target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName)));
        Assert.Equal(Version, ReadInstalledVersion("plan-breakdown"));
    }

    [Fact]
    public void InstallAll_WithoutForce_LeavesExistingFolderUntouched_AndReportsSkipped()
    {
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "EXISTING — DO NOT TOUCH");

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        SkillsInstaller.SkillResult planBreakdown = results.Single(r => r.Name == "plan-breakdown");
        Assert.Equal(SkillsInstaller.SkillOutcome.Skipped, planBreakdown.Outcome);

        // A skipped folder is left exactly as it was — NOT re-stamped (its absent/old version
        // is precisely the drift signal --version surfaces).
        Assert.Equal("EXISTING — DO NOT TOUCH",
            File.ReadAllText(Path.Combine(_target, "plan-breakdown", "SKILL.md")));

        // The other, absent skill was still installed (and stamped).
        SkillsInstaller.SkillResult guardrailsReview = results.Single(r => r.Name == "guardrails-review");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, guardrailsReview.Outcome);
        Assert.Equal(Version, ReadInstalledVersion("guardrails-review"));
    }

    [Fact]
    public void InstallAll_WithoutForce_LeavesLeftoverSidecarUntouched()
    {
        // Without --force a present folder (with its stale sidecar) is the drift signal we keep.
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "EXISTING");
        WriteFile(
            Path.Combine(_target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName),
            "1.0.0-preview.26");

        SkillsInstaller.InstallAll(_source, _target, force: false, Version);

        Assert.True(File.Exists(Path.Combine(
            _target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName)));
    }

    [Fact]
    public void InstallAll_MissingSourceDir_Throws()
    {
        string missing = Path.Combine(_root, "does-not-exist");
        Assert.Throws<DirectoryNotFoundException>(
            () => SkillsInstaller.InstallAll(missing, _target, force: false, Version));
    }

    [Fact]
    public void ResolveTargetDir_ExplicitTarget_WinsVerbatim()
    {
        string explicitTarget = Path.Combine(_root, "custom");
        Assert.Equal(explicitTarget, SkillsInstaller.ResolveTargetDir(explicitTarget, project: true));
    }

    [Fact]
    public void ResolveTargetDir_Project_UsesCurrentDirDotClaudeSkills()
    {
        string expected = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills");
        Assert.Equal(expected, SkillsInstaller.ResolveTargetDir(null, project: true));
    }

    [Fact]
    public void ResolveTargetDir_Default_UsesHomeDotClaudeSkills()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
        Assert.Equal(expected, SkillsInstaller.ResolveTargetDir(null, project: false));
    }

    /// <summary>
    /// A minimal unstamped SKILL.md: a frontmatter fence with a name but no <c>metadata:</c>
    /// block — the state the published tool ships, which install must stamp.
    /// </summary>
    private static string Frontmatter(string name) =>
        $"---\nname: {name}\n---\n\n# {name}\n";

    /// <summary>Read the installed skill's stamped frontmatter version (or null).</summary>
    private string? ReadInstalledVersion(string skill) =>
        SkillFrontmatter.ReadGuardrailsVersion(
            File.ReadAllText(Path.Combine(_target, skill, "SKILL.md")));

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
