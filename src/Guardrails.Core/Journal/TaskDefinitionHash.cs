using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// Computes a single task's <c>TaskDefinitionHash</c> (SSOT §7.2, issue #274 Part A): a SHA-256 over
/// exactly the files that define ONE task's behavior, folded over the SHARED
/// <see cref="TaskDefinitionFiles"/> enumeration (issue #260) — <c>task.json</c>, the resolved action
/// file, and every file under the task's <c>guardrails/**</c> and <c>preflights/**</c> folders
/// (recursive, incl. <c>.json</c> sidecars). Stamped at a task's successful settle (onto its journal
/// entry and its integration commit's <c>Guardrails-Task-Hash:</c> trailer) and recomputed on resume so
/// an edit to an already-<c>succeeded</c> task's definition is DETECTED and halts honestly rather than
/// silently reusing the stale cached segment (the definition-drift halt).
///
/// <para>Computed with the same discipline as <see cref="PlanHash"/> / <see cref="PlanDefinitionHash"/>
/// (labeled segments, newline-normalized bytes so CRLF/LF checkouts hash identically, deterministic
/// ordering, <c>sha256:</c> prefix) — but at TASK granularity. It REUSES <see cref="TaskDefinitionFiles"/>
/// (the #260 primitive) so a per-task hash and the whole-plan <see cref="PlanDefinitionHash"/> can never
/// drift on "what defines a task". The labels here are task-folder-relative (no <c>task:&lt;id&gt;/</c>
/// prefix), so a task's hash depends only on its OWN files' bytes and layout, not its id.</para>
/// </summary>
public static class TaskDefinitionHash
{
    private const string Prefix = "sha256:";

    /// <summary>Compute the <c>sha256:</c>-prefixed definition hash for a single loaded task.</summary>
    public static string Compute(TaskNode task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var builder = new StringBuilder();
        foreach ((string Label, string AbsolutePath) file in TaskDefinitionFiles.Enumerate(task))
        {
            HashText.AppendFile(builder, file.Label, file.AbsolutePath);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
