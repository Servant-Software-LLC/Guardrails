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
/// </summary>
public sealed class SkillFrontmatterStamperTests
{
    private const string Version = "1.0.0-preview.27";

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
