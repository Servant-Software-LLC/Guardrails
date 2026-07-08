using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// Computes the plan's <c>planHash</c> (SSOT §7): a SHA-256 over <c>guardrails.json</c>
/// plus every task's <c>task.json</c>, in a stable order (task ids sorted ordinally), so
/// the same plan always hashes the same. A mismatch on resume means the manifests changed
/// since the journal was written — the harness warns loudly and continues.
///
/// <para>Deliberately NARROW — structure + config only, excluding guardrail/preflight/action bodies —
/// because its consumers (the pre-DAG <c>planPreflights</c> SKIP and the resume mismatch warning) must
/// NOT re-fire on a body-only edit. The broader <see cref="PlanDefinitionHash"/> (§7.3) covers those
/// bodies and keys the review marker instead.</para>
/// </summary>
public static class PlanHash
{
    private const string Prefix = "sha256:";

    /// <summary>Compute the <c>sha256:</c>-prefixed hash for a loaded plan.</summary>
    public static string Compute(PlanDefinition plan)
    {
        var builder = new StringBuilder();

        // guardrails.json first.
        string configPath = Path.Combine(plan.PlanDirectory, "guardrails.json");
        HashText.AppendFile(builder, "guardrails.json", configPath);

        // Then every task.json, ordered by task id for determinism.
        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(task.Directory, "task.json");
            HashText.AppendFile(builder, $"task:{task.Id}", manifestPath);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
