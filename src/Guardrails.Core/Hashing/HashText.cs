using System.Text;

namespace Guardrails.Core.Hashing;

/// <summary>
/// Shared primitives for the plan-definition family of content hashes — <see cref="Journal.PlanHash"/>
/// (SSOT §7), <see cref="Journal.PlanDefinitionHash"/> (§7.3), the future per-task
/// <c>TaskDefinitionHash</c> (§7.2, #274), and the diagram staleness key
/// <see cref="Graph.GraphSourceHash"/> (§10). Centralizes the two things every one of them must agree
/// on so they cannot silently drift: (1) newline normalization, and (2) the labeled-segment framing.
/// Also provides the deterministic recursive file enumeration those hashes fold folders in with.
/// </summary>
internal static class HashText
{
    /// <summary>
    /// Unit / record separators that keep a segment's label and body — and adjacent segments —
    /// from colliding. Control characters (U+001F / U+001E) so they never appear in real text.
    /// </summary>
    public const char UnitSeparator = '';
    public const char RecordSeparator = '';

    /// <summary>
    /// Collapse <c>\r\n</c> and lone <c>\r</c> to <c>\n</c> so CRLF/LF checkouts hash identically.
    /// The single copy every plan hash normalizes through (issue #260 consolidation — previously
    /// <see cref="Journal.PlanHash"/> and <see cref="Graph.GraphSourceHash"/> each carried their own).
    /// </summary>
    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Append one labeled file segment to <paramref name="builder"/>: the <paramref name="label"/>, a
    /// unit separator, the file's newline-normalized bytes (empty when the file is absent), then a
    /// record separator. Labeling the segment means reordering or renaming a file changes the hash even
    /// when bodies are unchanged.
    /// </summary>
    public static void AppendFile(StringBuilder builder, string label, string absolutePath)
    {
        builder.Append(label).Append(UnitSeparator);
        if (File.Exists(absolutePath))
        {
            builder.Append(NormalizeNewlines(File.ReadAllText(absolutePath)));
        }

        builder.Append(RecordSeparator);
    }

    /// <summary>
    /// Enumerate every file under <paramref name="folder"/> recursively, yielding each as
    /// <c>(Label, AbsolutePath)</c> where <c>Label</c> is the file's <c>/</c>-normalized path relative
    /// to <paramref name="labelRoot"/>, sorted by that label ordinally. Returns nothing when the folder
    /// is absent. Because it lists FILES (not parsed guardrail entries) it catches every <c>.json</c>
    /// sidecar and any other authored artifact in the folder (SSOT §7.3).
    /// </summary>
    public static IEnumerable<(string Label, string AbsolutePath)> EnumerateFolderFiles(
        string labelRoot, string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        var files = new List<(string Label, string AbsolutePath)>();
        foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            files.Add((NormalizeRelative(labelRoot, path), path));
        }

        foreach ((string Label, string AbsolutePath) file in files.OrderBy(f => f.Label, StringComparer.Ordinal))
        {
            yield return file;
        }
    }

    /// <summary>The <c>/</c>-normalized path of <paramref name="path"/> relative to <paramref name="root"/>.</summary>
    public static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
