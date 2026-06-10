using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// Computes the plan's <c>planHash</c> (SSOT §7): a SHA-256 over <c>guardrails.json</c>
/// plus every task's <c>task.json</c>, in a stable order (task ids sorted ordinally), so
/// the same plan always hashes the same. A mismatch on resume means the manifests changed
/// since the journal was written — the harness warns loudly and continues.
/// </summary>
public static class PlanHash
{
    private const string Prefix = "sha256:";

    // Unit/record separators keep file labels and bodies from colliding across segments.
    private const char UnitSeparator = '';
    private const char RecordSeparator = '';

    /// <summary>Compute the <c>sha256:</c>-prefixed hash for a loaded plan.</summary>
    public static string Compute(PlanDefinition plan)
    {
        var builder = new StringBuilder();

        // guardrails.json first.
        string configPath = Path.Combine(plan.PlanDirectory, "guardrails.json");
        AppendFile(builder, "guardrails.json", configPath);

        // Then every task.json, ordered by task id for determinism.
        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(task.Directory, "task.json");
            AppendFile(builder, $"task:{task.Id}", manifestPath);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendFile(StringBuilder builder, string label, string path)
    {
        // Label the segment so reordering/renaming changes the hash, then the raw bytes
        // (normalized to \n so CRLF/LF checkouts hash identically).
        builder.Append(label).Append(UnitSeparator);
        if (File.Exists(path))
        {
            builder.Append(NormalizeNewlines(File.ReadAllText(path)));
        }
        builder.Append(RecordSeparator);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
}
