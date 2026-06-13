using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Graph;

/// <summary>
/// Computes the staleness key for a plan's rendered diagram (SSOT §10). The hash is a
/// SHA-256 over a canonical serialization of ONLY the diagram-relevant state: for each task
/// in ordinal order, its id, its <c>dependsOn</c> (sorted ordinal), its guardrail file
/// basenames (sorted ordinal), and its action kind. It is therefore stable across
/// irrelevant reorderings (e.g. task or guardrail enumeration order) and changes whenever a
/// task, a dependency, or a guardrail is added or removed — i.e. exactly when the rendered
/// diagram would differ in shape. Guardrail descriptions and label text are NOT covered:
/// they change node labels, not the DAG, and the diagram is cheap to regenerate.
/// </summary>
public static class GraphSourceHash
{
    // Unambiguous delimiters that cannot occur in task ids, dependency ids, or file names,
    // so the canonical form is injective (no two distinct plans serialize to one string).
    private const char Unit = '';   // separates fields within a task record
    private const char Record = ''; // separates one task record from the next

    /// <summary>
    /// Compute the lowercase-hex SHA-256 staleness key for <paramref name="plan"/>'s diagram.
    /// </summary>
    public static string Compute(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();

        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            sb.Append("task").Append(task.Id).Append(Unit);

            sb.Append("deps");
            sb.AppendJoin(Unit, task.DependsOn.OrderBy(d => d, StringComparer.Ordinal));
            sb.Append(Unit);

            sb.Append("guardrails");
            sb.AppendJoin(
                Unit,
                task.Guardrails
                    .Select(g => Path.GetFileName(g.Path))
                    .OrderBy(n => n, StringComparer.Ordinal));
            sb.Append(Unit);

            sb.Append("action").Append(task.Action.Kind).Append(Unit);

            sb.Append(Record);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
