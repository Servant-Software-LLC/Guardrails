using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the pure copy logic behind <c>guardrails skills install</c>: copying every bundled
/// skill folder from a fake source base dir into a temp target, the <c>--force</c> overwrite
/// path, and the no-force skip-and-report path. No packaged tool is needed — the helper takes
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

        // A fake bundle: two skills, one with a nested references/ subfolder.
        WriteFile(Path.Combine(_source, "plan-breakdown", "SKILL.md"), "# plan-breakdown");
        WriteFile(Path.Combine(_source, "plan-breakdown", "references", "catalogue.md"), "# catalogue");
        WriteFile(Path.Combine(_source, "guardrail-review", "SKILL.md"), "# guardrail-review");
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
        Assert.True(File.Exists(Path.Combine(_target, "guardrail-review", "SKILL.md")));
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
        SkillsInstaller.SkillResult guardrailReview = results.Single(r => r.Name == "guardrail-review");
        Assert.Equal(SkillsInstaller.SkillOutcome.Installed, guardrailReview.Outcome);
        Assert.True(File.Exists(Path.Combine(_target, "guardrail-review", "SKILL.md")));
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
