using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the pure drift logic behind the <c>guardrails --version</c> warning
/// (<see cref="SkillVersionReport.Build"/>). The marker read is injected, so these cases are
/// pure: matching vs mismatched versions, a present-but-unmarked install, a skill absent in a
/// root (not reported), both scan roots scanned, and <c>+build</c> metadata ignored.
/// </summary>
public sealed class SkillVersionReportTests
{
    private static readonly IReadOnlyList<string> KnownSkills =
        new[] { "plan-breakdown", "guardrails-review" };

    // Real temp dirs are the cleanest way to exercise Directory.Exists folder-presence semantics;
    // marker CONTENT is read either from real files here or via the injectable read seam.
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

        public void MakeSkill(string root, string skill, string? markerContent)
        {
            string skillDir = Path.Combine(root, skill);
            Directory.CreateDirectory(skillDir);
            if (markerContent is not null)
            {
                File.WriteAllText(
                    Path.Combine(skillDir, SkillVersionReport.MarkerFileName), markerContent);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    [Fact]
    public void MatchingVersion_IsNotDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.25");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.25", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.25", status.InstalledVersion);
        Assert.False(status.Drifted);
    }

    [Fact]
    public void MismatchedVersion_IsDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.24");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.25", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.24", status.InstalledVersion);
        Assert.True(status.Drifted);
    }

    [Fact]
    public void PresentButNoMarker_HasNullVersion_AndIsDrifted()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", markerContent: null);

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.25", KnownSkills, new[] { root });

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
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.25");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.25", KnownSkills, new[] { root });

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
        sb.MakeSkill(userRoot, "plan-breakdown", "1.0.0-preview.25");      // matches
        sb.MakeSkill(projectRoot, "plan-breakdown", "1.0.0-preview.24");   // drifted

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build(
                "1.0.0-preview.25", KnownSkills, new[] { userRoot, projectRoot });

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
        sb.MakeSkill(root, "plan-breakdown", "1.0.0-preview.25+abc123");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build(
                "1.0.0-preview.25+def456", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.False(status.Drifted);
    }

    [Fact]
    public void Marker_IsTrimmed_WhenRead()
    {
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", "  1.0.0-preview.25\n");

        IReadOnlyList<SkillVersionStatus> report =
            SkillVersionReport.Build("1.0.0-preview.25", KnownSkills, new[] { root });

        SkillVersionStatus status = report.Single(s => s.Name == "plan-breakdown");
        Assert.Equal("1.0.0-preview.25", status.InstalledVersion);
        Assert.False(status.Drifted);
    }

    [Fact]
    public void ReadMarkerSeam_IsHonoured()
    {
        // Folder presence still uses the disk; the marker content comes from the injected seam.
        using var sb = new Sandbox();
        string root = sb.MakeRoot("skills");
        sb.MakeSkill(root, "plan-breakdown", markerContent: null);

        IReadOnlyList<SkillVersionStatus> report = SkillVersionReport.Build(
            "1.0.0-preview.25",
            new[] { "plan-breakdown" },
            new[] { root },
            readMarker: _ => "1.0.0-preview.25");

        Assert.False(report.Single().Drifted);
    }
}
