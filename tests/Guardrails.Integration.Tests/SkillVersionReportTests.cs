using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the pure drift logic behind the <c>guardrails --version</c> warning
/// (<see cref="SkillVersionReport.Build"/>), now reading the version from each installed skill's
/// <c>SKILL.md</c> frontmatter (<c>metadata.guardrails-version</c>, issue #156). The version read
/// is injected for the pure cases (matching vs mismatched, missing key/frontmatter/file), with a
/// couple of on-disk cases proving the real <c>SKILL.md</c> read end to end. Also: a skill absent
/// in a root (not reported), both scan roots scanned, and <c>+build</c> metadata ignored.
/// </summary>
public sealed class SkillVersionReportTests
{
    private static readonly IReadOnlyList<string> KnownSkills =
        new[] { "plan-breakdown", "guardrails-review" };

    // Real temp dirs are the cleanest way to exercise Directory.Exists folder-presence semantics;
    // the version CONTENT is read either from a real SKILL.md here or via the injectable seam.
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "gr-skillver-" + Guid.NewGuid().ToString("N"));

        public string MakeRoot(string name)
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Create a skill folder; with <paramref name="version"/> set, write a SKILL.md
        /// whose frontmatter carries metadata.guardrails-version.</summary>
        public void MakeSkill(string root, string skill, string? version)
        {
            string skillDir = Path.Combine(root, skill);
            Directory.CreateDirectory(skillDir);
            if (version is not null)
            {
                File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), SkillMd(skill, version));
            }
        }

        /// <summary>Create a skill folder whose SKILL.md has NO metadata.guardrails-version key.</summary>
        public void MakeSkillNoVersionKey(string root, string skill)
        {
            string skillDir = Path.Combine(root, skill);
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(
                Path.Combine(skillDir, "SKILL.md"),
                $"---\nname: {skill}\ndescription: a skill\n---\n# {skill}\n");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string SkillMd(string name, string version) =>
            $"---\nname: {name}\ndescription: |\n  A test skill.\nmetadata:\n  guardrails-version: {version}\n---\n# {name}\n";
    }

    [Fact]
    public void MatchingVersion_IsNotDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.27");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.27", status.InstalledVersion);
        Assert.False(status.Drifted);
    }

    [Fact]
    public void MismatchedVersion_IsDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.26");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.26", status.InstalledVersion);
        Assert.True(status.Drifted);
    }

    [Fact]
    public void SkillMdPresentButNoVersionKey_HasNullVersion_AndIsDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkillNoVersionKey(root, "plan-breakdown");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Null(status.InstalledVersion);
        Assert.True(status.Drifted);
    }

    [Fact]
    public void SkillFolderPresentButNoSkillMd_HasNullVersion_AndIsDrifted()
    {
        // A preview.26 sidecar install reads as unversioned: the folder exists, but its SKILL.md
        // carries no frontmatter version (here we simulate the extreme — no SKILL.md at all).
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", version: null); // folder only, no SKILL.md

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Null(status.InstalledVersion);
        Assert.True(status.Drifted);
    }

    [Fact]
    public void SkillMdWithNoFrontmatter_HasNullVersion_AndIsDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        string skillDir = Path.Combine(root, "plan-breakdown");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# plan-breakdown\nNo frontmatter here.\n");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Null(status.InstalledVersion);
        Assert.True(status.Drifted);
    }

    [Fact]
    public void SkillAbsentInRoot_IsNotReported()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        // Only plan-breakdown is installed; guardrails-review is absent.
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.27");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.27", KnownSkills, new[] { root });

        Assert.Single(report);
        Assert.Equal("plan-breakdown", report[0].Name);
        Assert.DoesNotContain(report, s => s.Name == "guardrails-review");
    }

    [Fact]
    public void BothRoots_AreScanned()
    {
        using var sb = new Sandbox();
        string userRoot = sb.MakeRoot("user");
        string projectRoot = sb.MakeRoot("project");
        sb.MakeSkill(userRoot, "plan-breakdown", "1.0.0-preview.27");      // matches
        sb.MakeSkill(projectRoot, "plan-breakdown", "1.0.0-preview.26");   // drifted

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build(
                "1.0.0-preview.27", KnownSkills, new[] { userRoot, projectRoot });

        Assert.Equal(2, report.Count);
        Assert.False(report.Single(s => s.Root == userRoot).Drifted);
        Assert.True(report.Single(s => s.Root == projectRoot).Drifted);
    }

    [Fact]
    public void BuildMetadata_IsIgnoredInComparison()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        // Same semantic version, different +build metadata on each side.
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.27+abc123");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build(
                "1.0.0-preview.27+def456", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.False(status.Drifted);
    }

    [Fact]
    public void ReadVersionSeam_IsHonoured()
    {
        // Folder presence still uses the disk; the version content comes from the injected seam.
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", version: null); // folder only, no SKILL.md

        IReadOnlyList<SkillVersionStatus> report = SkillVersionReport.Build(
            "1.0.0-preview.27",
            new[] { "plan-breakdown" },
            new[] { root },
            readVersion: _ => "1.0.0-preview.27");

        Assert.False(report.Single().Drifted);
    }

    [Fact]
    public void InstalledAtVersionX_ThenScannedAtX_ReportsNoDrift()
    {
        // End-to-end with the real on-disk read (issue #169): install an UNSTAMPED source at X,
        // then the reader over the install root finds metadata.guardrails-version = X → no drift.
        using var sb = new Sandbox();
        string source = sb.MakeRoot("source");
        string install = sb.MakeRoot("install");
        WriteUnstampedSource(source, "plan-breakdown");
        WriteUnstampedSource(source, "guardrails-review");

        SkillsInstaller.InstallAll(source, install, force: false, "1.0.0-preview.30");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.30", KnownSkills, new[] { install });

        Assert.Equal(2, report.Count);
        Assert.All(report, s =>
        {
            Assert.Equal("1.0.0-preview.30", s.InstalledVersion);
            Assert.False(s.Drifted);
        });
    }

    [Fact]
    public void InstalledAtVersionX_ThenScannedAtY_ReportsDrift_NotUnversioned()
    {
        // After a real install at X, a newer harness Y sees a stamped-but-mismatched version
        // (drifted), NOT 'unversioned' — unversioned is reserved for a truly absent stamp.
        using var sb = new Sandbox();
        string source = sb.MakeRoot("source");
        string install = sb.MakeRoot("install");
        WriteUnstampedSource(source, "plan-breakdown");

        SkillsInstaller.InstallAll(source, install, force: false, "1.0.0-preview.29");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.30", KnownSkills, new[] { install });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.29", status.InstalledVersion); // a real value, not null
        Assert.True(status.Drifted);
    }

    /// <summary>Write an UNSTAMPED source SKILL.md (frontmatter fence, no metadata block).</summary>
    private static void WriteUnstampedSource(string root, string skill)
    {
        string skillDir = Path.Combine(root, skill);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skill}\ndescription: a skill\n---\n# {skill}\n");
    }
}
