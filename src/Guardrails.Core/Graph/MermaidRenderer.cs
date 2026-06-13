using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Graph;

/// <summary>
/// Renders a plan's task/guardrail DAG as a Mermaid <c>flowchart TD</c> (SSOT §10).
/// Pure: <see cref="Render"/> maps a <see cref="PlanDefinition"/> to Mermaid text with no
/// I/O. Per task: the task node fans out an edge to each of its guardrail nodes; those
/// guardrail nodes all merge into a single per-task "Finished" node; and a dependency edge
/// runs FROM a dependency's Finished node TO the dependent task node
/// (<c>done_A --> task_B</c> for each B that <c>dependsOn</c> A). Retry / feedback edges
/// are intentionally out of scope for v1.
/// </summary>
public static class MermaidRenderer
{
    /// <summary>Render the plan as a Mermaid <c>flowchart TD</c> string (no trailing I/O).</summary>
    public static string Render(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var graph = new DependencyGraph(plan.Tasks);

        // Ordinal task order throughout (repo convention).
        List<TaskNode> tasks = plan.Tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        // --- nodes + intra-task edges -------------------------------------------------
        foreach (TaskNode task in tasks)
        {
            string taskNode = TaskNodeId(task.Id);
            string doneNode = DoneNodeId(task.Id);

            sb.AppendLine($"  {taskNode}[{Quote(task.Id)}]:::task");

            int ordinal = 0;
            foreach (GuardrailDefinition guardrail in task.Guardrails
                         .OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                string guardrailNode = GuardrailNodeId(task.Id, ordinal);
                string label = string.IsNullOrWhiteSpace(guardrail.Description)
                    ? guardrail.Name
                    : guardrail.Description!;

                sb.AppendLine($"  {guardrailNode}[{Quote(label)}]:::guardrail");
                sb.AppendLine($"  {taskNode} --> {guardrailNode}");
                sb.AppendLine($"  {guardrailNode} --> {doneNode}");
                ordinal++;
            }

            sb.AppendLine($"  {doneNode}[{Quote($"{task.Id} ✓ Finished")}]:::done");
        }

        // --- dependency edges: done_A --> task_B for each B dependsOn A ----------------
        foreach (TaskNode dependency in tasks)
        {
            foreach (string dependentId in graph.DependentsOf(dependency.Id)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {DoneNodeId(dependency.Id)} --> {TaskNodeId(dependentId)}");
            }
        }

        // --- class definitions (three colors) -----------------------------------------
        sb.AppendLine("  classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;");
        sb.AppendLine("  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;");
        sb.AppendLine("  classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;");

        return sb.ToString();
    }

    // --- node id helpers --------------------------------------------------------------

    /// <summary>Mermaid node id for a task: <c>task_&lt;sanitized-id&gt;</c>.</summary>
    private static string TaskNodeId(string taskId) => $"task_{Sanitize(taskId)}";

    /// <summary>Mermaid node id for a task's Finished node: <c>done_&lt;sanitized-id&gt;</c>.</summary>
    private static string DoneNodeId(string taskId) => $"done_{Sanitize(taskId)}";

    /// <summary>
    /// Mermaid node id for a guardrail: <c>gr_&lt;sanitized-task-id&gt;_&lt;ordinal&gt;</c>.
    /// Task ids are unique, so prefixing with the (sanitized) task id keeps guardrail node
    /// ids collision-safe even when two tasks share a guardrail name.
    /// </summary>
    private static string GuardrailNodeId(string taskId, int ordinal) =>
        $"gr_{Sanitize(taskId)}_{ordinal}";

    /// <summary>
    /// Turn an id into a safe Mermaid node-id fragment: every character that is not an ASCII
    /// letter, digit, or underscore becomes <c>_</c> (so kebab <c>-</c> → <c>_</c>). The
    /// distinct <c>task_</c>/<c>gr_</c>/<c>done_</c> prefixes keep the three node families
    /// from colliding, and task ids are themselves unique.
    /// </summary>
    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            bool safe = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
            sb.Append(safe ? c : '_');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wrap a label in double quotes for a Mermaid node, escaping embedded double quotes as
    /// <c>&amp;quot;</c> (Mermaid's HTML-entity escape) so labels carrying real ids /
    /// descriptions never break the node syntax.
    /// </summary>
    private static string Quote(string label) =>
        "\"" + label.Replace("\"", "&quot;", StringComparison.Ordinal) + "\"";
}
