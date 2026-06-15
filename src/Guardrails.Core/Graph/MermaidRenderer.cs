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
/// <remarks>
/// Line breaks are emitted as an explicit <c>\n</c> (never
/// <see cref="StringBuilder.AppendLine()"/>, which writes <c>Environment.NewLine</c> = CRLF
/// on Windows). This keeps <see cref="Render"/>, <see cref="SemanticContent"/>, and the
/// <c>--stdout</c> output byte-identical on every OS, so the committed <c>diagram.md</c>
/// few-shot reference never churns between a Windows and a Linux regeneration (issue #3).
/// </remarks>
public static class MermaidRenderer
{
    /// <summary>Render the plan as a Mermaid <c>flowchart TD</c> string (no trailing I/O).</summary>
    public static string Render(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        AppendLf(sb, "flowchart TD");

        AppendNodesAndEdges(plan, sb);

        // --- class definitions (three colors) -----------------------------------------
        // Cosmetic only: deliberately EXCLUDED from the staleness key (see SemanticContent).
        AppendLf(sb, "  classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;");
        AppendLf(sb, "  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;");
        AppendLf(sb, "  classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;");

        return sb.ToString();
    }

    /// <summary>
    /// The SEMANTIC content of the diagram: ONLY the nodes + edges (with their drawn labels),
    /// with NO <c>flowchart TD</c> header and NO cosmetic <c>classDef</c> lines. This is what
    /// <see cref="GraphSourceHash"/> hashes, so the staleness key tracks exactly what the
    /// diagram DRAWS (node labels and DAG shape) and is immune to styling changes. Shares the
    /// single <see cref="AppendNodesAndEdges"/> emitter with <see cref="Render"/>, so the two
    /// can never drift.
    /// </summary>
    internal static string SemanticContent(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        AppendNodesAndEdges(plan, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Append the nodes + intra-task edges + dependency edges (the semantic content) for the
    /// plan, in ordinal task order. The single shared emitter behind both <see cref="Render"/>
    /// and <see cref="SemanticContent"/>.
    /// </summary>
    private static void AppendNodesAndEdges(PlanDefinition plan, StringBuilder sb)
    {
        var graph = new DependencyGraph(plan.Tasks);

        // Ordinal task order throughout (repo convention).
        List<TaskNode> tasks = plan.Tasks
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        // Allocate an injective node-id base per task ONCE, in ordinal order: a task whose
        // sanitized id collides with an earlier one gets a deterministic _2, _3, … suffix.
        IReadOnlyDictionary<string, string> nodeIdBase = AllocateNodeIdBases(tasks);

        // --- nodes + intra-task edges -------------------------------------------------
        foreach (TaskNode task in tasks)
        {
            string @base = nodeIdBase[task.Id];
            string taskNode = $"task_{@base}";
            string doneNode = $"done_{@base}";

            AppendLf(sb, $"  {taskNode}[{Quote(task.Id)}]:::task");

            int ordinal = 0;
            foreach (GuardrailDefinition guardrail in task.Guardrails
                         .OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                string guardrailNode = $"gr_{@base}_{ordinal}";
                string label = string.IsNullOrWhiteSpace(guardrail.Description)
                    ? guardrail.Name
                    : guardrail.Description!;

                AppendLf(sb, $"  {guardrailNode}[{Quote(label)}]:::guardrail");
                AppendLf(sb, $"  {taskNode} --> {guardrailNode}");
                AppendLf(sb, $"  {guardrailNode} --> {doneNode}");
                ordinal++;
            }

            AppendLf(sb, $"  {doneNode}[{Quote($"{task.Id} ✓ Finished")}]:::done");
        }

        // --- dependency edges: done_A --> task_B for each B dependsOn A ----------------
        foreach (TaskNode dependency in tasks)
        {
            foreach (string dependentId in graph.DependentsOf(dependency.Id)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                AppendLf(sb, $"  done_{nodeIdBase[dependency.Id]} --> task_{nodeIdBase[dependentId]}");
            }
        }
    }

    /// <summary>
    /// Append <paramref name="line"/> followed by an explicit <c>'\n'</c>. Used everywhere a
    /// line is emitted INSTEAD of <see cref="StringBuilder.AppendLine(string)"/>, whose
    /// <c>Environment.NewLine</c> would inject CRLF on Windows and make the rendered diagram
    /// (and its source hash) platform-dependent (issue #3).
    /// </summary>
    private static void AppendLf(StringBuilder sb, string line) => sb.Append(line).Append('\n');

    // --- node id helpers --------------------------------------------------------------

    /// <summary>
    /// Build the task-id → node-id-base map for <paramref name="tasks"/> (already ordinal),
    /// guaranteeing distinct bases even when two task ids <see cref="Sanitize"/> to the same
    /// string (e.g. <c>a.b</c> and <c>a_b</c> both → <c>a_b</c>). The first claimant keeps the
    /// readable sanitized base; later collisions get a deterministic <c>_2</c>, <c>_3</c>, …
    /// suffix in ordinal order. Every node family for a task (<c>task_</c>/<c>gr_</c>/
    /// <c>done_</c>) derives from this unique base, so all node ids stay collision-free.
    /// </summary>
    private static IReadOnlyDictionary<string, string> AllocateNodeIdBases(IReadOnlyList<TaskNode> tasks)
    {
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (TaskNode task in tasks)
        {
            string candidate = Sanitize(task.Id);
            if (!taken.Add(candidate))
            {
                int suffix = 2;
                string disambiguated;
                do
                {
                    disambiguated = $"{candidate}_{suffix}";
                    suffix++;
                }
                while (!taken.Add(disambiguated));

                candidate = disambiguated;
            }

            bases[task.Id] = candidate;
        }

        return bases;
    }

    /// <summary>
    /// Turn an id into a safe Mermaid node-id fragment: every character that is not an ASCII
    /// letter, digit, or underscore becomes <c>_</c> (so kebab <c>-</c> → <c>_</c>). Because
    /// this is many-to-one (e.g. <c>a.b</c> and <c>a_b</c> both map to <c>a_b</c>),
    /// <see cref="AllocateNodeIdBases"/> deduplicates the results so node ids stay injective.
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
    /// Wrap a label in double quotes for a Mermaid node and make free-text descriptions safe.
    /// Mermaid flowchart syntax is line-oriented and renders labels as HTML, so a raw label
    /// can break the WHOLE diagram. This (1) collapses every line break (<c>\r\n</c>, <c>\r</c>,
    /// <c>\n</c>) to a single space, then (2) HTML-escapes in an order that never double-escapes
    /// entities: <c>&amp;</c>→<c>&amp;amp;</c>, <c>&lt;</c>→<c>&amp;lt;</c>, <c>&gt;</c>→
    /// <c>&amp;gt;</c>, <c>"</c>→<c>&amp;quot;</c>, <c>#</c>→<c>&amp;#35;</c> (<c>&lt;</c>/<c>&gt;</c>
    /// would otherwise silently drop text as stray tags; <c>#</c> can trigger entity parsing).
    /// </summary>
    private static string Quote(string label)
    {
        string collapsed = label
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        string escaped = collapsed
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("#", "&#35;", StringComparison.Ordinal);

        return "\"" + escaped + "\"";
    }
}
