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
        AppendClassDefs(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Render the plan PLUS <c>click</c> directives that open each node's source under the plan
    /// folder — a task node opens its task folder, a guardrail node opens its guardrail file
    /// (issue #33). Used ONLY for the local <c>diagram.html</c> viewer:
    /// <list type="bullet">
    ///   <item>GitHub renders Mermaid in a sandboxed mode that disables clicks, so these would be
    ///         inert in <c>diagram.md</c> — and the targets are <c>file://</c>-local anyway.</item>
    ///   <item>They are deliberately NOT in <c>diagram.md</c> and NOT in the staleness hash
    ///         (<see cref="SemanticContent"/>); the targets are derived deterministically from the
    ///         same plan, as plan-relative forward-slash paths, so the output is byte-identical on
    ///         every OS (no timestamp, no absolute path).</item>
    /// </list>
    /// </summary>
    public static string RenderInteractive(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        AppendLf(sb, "flowchart TD");
        IReadOnlyDictionary<string, string> nodeIdBase = AppendNodesAndEdges(plan, sb);
        AppendClassDefs(sb);
        AppendClickDirectives(plan, nodeIdBase, sb);
        return sb.ToString();
    }

    /// <summary>
    /// The three cosmetic <c>classDef</c> lines (colors). Shared by <see cref="Render"/> and
    /// <see cref="RenderInteractive"/>; deliberately EXCLUDED from the staleness key (see
    /// <see cref="SemanticContent"/>), which is why <see cref="SemanticContent"/> does not call it.
    /// </summary>
    private static void AppendClassDefs(StringBuilder sb)
    {
        AppendLf(sb, "  classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;");
        AppendLf(sb, "  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;");
        AppendLf(sb, "  classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;");
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
    /// plan, in ordinal task order. The single shared emitter behind <see cref="Render"/>,
    /// <see cref="SemanticContent"/>, and <see cref="RenderInteractive"/>. Returns the
    /// task-id → node-id-base map so the caller can emit click directives against the SAME node
    /// ids (the emitted bytes are unchanged whether or not the caller uses the return value).
    /// </summary>
    private static IReadOnlyDictionary<string, string> AppendNodesAndEdges(PlanDefinition plan, StringBuilder sb)
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

        return nodeIdBase;
    }

    /// <summary>
    /// Append <c>click</c> directives (HTML-viewer only; see <see cref="RenderInteractive"/>):
    /// each task node opens its task FOLDER (<c>tasks/&lt;id&gt;/</c>), each guardrail node opens
    /// its guardrail FILE — both as plan-relative, forward-slash <c>file://</c> targets resolved
    /// relative to <c>diagram.html</c> at the plan root, opened in a new tab (<c>_blank</c>) so the
    /// diagram stays put. Emitted in the same ordinal/sorted order as the nodes for determinism.
    /// </summary>
    private static void AppendClickDirectives(
        PlanDefinition plan, IReadOnlyDictionary<string, string> nodeIdBase, StringBuilder sb)
    {
        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string @base = nodeIdBase[task.Id];
            string taskDir = ToPlanRelative(plan.PlanDirectory, task.Directory);
            AppendLf(sb, $"  click task_{@base} href \"{taskDir}/\" \"{ClickTooltip(task.Id)}\" _blank");

            int ordinal = 0;
            foreach (GuardrailDefinition guardrail in task.Guardrails
                         .OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                string grPath = ToPlanRelative(plan.PlanDirectory, guardrail.Path);
                AppendLf(sb, $"  click gr_{@base}_{ordinal} href \"{grPath}\" \"{ClickTooltip(guardrail.Name)}\" _blank");
                ordinal++;
            }
        }
    }

    /// <summary>
    /// A plan-folder-relative, forward-slash path for a <c>click href</c> target. Forward slashes
    /// (not OS separators) keep the generated <c>diagram.html</c> byte-identical across OSes and
    /// make the href resolve relative to <c>diagram.html</c> at the plan root.
    /// </summary>
    private static string ToPlanRelative(string planDirectory, string absolutePath) =>
        Path.GetRelativePath(planDirectory, absolutePath)
            .Replace('\\', '/')
            .Replace("\"", "%22", StringComparison.Ordinal); // " is legal in Linux filenames; URL-encode to keep the click directive parseable

    /// <summary>Make a label safe inside a double-quoted Mermaid <c>click</c> tooltip.</summary>
    private static string ClickTooltip(string text) =>
        text.Replace("\"", "&quot;", StringComparison.Ordinal);

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
