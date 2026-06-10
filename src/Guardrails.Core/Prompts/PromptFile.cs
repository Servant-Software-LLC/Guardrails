using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Optional YAML frontmatter for a <c>*.prompt.md</c> action or guardrail (SSOT §4.2).
/// All keys are optional; absent frontmatter yields an all-null instance.
/// </summary>
public sealed record PromptFrontmatter
{
    /// <summary>Human description (SSOT §4.2); informational.</summary>
    public string? Description { get; init; }

    /// <summary>Runner name override (prompt actions/guardrails); null = use the config default.</summary>
    public string? Runner { get; init; }

    /// <summary>Turn ceiling override; null = inherit from the runner config.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Per-prompt timeout override in seconds; null = inherit from task/config.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>An instance with every field null (no frontmatter present).</summary>
    public static PromptFrontmatter Empty { get; } = new();
}

/// <summary>
/// A parsed <c>*.prompt.md</c> file: its (optional) frontmatter and the body that follows.
/// Frontmatter is a leading <c>---</c>-fenced YAML block (SSOT §4.2); when present it is
/// stripped from <see cref="Body"/>. Malformed YAML frontmatter is a loading error.
/// </summary>
public sealed record PromptFile
{
    /// <summary>The frontmatter metadata (<see cref="PromptFrontmatter.Empty"/> if none).</summary>
    public required PromptFrontmatter Frontmatter { get; init; }

    /// <summary>The prompt body — the file content after the frontmatter block (verbatim, no leading blank line).</summary>
    public required string Body { get; init; }
}

/// <summary>The outcome of parsing a prompt file: the file, or an error message for malformed frontmatter.</summary>
public sealed record PromptParseResult
{
    /// <summary>The parsed file when <see cref="Error"/> is null.</summary>
    public PromptFile? File { get; init; }

    /// <summary>A human-readable error when frontmatter could not be parsed; else null.</summary>
    public string? Error { get; init; }

    public bool Success => Error is null;
}

/// <summary>
/// Parses <c>*.prompt.md</c> files: separates an optional leading <c>---</c>-fenced YAML
/// frontmatter block from the body, deserializing the frontmatter with YamlDotNet (SSOT
/// §4.2). The body is everything after the closing fence (or the whole file when there is
/// no frontmatter). Malformed YAML inside a present fence is reported as an error.
/// </summary>
public static class PromptFileParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parse the prompt file content (the full file text).</summary>
    public static PromptParseResult Parse(string content)
    {
        // Normalize line endings to '\n' for fence detection; the body preserves the
        // original content after the fence verbatim.
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!StartsWithFence(normalized))
        {
            return new PromptParseResult { File = new PromptFile { Frontmatter = PromptFrontmatter.Empty, Body = content } };
        }

        // Find the closing fence: a line that is exactly "---" after the opening one.
        int firstNewline = normalized.IndexOf('\n');
        int searchFrom = firstNewline < 0 ? normalized.Length : firstNewline + 1;
        int closeIndex = FindClosingFence(normalized, searchFrom);

        if (closeIndex < 0)
        {
            // An opening fence with no close = malformed frontmatter.
            return new PromptParseResult { Error = "frontmatter opening '---' has no closing '---'." };
        }

        string yaml = normalized[searchFrom..closeIndex];
        int bodyStart = SkipToBody(normalized, closeIndex);
        string body = normalized[bodyStart..];

        RawFrontmatter? raw;
        try
        {
            raw = string.IsNullOrWhiteSpace(yaml)
                ? new RawFrontmatter()
                : Yaml.Deserialize<RawFrontmatter>(yaml);
        }
        catch (YamlException ex)
        {
            return new PromptParseResult { Error = $"malformed YAML frontmatter: {ex.Message}" };
        }

        raw ??= new RawFrontmatter();

        var frontmatter = new PromptFrontmatter
        {
            Description = raw.Description,
            Runner = raw.Runner,
            MaxTurns = raw.MaxTurns,
            TimeoutSeconds = raw.TimeoutSeconds
        };

        return new PromptParseResult { File = new PromptFile { Frontmatter = frontmatter, Body = body } };
    }

    private static bool StartsWithFence(string normalized) =>
        normalized.StartsWith("---\n", StringComparison.Ordinal) ||
        normalized == "---" ||
        normalized.StartsWith("---", StringComparison.Ordinal) && IsFenceOnlyFirstLine(normalized);

    private static bool IsFenceOnlyFirstLine(string normalized)
    {
        int newline = normalized.IndexOf('\n');
        string firstLine = newline < 0 ? normalized : normalized[..newline];
        return firstLine.Trim() == "---";
    }

    private static int FindClosingFence(string normalized, int searchFrom)
    {
        int index = searchFrom;
        while (index < normalized.Length)
        {
            int lineEnd = normalized.IndexOf('\n', index);
            string line = lineEnd < 0 ? normalized[index..] : normalized[index..lineEnd];
            if (line.Trim() == "---")
            {
                return index; // start of the closing-fence line
            }

            if (lineEnd < 0)
            {
                return -1;
            }

            index = lineEnd + 1;
        }

        return -1;
    }

    private static int SkipToBody(string normalized, int closeFenceLineStart)
    {
        int lineEnd = normalized.IndexOf('\n', closeFenceLineStart);
        if (lineEnd < 0)
        {
            return normalized.Length;
        }

        // Skip the closing-fence line and a single immediately-following blank line, so the
        // body starts at real content (matches authors' "--- \n\n body" convention).
        int afterFence = lineEnd + 1;
        if (afterFence < normalized.Length && normalized[afterFence] == '\n')
        {
            afterFence++;
        }

        return afterFence;
    }

    /// <summary>The raw YAML shape of the frontmatter (SSOT §4.2).</summary>
    private sealed class RawFrontmatter
    {
        public string? Description { get; set; }
        public string? Runner { get; set; }
        public int? MaxTurns { get; set; }
        public int? TimeoutSeconds { get; set; }
    }
}
