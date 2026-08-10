using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Covers <see cref="SkillFrontmatter.ReadGuardrailsVersion"/> — the reader that extracts
/// <c>metadata.guardrails-version</c> from a <c>SKILL.md</c> frontmatter (issue #156). The drift
/// check only needs "matching version or not", so anything other than a present, parseable key
/// reads as <c>null</c> (the <c>unversioned</c> signal).
///
/// <para>The stamped value is written as a BARE (unquoted) YAML scalar. Under the legacy
/// <c>1.0.0-preview.N</c> scheme that scalar always contained letters, so YAML could only ever
/// resolve it as a string. Stable <c>X.Y.0</c> versions (issue #421) are digits-and-dots, so the
/// digits-only shapes are pinned explicitly below — a resolver that treated one as a number
/// would round-trip a culture-formatted value and make every skill read as drifted.</para>
/// </summary>
public sealed class SkillFrontmatterTests
{
    [Theory]
    // Stable X.Y.0 — the current release scheme (bare digits and dots).
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("10.20.30")]
    // A two-component scalar is the genuinely YAML-numeric shape (it parses as a float). The
    // scheme never emits one, but pinning it proves the reader hands back raw text rather than
    // a parsed-and-reformatted number.
    [InlineData("1.1")]
    // Legacy prerelease — the shape every published preview carried.
    [InlineData("1.0.0-preview.49")]
    public void ReadsVersion_Verbatim_ForStableAndPrereleaseScalars(string version)
    {
        string md =
            $"---\nname: plan-breakdown\ndescription: |\n  A skill.\nmetadata:\n  guardrails-version: {version}\n---\n# body\n";

        Assert.Equal(version, SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void ReadsVersion_WhenPresentUnderMetadata()
    {
        string md =
            "---\nname: plan-breakdown\ndescription: |\n  A skill.\nmetadata:\n  guardrails-version: 1.1.0\n---\n# body\n";

        Assert.Equal("1.1.0", SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void ReadsVersion_WithCrlfLineEndings()
    {
        string md =
            "---\r\nname: x\r\nmetadata:\r\n  guardrails-version: 9.9.9\r\n---\r\n# body\r\n";

        Assert.Equal("9.9.9", SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void ReadsVersion_WhenMetadataHasOtherKeys()
    {
        string md =
            "---\nname: x\nmetadata:\n  author: someone\n  guardrails-version: 1.2.3\n---\nbody\n";

        Assert.Equal("1.2.3", SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void Null_WhenNoFrontmatterFence()
    {
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion("# plan-breakdown\nNo frontmatter.\n"));
    }

    [Fact]
    public void Null_WhenFrontmatterHasNoMetadataBlock()
    {
        string md = "---\nname: x\ndescription: a skill\n---\nbody\n";
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void Null_WhenMetadataHasNoVersionKey()
    {
        string md = "---\nname: x\nmetadata:\n  author: someone\n---\nbody\n";
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void Null_WhenOpeningFenceHasNoClose()
    {
        string md = "---\nname: x\nmetadata:\n  guardrails-version: 1.2.3\n# no closing fence\n";
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void Null_WhenVersionValueIsEmpty()
    {
        string md = "---\nname: x\nmetadata:\n  guardrails-version:\n---\nbody\n";
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void Null_WhenFrontmatterYamlIsMalformed()
    {
        // A garbled frontmatter is treated as unversioned, never an exception.
        string md = "---\nname: x\n  : : bad\nmetadata: [unclosed\n---\nbody\n";
        Assert.Null(SkillFrontmatter.ReadGuardrailsVersion(md));
    }

    [Fact]
    public void TrimsSurroundingWhitespace_OnTheValue()
    {
        string md = "---\nname: x\nmetadata:\n  guardrails-version: \"  1.2.3  \"\n---\nbody\n";
        Assert.Equal("1.2.3", SkillFrontmatter.ReadGuardrailsVersion(md));
    }
}
