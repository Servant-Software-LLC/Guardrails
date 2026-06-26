using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the pure copy logic behind <c>guardrails skills install</c>: copying every bundled
/// skill folder from a fake source base dir into a temp target, the <c>--force</c> overwrite
/// path, and the no-force skip-and-report path. The version travels INSIDE each bundled
/// <c>SKILL.md</c> frontmatter (stamped at build, issue #156), so install is a plain copy that
/// writes no sidecar — and a <c>--force</c> install removes a leftover preview.26
/// <c>.guardrails-skill-version</c> sidecar. No packaged tool is needed — the helper takes
/// (sourceSkillsDir, targetDir, force) directly.
/// </summary>
public sealed class SkillsInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public SkillsInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "guardrails-skills-test-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "skills");
        _target = Path.Combine(_root, "target");

        // A fake bundle: two skills, one with a nested references/ subfolder. The SKILL.md
        // bodies stand in for the build-stamped frontmatter copies (content is opaque to copy).
        WriteFile(Path.Combine(_source, "plan-breakdown", "SKILL.md"), "# plan-breakdown");
        WriteFile(Path.Combine(_source, "plan-breakdown", "references", "catalogue.md"), "# catalogue");
        WriteFile(Path.Combine(_source, "guardrails-review", "SKILL.md"), "# guardrails-review");
    }

    [Fact]
    public void InstallAll_CopiesEveryBundledSkill_PreservingNestedStructure()
    {
        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: false);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(SkillsInstaller.SkillOutcome.Installed, r.Outcome));

        Assert.True(File.Exists(Path.Combine(_target, "plan-breakdown", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_target, "plan-breakdown", "references", "catalogue.md")));
        Assert.True(File.Exists(Path.Combine(_target, "guardrails-review", "SKILL.md")));
    }

    [Fact]
    public void InstallAll_WritesNoSidecarMarker()
    {
        // The version lives in SKILL.md frontmatter now; install must NOT create a sidecar.
        SkillsInstaller.InstallAll(_source, _target, force: false);

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
            SkillsInstaller.InstallAll(_source, _target, force: true);

        SkillsInstaller.SkillResult planBreakdown = results.Single(r => r.Name == "plan-breakdown");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, planBreakdown.Outcome);

        // Content was refreshed from source and the stale extra file is gone.
        Assert.Equal("# plan-breakdown", File.ReadAllText(Path.Combine(_target, "plan-breakdown", "SKILL.md")));
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

        SkillsInstaller.InstallAll(_source, _target, force: true);

        Assert.False(File.Exists(Path.Combine(
            _target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName)));
        Assert.Equal("# plan-breakdown",
            File.ReadAllText(Path.Combine(_target, "plan-breakdown", "SKILL.md")));
    }

    [Fact]
    public void InstallAll_WithoutForce_LeavesExistingFolderUntouched_AndReportsSkipped()
    {
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "EXISTING — DO NOT TOUCH");

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(_source, _target, force: false);

        SkillsInstaller.SkillResult planBreakdown = results.Single(r => r.Name == "plan-breakdown");
        Assert.Equal(SkillsInstaller.SkillOutcome.Skipped, planBreakdown.Outcome);

        // The existing skill folder was left exactly as it was.
        Assert.Equal("EXISTING — DO NOT TOUCH",
            File.ReadAllText(Path.Combine(_target, "plan-breakdown", "SKILL.md")));

        // The other, absent skill was still installed.
        SkillsInstaller.SkillResult guardrailsReview = results.Single(r => r.Name == "guardrails-review");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, guardrailsReview.Outcome);
        Assert.True(File.Exists(Path.Combine(_target, "guardrails-review", "SKILL.md")));
    }

    [Fact]
    public void InstallAll_WithoutForce_LeavesLeftoverSidecarUntouched()
    {
        // Without --force a present folder (with its stale sidecar) is the drift signal we keep.
        WriteFile(Path.Combine(_target, "plan-breakdown", "SKILL.md"), "EXISTING");
        WriteFile(
            Path.Combine(_target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName),
            "1.0.0-preview.26");

        SkillsInstaller.InstallAll(_source, _target, force: false);

        Assert.True(File.Exists(Path.Combine(
            _target, "plan-breakdown", SkillsInstaller.LegacySidecarFileName)));
    }

    [Fact]
    public void InstallAll_MissingSourceDir_Throws()
    {
        string missing = Path.Combine(_root, "does-not-exist");
        Assert.Throws<DirectoryNotFoundException>(
            () => SkillsInstaller.InstallAll(missing, _target, force: false));
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
