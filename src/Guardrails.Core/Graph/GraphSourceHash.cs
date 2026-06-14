using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Graph;

/// <summary>
/// Computes the staleness key for a plan's rendered diagram (SSOT §10). The hash is a
/// SHA-256 over the diagram's SEMANTIC content as emitted by
/// <see cref="MermaidRenderer.SemanticContent"/> — the drawn node labels (guardrail
/// <c>Description ?? Name</c>, task ids, the per-task "Finished" labels) and the DAG shape
/// (nodes + fan-out/merge/dependency edges). Because it hashes exactly what the diagram
/// DRAWS, it changes whenever the rendered diagram would change — a task, a dependency, a
/// guardrail, or a node label — and is immune to cosmetic <c>classDef</c> styling. It is
/// stable across irrelevant input reorderings (the renderer sorts tasks, guardrails, and
/// dependents ordinal). It deliberately EXCLUDES <c>action.Kind</c>, which the diagram does
/// not draw.
/// </summary>
public static class GraphSourceHash
{
    /// <summary>
    /// Compute the lowercase-hex SHA-256 staleness key for <paramref name="plan"/>'s diagram.
    /// </summary>
    public static string Compute(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string semantic = MermaidRenderer.SemanticContent(plan);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(semantic));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
