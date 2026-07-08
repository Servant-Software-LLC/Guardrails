using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// Computes the plan's <c>PlanDefinitionHash</c> (SSOT §7.3, issue #260): a SHA-256 over the plan's
/// whole <b>behavioral</b> definition — everything a <c>/guardrails-review</c> pass scrutinizes,
/// including the guardrail/preflight/action <b>bodies</b> that the narrower <see cref="PlanHash"/> (§7)
/// deliberately excludes. It exists ONLY to key the review marker (<see cref="Review.ReviewMarker"/>,
/// §13); it is NOT a resume key and has no other consumers. Widening <see cref="PlanHash"/> itself would
/// false-halt its load-bearing pre-DAG-SKIP / resume consumers, which is why this is a separate hash.
///
/// <para>Inputs, in fixed order with the same discipline as <see cref="PlanHash"/> (labeled segments,
/// <c>/</c>-normalized relative paths, newline-normalized bytes, <c>sha256:</c> prefix):</para>
/// <list type="number">
///   <item><c>guardrails.json</c>.</item>
///   <item>For each task (sorted by <see cref="TaskNode.Id"/> ordinal), the SHARED per-task file set
///     from <see cref="TaskDefinitionFiles"/>: <c>task.json</c> + resolved action + <c>guardrails/**</c>
///     + <c>preflights/**</c> (recursive, catching <c>.json</c> sidecars).</item>
///   <item>Every file under <c>&lt;plan&gt;/guardrails/**</c> (terminal gate, §3.3), recursive, sorted.</item>
///   <item>Every file under <c>&lt;plan&gt;/preflights/**</c> (pre-DAG full-flight checks, §7), recursive, sorted.</item>
/// </list>
///
/// <para>Excludes <c>state/</c> (circular — the review marker it keys lives there), the generated
/// <c>diagram.md</c>/<c>diagram.html</c>, <c>guardrails.baseline</c>, <c>logs/</c>, and <c>captured/</c>
/// — none are authored behavior. The per-task file set in step 2 is the same primitive
/// <c>TaskDefinitionHash</c> (§7.2, #274) reuses, so the hashes cannot drift.</para>
/// </summary>
public static class PlanDefinitionHash
{
    private const string Prefix = "sha256:";

    /// <summary>Folder name of the plan-level terminal gate (matches the loader).</summary>
    private const string GuardrailsDirName = "guardrails";

    /// <summary>Folder name of the plan-level full-flight checks (matches the loader).</summary>
    private const string PreflightsDirName = "preflights";

    /// <summary>Compute the <c>sha256:</c>-prefixed behavioral-definition hash for a loaded plan.</summary>
    public static string Compute(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();

        // 1. guardrails.json first.
        HashText.AppendFile(builder, "guardrails.json", Path.Combine(plan.PlanDirectory, "guardrails.json"));

        // 2. Each task's shared definition file set, ordered by task id, prefixed to disambiguate.
        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            foreach ((string Label, string AbsolutePath) file in TaskDefinitionFiles.Enumerate(task))
            {
                HashText.AppendFile(builder, $"task:{task.Id}/{file.Label}", file.AbsolutePath);
            }
        }

        // 3. Plan-level terminal-gate folder, then 4. plan-level preflights folder.
        AppendFolder(builder, plan.PlanDirectory, GuardrailsDirName);
        AppendFolder(builder, plan.PlanDirectory, PreflightsDirName);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendFolder(StringBuilder builder, string planDirectory, string folderName)
    {
        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(planDirectory, Path.Combine(planDirectory, folderName)))
        {
            HashText.AppendFile(builder, file.Label, file.AbsolutePath);
        }
    }
}
