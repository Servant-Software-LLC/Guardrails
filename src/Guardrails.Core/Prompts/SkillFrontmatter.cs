using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Reads the build-stamped version out of a skill's <c>SKILL.md</c> frontmatter
/// (<c>metadata.guardrails-version</c>, issue #156). Frontmatter is the same leading
/// <c>---</c>-fenced YAML block the prompt/skill files use (SSOT §4.2); the value is
/// deserialized with the same YamlDotNet pipeline rather than hand-rolled, so quoting and
/// escaping behave consistently.
///
/// The version is intentionally read leniently: a file with no frontmatter, no
/// <c>metadata</c> block, or no <c>guardrails-version</c> key yields <c>null</c> — the
/// <c>unversioned</c> signal the drift check turns into a "run <c>--force</c>" nudge. Malformed
/// YAML also yields <c>null</c> (a stale/garbled install is treated as unversioned, never an
/// exception).
/// </summary>
public static class SkillFrontmatter
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Extract <c>metadata.guardrails-version</c> from the full text of a <c>SKILL.md</c>, or
    /// <c>null</c> when absent/unparseable. The returned value is trimmed; an empty/whitespace
    /// value is treated as absent (<c>null</c>).
    /// </summary>
    public static string? ReadGuardrailsVersion(string skillMarkdown)
    {
        ArgumentNullException.ThrowIfNull(skillMarkdown);

        string? yaml = ExtractFrontmatterYaml(skillMarkdown);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        RawSkillFrontmatter? raw;
        try
        {
            raw = Yaml.Deserialize<RawSkillFrontmatter>(yaml);
        }
        catch (YamlException)
        {
            // A garbled frontmatter is treated as unversioned, not an error — the drift check
            // only needs "do we have a matching version or not".
            return null;
        }

        string? version = raw?.Metadata?.GuardrailsVersion?.Trim();
        return string.IsNullOrEmpty(version) ? null : version;
    }

    /// <summary>
    /// Return the YAML between the leading <c>---</c> fences (the same convention as
    /// <see cref="PromptFileParser"/>), or <c>null</c> if the content has no opening fence or
    /// no closing fence.
    /// </summary>
    private static string? ExtractFrontmatterYaml(string content)
    {
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return null;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return string.Join("\n", lines[1..i]);
            }
        }

        return null; // opening fence with no close
    }

    /// <summary>The slice of skill frontmatter we read: just the <c>metadata</c> block.</summary>
    private sealed class RawSkillFrontmatter
    {
        public RawMetadata? Metadata { get; set; }
    }

    private sealed class RawMetadata
    {
        public string? GuardrailsVersion { get; set; }
    }
}
