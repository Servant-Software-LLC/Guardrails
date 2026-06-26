using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Covers <see cref="SkillFrontmatter.ReadGuardrailsVersion"/> — the reader that extracts
/// <c>metadata.guardrails-version</c> from a <c>SKILL.md</c> frontmatter (issue #156). The drift
/// check only needs "matching version or not", so anything other than a present, parseable key
/// reads as <c>null</c> (the <c>unversioned</c> signal).
/// </summary>
public sealed class SkillFrontmatterTests
{
    [Fact]
    public void ReadsVersion_WhenPresentUnderMetadata()
    {
        string md =
            "---\nname: plan-breakdown\ndescription: |\n  A skill.\nmetadata:\n  guardrails-version: 1.0.0-preview.27\n---\n# body\n";

        Assert.Equal("1.0.0-preview.27", SkillFrontmatter.ReadGuardrailsVersion(md));
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
