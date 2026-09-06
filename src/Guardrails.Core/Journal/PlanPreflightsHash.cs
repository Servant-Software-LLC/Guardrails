using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// The behavioral hash of a plan's <c>&lt;plan&gt;/preflights/</c> folder — the SUBJECT of the pre-DAG
/// phase's resume skip (SSOT §7, issue #574).
///
/// <para>
/// <b>Why a third hash rather than reusing one of the two that exist.</b> The skip used
/// <see cref="PlanHash"/>, which covers <c>guardrails.json</c> plus every <c>task.json</c> — and covers
/// none of what the phase actually checks. That mismatch produced two opposite defects from one cause:
/// </para>
/// <list type="bullet">
///   <item>
///     the hash <b>holds</b> while the checked thing <b>changed</b> — edit the preflight that failed the
///     gate and the marker still matches, so the phase skips when it should run (#623's second claim);
///   </item>
///   <item>
///     the hash <b>moves</b> while the checked thing <b>did not</b> — any edit to a <c>task.json</c>
///     mid-run re-runs the phase, and by then the plan's own work has landed, so a baseline asserting
///     "the area was green before we started" is re-evaluated against POST-WORK bytes and halts a healthy
///     run with a message blaming pre-existing breakage (#574).
///   </item>
/// </list>
///
/// <para>
/// <see cref="PlanDefinitionHash"/> is the obvious lever and needs no new field, but it is broader than
/// the subject: it also covers action bodies and every task guardrail, so editing an unrelated task's
/// prompt would still force a needless re-run — #574's failure mode again, just rarer. Scoping the hash
/// to the folder the skip actually guards is what makes the skip mean "these checks are unchanged".
/// </para>
///
/// <para>
/// <b>Scope is the plan-root <c>preflights/</c> folder ONLY</b>, deliberately. The other two pre-DAG
/// steps — committed sample-pair verification and the openai-compat endpoint probe — run BEFORE the skip
/// unconditionally and are not gated by it, so widening this hash to cover them would re-run the Full
/// Flight Checks for a change that cannot alter their verdict.
/// </para>
/// </summary>
public static class PlanPreflightsHash
{
    private const string Prefix = "sha256:";

    /// <summary>Folder name of the plan-level full-flight checks (matches the loader).</summary>
    private const string PreflightsDirName = "preflights";

    /// <summary>
    /// Compute the <c>sha256:</c>-prefixed hash of the plan-root <c>preflights/</c> folder. A plan with no
    /// such folder hashes the empty string — stable, and never equal to a folder that has content.
    /// </summary>
    public static string Compute(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();

        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(
                     plan.PlanDirectory, Path.Combine(plan.PlanDirectory, PreflightsDirName)))
        {
            HashText.AppendFile(builder, file.Label, file.AbsolutePath);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
