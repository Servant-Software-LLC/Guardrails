using Guardrails.Cli;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Asserts the build/pack contract from issue #156: the BUNDLED <c>SKILL.md</c> copies in the
/// Guardrails.Cli build output carry <c>metadata.guardrails-version</c> (injected by the
/// StampSkillVersions MSBuild target), while the repo-source <c>.claude/skills/**/SKILL.md</c>
/// files stay clean (no key). Guardrails.Cli is a project dependency of this test assembly, so it
/// is built before these tests run; the output is read under the same build configuration this
/// assembly was compiled with.
///
/// Also pins PARITY: the MSBuild stamping task (<c>StampSkillVersionsTask</c>) carries a verbatim
/// copy of <see cref="SkillFrontmatterStamper.Stamp"/> (it cannot reference the not-yet-built CLI
/// assembly, and must not drag Microsoft.Build into the shipped tool). The parity test re-derives
/// each bundled output file by running the helper over the repo source and asserts it is
/// byte-identical to what the task actually wrote — so any divergence between the two copies of the
/// algorithm fails the suite.
/// </summary>
public sealed class StampInjectionBuildTests
{
    private static readonly string[] BundledSkills =
        { "plan-breakdown", "guardrails-review", "guardrails-domain-knowledge" };

    [Fact]
    public void BuildOutput_BundledSkillMd_CarriesGuardrailsVersion()
    {
        string outputSkills = CliOutputSkillsDir();

        foreach (string skill in BundledSkills)
        {
            string skillMd = Path.Combine(outputSkills, skill, "SKILL.md");
            Assert.True(File.Exists(skillMd), $"expected bundled SKILL.md at {skillMd}");

            string? version = SkillFrontmatter.ReadGuardrailsVersion(File.ReadAllText(skillMd));
            Assert.False(
                string.IsNullOrWhiteSpace(version),
                $"bundled {skill}/SKILL.md should carry metadata.guardrails-version after build");
        }
    }

    [Fact]
    public void RepoSource_SkillMd_HasNoStampedVersion()
    {
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string sourceSkills = Path.Combine(repoRoot, ".claude", "skills");

        foreach (string skill in BundledSkills)
        {
            string skillMd = Path.Combine(sourceSkills, skill, "SKILL.md");
            Assert.True(File.Exists(skillMd), $"expected source SKILL.md at {skillMd}");

            string? version = SkillFrontmatter.ReadGuardrailsVersion(File.ReadAllText(skillMd));
            Assert.True(
                version is null,
                $"source {skill}/SKILL.md must stay clean — the stamp belongs in the build output only");
        }
    }

    [Fact]
    public void BuildOutput_MatchesHelperStamp_OverRepoSource()
    {
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string sourceSkills = Path.Combine(repoRoot, ".claude", "skills");
        string outputSkills = CliOutputSkillsDir();

        foreach (string skill in BundledSkills)
        {
            string sourceMd = Path.Combine(sourceSkills, skill, "SKILL.md");
            string outputMd = Path.Combine(outputSkills, skill, "SKILL.md");
            Assert.True(File.Exists(outputMd), $"expected bundled SKILL.md at {outputMd}");

            // The version the MSBuild task actually stamped (= $(Version) at build time).
            string? builtVersion =
                SkillFrontmatter.ReadGuardrailsVersion(File.ReadAllText(outputMd));
            Assert.False(
                string.IsNullOrWhiteSpace(builtVersion),
                $"bundled {skill}/SKILL.md should carry a stamped version");

            // Re-derive the expected output with the unit-tested helper and require an exact match:
            // proves StampSkillVersionsTask's verbatim copy of the algorithm has not diverged.
            string expected = SkillFrontmatterStamper.Stamp(File.ReadAllText(sourceMd), builtVersion!);
            Assert.Equal(expected, File.ReadAllText(outputMd));
        }
    }

    /// <summary>
    /// The Guardrails.Cli build output's <c>skills/</c> dir, under the same configuration this
    /// test assembly was built in (derived from this assembly's own output path).
    /// </summary>
    private static string CliOutputSkillsDir()
    {
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string configuration = ConfigurationFromTestOutputPath(AppContext.BaseDirectory);
        return Path.Combine(
            repoRoot, "src", "Guardrails.Cli", "bin", configuration, "net8.0", "skills");
    }

    /// <summary>Derive Debug/Release from the test assembly's output path (…/bin/&lt;cfg&gt;/net8.0/).</summary>
    private static string ConfigurationFromTestOutputPath(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir);
        // baseDir = …/bin/<configuration>/net8.0  → the configuration is the grandparent.
        DirectoryInfo? net = dir.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? dir
            : dir.Parent; // tolerate a trailing separator producing a different leaf
        string? configuration = net?.Parent?.Name;
        return string.IsNullOrEmpty(configuration) ? "Release" : configuration;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Guardrails.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Guardrails.sln) from " + start);
    }
}
