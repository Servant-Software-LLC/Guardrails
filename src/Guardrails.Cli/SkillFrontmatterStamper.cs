namespace Guardrails.Cli;

/// <summary>
/// Injection of <c>metadata.guardrails-version</c> into a skill's <c>SKILL.md</c>
/// frontmatter (issue #156). The version is a release fact, not an author-typed value, so
/// <see cref="SkillsInstaller"/> stamps it into each INSTALLED copy of <c>SKILL.md</c> at
/// install time (issue #169 — the bundled/published source is left unstamped, since a
/// <c>PackAsTool</c> package would otherwise ship the unstamped publish output). The
/// repo-source files stay clean. <see cref="SkillVersionReport"/> later reads the same key
/// back to detect drift.
///
/// The transform is a surgical, line-oriented edit of the leading <c>---</c>-fenced YAML
/// block: it preserves every other key (notably the multiline <c>description: |</c> block)
/// and their order. Three cases:
/// <list type="bullet">
///   <item>a top-level <c>metadata:</c> block with a <c>guardrails-version:</c> child →
///   the value is replaced in place;</item>
///   <item>a <c>metadata:</c> block without that child → the child is inserted at the top of
///   the block;</item>
///   <item>no <c>metadata:</c> block → one is appended at the end of the frontmatter.</item>
/// </list>
/// A file without a leading frontmatter fence is returned unchanged (nothing to stamp).
///
/// Pure (string in, string out) so the install step and the unit tests exercise the identical
/// logic with no disk or console concerns.
/// </summary>
public static class SkillFrontmatterStamper
{
    /// <summary>The frontmatter key carrying the build version (under <c>metadata:</c>).</summary>
    public const string VersionKey = "guardrails-version";

    /// <summary>The top-level YAML key whose child is <see cref="VersionKey"/>.</summary>
    public const string MetadataKey = "metadata";

    /// <summary>
    /// Return <paramref name="content"/> with <c>metadata.guardrails-version</c> set to
    /// <paramref name="version"/>, preserving the original line endings and every other
    /// frontmatter key. Files without a leading <c>---</c> fence are returned verbatim.
    /// </summary>
    public static string Stamp(string content, string version)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(version);

        string newline = DetectNewline(content);
        string[] lines = content.Split('\n');

        // Strip a trailing '\r' that the '\n' split left on each line under CRLF, so the body
        // logic works on bare lines; the original newline is reattached on join.
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            // No frontmatter fence — nothing to stamp.
            return content;
        }

        int closeFence = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeFence = i;
                break;
            }
        }

        if (closeFence < 0)
        {
            // Opening fence with no close — malformed; leave it untouched rather than corrupt it.
            return content;
        }

        var result = new List<string>(lines.Length + 2);
        // Frontmatter is the lines strictly between the fences: [1, closeFence).
        var frontmatter = new List<string>();
        for (int i = 1; i < closeFence; i++)
        {
            frontmatter.Add(lines[i]);
        }

        List<string> stamped = StampFrontmatterLines(frontmatter, version);

        result.Add(lines[0]);            // opening "---"
        result.AddRange(stamped);
        result.Add(lines[closeFence]);   // closing "---"
        for (int i = closeFence + 1; i < lines.Length; i++)
        {
            result.Add(lines[i]);        // the body, verbatim
        }

        return string.Join(newline, result);
    }

    /// <summary>
    /// Apply the metadata-key edit to the frontmatter lines (those strictly between the two
    /// <c>---</c> fences) and return the new list.
    /// </summary>
    private static List<string> StampFrontmatterLines(List<string> frontmatter, string version)
    {
        int metadataLine = FindTopLevelKeyLine(frontmatter, MetadataKey);
        if (metadataLine < 0)
        {
            return AppendMetadataBlock(frontmatter, version);
        }

        return SetChildUnderMetadata(frontmatter, metadataLine, version);
    }

    /// <summary>
    /// Find the index of a top-level (column-0, no leading whitespace) <c>key:</c> line, or
    /// -1. Lines inside a deeper-indented block (e.g. the multiline description) are skipped
    /// because they are indented.
    /// </summary>
    private static int FindTopLevelKeyLine(IReadOnlyList<string> lines, string key)
    {
        string prefix = key + ":";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue; // indented = child of a previous key, not top level
            }

            string trimmed = line.TrimEnd();
            if (trimmed == key + ":" || line.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Append a fresh <c>metadata:</c> block carrying the version child to the end of the
    /// frontmatter.
    /// </summary>
    private static List<string> AppendMetadataBlock(List<string> frontmatter, string version)
    {
        var result = new List<string>(frontmatter);
        result.Add($"{MetadataKey}:");
        result.Add($"  {VersionKey}: {version}");
        return result;
    }

    /// <summary>
    /// With a <c>metadata:</c> block at <paramref name="metadataLine"/>, replace an existing
    /// <c>guardrails-version:</c> child in place, or insert one as the first child of the
    /// block (matching the block's child indentation).
    /// </summary>
    private static List<string> SetChildUnderMetadata(
        List<string> frontmatter, int metadataLine, string version)
    {
        // The metadata block's children are the indented lines that follow it, up to the next
        // top-level (column-0) key or the end of the frontmatter.
        int firstChild = metadataLine + 1;
        int afterBlock = firstChild;
        while (afterBlock < frontmatter.Count)
        {
            string line = frontmatter[afterBlock];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break; // next top-level key
            }

            afterBlock++;
        }

        // Look for an existing version child within the block.
        for (int i = firstChild; i < afterBlock; i++)
        {
            string trimmed = frontmatter[i].TrimStart();
            if (trimmed.StartsWith(VersionKey + ":", StringComparison.Ordinal))
            {
                string indent = frontmatter[i][..(frontmatter[i].Length - trimmed.Length)];
                var replaced = new List<string>(frontmatter);
                replaced[i] = $"{indent}{VersionKey}: {version}";
                return replaced;
            }
        }

        // No version child — insert one, matching the indentation of the block's existing
        // children when present, else a two-space default.
        string childIndent = "  ";
        if (firstChild < afterBlock)
        {
            string firstChildLine = frontmatter[firstChild];
            string firstTrimmed = firstChildLine.TrimStart();
            childIndent = firstChildLine[..(firstChildLine.Length - firstTrimmed.Length)];
        }

        var inserted = new List<string>(frontmatter);
        inserted.Insert(firstChild, $"{childIndent}{VersionKey}: {version}");
        return inserted;
    }

    /// <summary>
    /// Detect the dominant newline of <paramref name="content"/> so the stamped output keeps
    /// the original style (CRLF on Windows-authored files, LF otherwise).
    /// </summary>
    private static string DetectNewline(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
