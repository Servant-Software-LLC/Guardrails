using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Predicate helpers for the per-guardrail <c>scope</c> field (plan 08 §4.3 / Decision 2).
/// </summary>
public static class GuardrailScopeFilter
{
    /// <summary>
    /// Returns the integration-guardrail set: all guardrails declared <c>scope:"integration"</c>.
    /// Both union re-verify points and the terminal integrationGate sink call this (SSOT §4.3).
    /// </summary>
    public static IReadOnlyList<GuardrailDefinition> IntegrationSet(
        IEnumerable<GuardrailDefinition> guardrails) =>
        guardrails
            .Where(g => string.Equals(g.Scope, "integration", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Decides whether <paramref name="guardrail"/> should re-run at a union point under the
    /// superseded three-part union model (union task's full set + every colliding sibling's full
    /// set unconditionally + the integration set).
    ///
    /// <para><b>DORMANT in v1 (#132).</b> The v1 union re-verify contract is integration-set-only
    /// (SSOT §4.3): every union point re-runs <see cref="IntegrationSet"/> and nothing else, with
    /// no per-task or per-colliding-sibling selection. This predicate has NO production callers —
    /// it is retained only as the seed of the deferred union-safe colliding-sibling re-verify
    /// design and is exercised by unit tests. Do not wire it into the scheduler without revisiting
    /// §4.3 and the #132 tracking issue.</para>
    /// </summary>
    /// <param name="guardrail">The guardrail being evaluated.</param>
    /// <param name="isCollidingSibling">
    /// True when the guardrail's task has a write-scope collision with the union task's branch.
    /// Colliding-sibling guardrails always re-run (B-3: the AI may have silently dropped the
    /// sibling's source hunk, leaving its test file untouched in the merge diff).
    /// </param>
    /// <param name="touchedByMerge">
    /// Files the merge touched that belong to the guardrail's task (pre-filtered by the caller).
    /// Ignored when scope is "integration" or <paramref name="isCollidingSibling"/> is true.
    /// </param>
    public static bool ShouldRunAtUnion(
        GuardrailDefinition guardrail,
        bool isCollidingSibling,
        IReadOnlySet<string> touchedByMerge)
    {
        // integration-scoped guardrails run at every union regardless of anything
        if (string.Equals(guardrail.Scope, "integration", StringComparison.OrdinalIgnoreCase))
            return true;

        // colliding siblings run unconditionally — B-3 split
        if (isCollidingSibling)
            return true;

        // distant, non-colliding local/null: run only if merge touched the task's files
        return touchedByMerge.Count > 0;
    }
}
