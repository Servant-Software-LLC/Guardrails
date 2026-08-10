using Guardrails.Cli;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers <see cref="SkillFrontmatterStamper"/> — the injection of
/// <c>metadata.guardrails-version</c> into a <c>SKILL.md</c> frontmatter (issue #156), now run
/// at install time by <see cref="SkillsInstaller"/> (issue #169). A round-trip through
/// <see cref="SkillFrontmatter.ReadGuardrailsVersion"/> pins the read/write contract. Each case
/// asserts the rest of the frontmatter (notably the multiline <c>description: |</c> block)
/// survives untouched.
///
/// <para>The stamper writes the version as a BARE (unquoted) YAML scalar. Legacy
/// <c>1.0.0-preview.N</c> versions always contained letters, so YAML could only read them back as
/// strings; stable <c>X.Y.0</c> versions (issue #421) are digits and dots, so
/// <see cref="RoundTripsVerbatim_ForStableAndPrereleaseVersions"/> pins the write→read round trip
/// for both shapes explicitly.</para>
/// </summary>
public sealed class SkillFrontmatterStamperTests
{
    private const string Version = "1.1.0";

    [Theory]
    // Stable X.Y.0 — the current release scheme.
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("10.20.30")]
    // The genuinely YAML-numeric shape (a two-component scalar parses as a float). The scheme
    // never emits one, but pinning it proves the bare-scalar write survives a read verbatim
    // rather than coming back as a culture-formatted number.
    [InlineData("1.1")]
    // Legacy prerelease — the shape every published preview carried.
    [InlineData("1.0.0-preview.49")]
    public void RoundTripsVerbatim_ForStableAndPrereleaseVersions(string version)
    {
        // Both stamper paths: appending a fresh metadata block, and replacing an existing value.
        string noMetadata = "---\nname: x\ndescription: a skill\n---\nbody\n";
        string withMetadata = "---\nname: x\nmetadata:\n  guardrails-version: 0.0.1\n---\nbody\n";

        Assert.Equal(version, SkillFrontmatter.ReadGuardrailsVersion(
            SkillFrontmatterStamper.Stamp(noMetadata, version)));
        Assert.Equal(version, SkillFrontmatter.ReadGuardrailsVersion(
            SkillFrontmatterStamper.Stamp(withMetadata, version)));
    }

    [Fact]
    public void RoundTripsThroughDrift_StableStampedSkill_MatchesStableHarness()
    {
        // The end-to-end shape of the real drift check with a STABLE version on both sides: a
        // skill stamped by the running tool must not read as drifted. Under the preview scheme
        // this path only ever saw letter-bearing scalars.
        string stamped = SkillFrontmatterStamper.Stamp(
            "---\nname: plan-breakdown\ndescription: a skill\n---\nbody\n", "1.1.0");
        string installed = SkillFrontmatter.ReadGuardrailsVersion(stamped)!;

        Assert.Equal(
            GuardrailsVersion.Normalize("1.1.0"),
            GuardrailsVersion.Normalize(installed));
    }

    [Fact]
    public void AppendsMetadataBlock_WhenAbsent_AndKeepsDescription()
    {
        string input =
            "---\nname: plan-breakdown\ndescription: |\n  Line one.\n  Line two.\n---\n# Body\n";

        string output = SkillFrontmatterStamper.Stamp(input, Version);

        Assert.Equal(Version, SkillFrontmatter.ReadGuardrailsVersion(output));
        Assert.Contains("name: plan-breakdown", output);
        Assert.Contains("  Line one.\n  Line two.", output);
        Assert.Contains("metadata:\n  guardrails-version: " + Version, output);
        Assert.Contains("# Body", output);
        // The metadata block sits inside the frontmatter (before the closing fence).
        int metaIdx = output.IndexOf("metadata:", StringComparison.Ordinal);
        int closeIdx = output.IndexOf("\n---", output.IndexOf('\n') + 1, StringComparison.Ordinal);
        Assert.True(metaIdx >= 0 && metaIdx < closeIdx);
    }

    [Fact]
    public void ReplacesExistingVersion_InPlace_NotDuplicated()
    {
        string input =
            "---\nname: x\nmetadata:\n  guardrails-version: 0.0.0-old\n---\nbody\n";

        string output = SkillFrontmatterStamper.Stamp(input, Version);

        Assert.Equal(Version, SkillFrontmatter.ReadGuardrailsVersion(output));
        // Exactly one occurrence of the key — no duplicate line.
        int count = output.Split("guardrails-version:").Length - 1;
        Assert.Equal(1, count);
        Assert.DoesNotContain("0.0.0-old", output);
    }

    [Fact]
    public void InsertsVersionChild_IntoExistingMetadataBlock_PreservingSiblings()
    {
        string input =
            "---\nname: x\nmetadata:\n  author: someone\n---\nbody\n";

        string output = SkillFrontmatterStamper.Stamp(input, Version);

        Assert.Equal(Version, SkillFrontmatter.ReadGuardrailsVersion(output));
        Assert.Contains("author: someone", output);
    }

    [Fact]
    public void NoFrontmatterFence_ReturnsContentUnchanged()
    {
        string input = "# plan-breakdown\nNo frontmatter here.\n";
        Assert.Equal(input, SkillFrontmatterStamper.Stamp(input, Version));
    }

    [Fact]
    public void OpeningFenceWithNoClose_ReturnsContentUnchanged()
    {
        string input = "---\nname: x\nno closing fence\n";
        Assert.Equal(input, SkillFrontmatterStamper.Stamp(input, Version));
    }

    [Fact]
    public void PreservesCrlfLineEndings()
    {
        string input = "---\r\nname: x\r\ndescription: a skill\r\n---\r\n# Body\r\n";

        string output = SkillFrontmatterStamper.Stamp(input, Version);

        Assert.Contains("\r\n", output);
        Assert.DoesNotContain("\n\n---", output.Replace("\r\n", "\n")); // no stray blank-line corruption
        Assert.Equal(Version, SkillFrontmatter.ReadGuardrailsVersion(output));
    }

    [Fact]
    public void Idempotent_StampingTwiceYieldsSameResult()
    {
        string input = "---\nname: x\ndescription: a skill\n---\nbody\n";

        string once = SkillFrontmatterStamper.Stamp(input, Version);
        string twice = SkillFrontmatterStamper.Stamp(once, Version);

        Assert.Equal(once, twice);
    }
}
