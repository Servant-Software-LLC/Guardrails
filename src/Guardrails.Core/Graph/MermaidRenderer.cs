using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Graph;

/// <summary>
/// Renders a plan's task/guardrail DAG as a Mermaid <c>flowchart TD</c> (SSOT §10) using the
/// CONTAINER model (design-of-record 09-preflight-first-class). Pure: <see cref="Render"/> maps
/// a <see cref="PlanDefinition"/> to Mermaid text with no I/O.
/// </summary>
/// <remarks>
/// <para>
/// Shape: each task is a <c>subgraph task_&lt;id&gt;</c> container holding nested
/// <c>Preflights</c> / <c>Guardrails</c> subgraphs whose individual check nodes live INSIDE the
/// container — no free guardrail/preflight nodes, no per-task <c>done_&lt;id&gt;</c>
/// reconvergence node, no <c>task --&gt; guardrail</c> fan-out edge. Two mandatory plan-level
/// containers bracket the whole DAG: <c>plan_preflights</c> ("Full Flight Checks") at the top,
/// <c>plan_guardrails</c> ("Terminal Gate") at the bottom — always emitted, even when their
/// folder is empty, because they are structural brackets, not conditional content.
/// </para>
/// <para>
/// The DAG is drawn <c>subgraph --&gt; subgraph</c>: each edge references a container's subgraph
/// id directly (<c>task_A --&gt; task_B</c>, <c>plan_preflights --&gt; task_root</c>, …), so Mermaid
/// clips the arrow to the container's OUTER BORDER like any ordinary box-to-box flowchart edge —
/// no interior anchor, no line piercing the box (issue #210). The bundled Mermaid version
/// (<c>mermaid@11.4.1</c>, CDN-pinned in <see cref="HtmlDiagramRenderer"/>) renders these
/// boundary-clipped subgraph edges faithfully; this was verified by rendering both the anchor
/// form and the direct form headless against 11.4.1 and measuring the edge endpoints (the anchor
/// form terminated ~65px INSIDE each box; the direct form lands ON the border). Container "kind"
/// styling (task / plan-level) is applied via a per-container <c>style &lt;id&gt; fill:…;</c>
/// statement rather than a <c>class &lt;id&gt; &lt;className&gt;;</c> assignment — in 11.4.1 a
/// <c>class</c> assignment does not reach a subgraph that is itself an edge endpoint (and every
/// container is one under the subgraph→subgraph model), whereas <c>style &lt;id&gt;</c> colours it
/// and also colours an EMPTY plan-level bracket (which Mermaid renders as a plain node, not a
/// cluster). Only the LEAF check nodes keep <c>classDef</c>-based colouring (<c>:::preflight</c> /
/// <c>:::guardrail</c>).
/// </para>
/// <para>
/// A task-level preflight still gates its producer's <c>dependsOn</c> edge: the edge remains
/// drawn container→container exactly as any other dependency edge, and the preflight renders as an
/// ordinary check node inside the consumer's own Preflights subgraph — it is never re-routed to
/// originate from the preflight node itself.
/// </para>
/// <para>
/// Line breaks are emitted as an explicit <c>\n</c> (never
/// <see cref="StringBuilder.AppendLine()"/>, which writes <c>Environment.NewLine</c> = CRLF
/// on Windows). This keeps <see cref="Render"/>, <see cref="SemanticContent"/>, and the
/// <c>--stdout</c> output byte-identical on every OS, so the committed <c>diagram.md</c>
/// few-shot reference never churns between a Windows and a Linux regeneration (issue #3).
/// </para>
/// </remarks>
public static class MermaidRenderer
{
    private const string PlanPreflightsId = "plan_preflights";
    private const string PlanGuardrailsId = "plan_guardrails";

    // Container fills applied per-container via a `style <id> …` statement (NOT a
    // `class <id> <className>;` statement). In Mermaid 11.4.1 a `class` assignment does NOT reach
    // a subgraph that is itself an edge endpoint — and every container is an edge endpoint under
    // the subgraph→subgraph edge model (issue #210) — whereas a `style <id>` DOES colour it. A
    // `style <id>` also colours an EMPTY plan-level bracket, which Mermaid renders as a plain node
    // rather than a cluster; that keeps the Full Flight Checks / Terminal Gate brackets on-brand
    // even when their folder is empty. Values mirror the retired `classDef task`/`classDef
    // planLevel` fills exactly, so the rendered colours are unchanged.
    private const string TaskStyle = "fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;";
    private const string PlanLevelStyle = "fill:#d4edda,stroke:#2e7d32,color:#10341a;";

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
    /// folder — a task node opens its task folder, a check node opens its guardrail/preflight
    /// file (issue #33). Used ONLY for the local <c>diagram.html</c> viewer:
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
    /// The two cosmetic <c>classDef</c> lines (colors) for the LEAF check nodes — preflight check
    /// and guardrail check — which reference them inline via <c>:::preflight</c>/<c>:::guardrail</c>.
    /// The task-container and plan-level-container fills are applied per-container by a
    /// <c>style &lt;id&gt; …</c> statement instead (see <see cref="TaskStyle"/> /
    /// <see cref="PlanLevelStyle"/>), because a <c>class</c> assignment does not reach a subgraph
    /// that is an edge endpoint in the bundled Mermaid. Shared by <see cref="Render"/> and
    /// <see cref="RenderInteractive"/>; deliberately EXCLUDED from the staleness key (see
    /// <see cref="SemanticContent"/>), which is why <see cref="SemanticContent"/> does not call it.
    /// </summary>
    private static void AppendClassDefs(StringBuilder sb)
    {
        AppendLf(sb, "  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;");
        AppendLf(sb, "  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;");
    }

    /// <summary>
    /// The SEMANTIC content of the diagram: ONLY the containers + nested checks + container edges
    /// (with their drawn labels), with NO <c>flowchart TD</c> header and NO cosmetic
    /// <c>classDef</c> color definitions. This is what <see cref="GraphSourceHash"/> hashes, so
    /// the staleness key tracks exactly what the diagram DRAWS (container membership, nested
    /// check labels, DAG shape) — including the PLAN-LEVEL <c>preflights/</c>/<c>guardrails/</c>
    /// folder checks, not just <c>tasks{}</c>, so editing a Terminal Gate check moves the hash
    /// too. Shares the single <see cref="AppendNodesAndEdges"/> emitter with <see cref="Render"/>,
    /// so the two can never drift.
    /// </summary>
    internal static string SemanticContent(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        AppendNodesAndEdges(plan, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Append the container model (the semantic content) for the plan: the plan-level Full Flight
    /// Checks container, one container per task in ordinal order, the plan-level Terminal Gate
    /// container, and finally every container→container edge. The single shared emitter behind
    /// <see cref="Render"/>, <see cref="SemanticContent"/>, and <see cref="RenderInteractive"/>.
    /// Returns the task-id → node-id-base map so the caller can emit click directives against the
    /// SAME node ids (the emitted bytes are unchanged whether or not the caller uses the return
    /// value).
    /// </summary>
    /// <remarks>
    /// ORDER MATTERS: every container is emitted (fully, including its <c>end</c>) BEFORE any
    /// container→container edge line. A <c>subgraph A --&gt; subgraph B</c> edge references the two
    /// container ids, so both subgraph blocks must already be declared; emitting all containers
    /// first keeps every edge's endpoints resolvable and the output deterministic.
    /// </remarks>
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

        // --- plan-level "Full Flight Checks" container (top, ALWAYS emitted) -----------
        AppendPlanLevelContainer(sb, PlanPreflightsId, "Full Flight Checks", plan.PlanPreflights, "preflight");

        // --- one container per task ------------------------------------------------------
        foreach (TaskNode task in tasks)
        {
            AppendTaskContainer(sb, task, nodeIdBase[task.Id]);
        }

        // --- plan-level "Terminal Gate" container (bottom, ALWAYS emitted) --------------
        AppendPlanLevelContainer(sb, PlanGuardrailsId, "Terminal Gate", plan.PlanGuardrails, "guardrail");

        // --- container→container edges (every container above is already fully declared) -----
        AppendContainerEdges(graph, tasks, nodeIdBase, sb);

        return nodeIdBase;
    }

    /// <summary>
    /// Append a plan-level bracket container (<c>plan_preflights</c> or <c>plan_guardrails</c>):
    /// its checks (sorted ordinal by name) as small boxes inside, styled with
    /// <paramref name="checkClass"/>. ALWAYS emitted, even when <paramref name="checks"/> is empty
    /// — the two plan-level containers are structural brackets on the whole DAG, not conditional
    /// content. The DAG edge attaches to this container's own subgraph id (no interior anchor).
    /// </summary>
    private static void AppendPlanLevelContainer(
        StringBuilder sb,
        string containerId,
        string label,
        IReadOnlyList<GuardrailDefinition> checks,
        string checkClass)
    {
        AppendLf(sb, $"  subgraph {containerId}[{Quote(label)}]");
        AppendCheckNodes(sb, "    ", $"{containerId}", checks, checkClass);
        AppendLf(sb, "  end");
        AppendLf(sb, $"  style {containerId} {PlanLevelStyle}");
    }

    /// <summary>
    /// Append one task container: a nested <c>Preflights</c> subgraph ONLY when the task declares
    /// task-level preflights (the common case has none, and an always-empty box would just be
    /// clutter — mirrors the owner's ASCII mock, which omits the Preflights section for a task with
    /// none), a nested <c>Guardrails</c> subgraph (always present — every task carries at least one
    /// guardrail, enforced at load time), then the container's <c>style</c> fill. The DAG edge
    /// attaches to this container's own subgraph id (no interior anchor).
    /// </summary>
    private static void AppendTaskContainer(StringBuilder sb, TaskNode task, string @base)
    {
        string containerId = $"task_{@base}";

        AppendLf(sb, $"  subgraph {containerId}[{Quote(task.Id)}]");

        if (task.Preflights.Count > 0)
        {
            AppendNestedCheckSubgraph(sb, $"{containerId}_preflights", "Preflights", $"{containerId}_pf", task.Preflights, "preflight");
        }

        AppendNestedCheckSubgraph(sb, $"{containerId}_guardrails", "Guardrails", $"{containerId}_gr", task.Guardrails, "guardrail");

        AppendLf(sb, "  end");
        AppendLf(sb, $"  style {containerId} {TaskStyle}");
    }

    /// <summary>Append one nested (Preflights/Guardrails) subgraph and its check nodes.</summary>
    private static void AppendNestedCheckSubgraph(
        StringBuilder sb,
        string subgraphId,
        string label,
        string nodeIdPrefix,
        IReadOnlyList<GuardrailDefinition> checks,
        string checkClass)
    {
        AppendLf(sb, $"    subgraph {subgraphId}[{Quote(label)}]");
        AppendCheckNodes(sb, "      ", nodeIdPrefix, checks, checkClass);
        AppendLf(sb, "    end");
    }

    /// <summary>
    /// Append <paramref name="checks"/> (sorted ordinal by name, for input-order independence) as
    /// <c>{nodeIdPrefix}_{ordinal}[label]:::{checkClass}</c> node lines, one per check, drawing
    /// <c>Description ?? Name</c> exactly as the prior flat-node renderer did.
    /// </summary>
    private static void AppendCheckNodes(
        StringBuilder sb,
        string indent,
        string nodeIdPrefix,
        IReadOnlyList<GuardrailDefinition> checks,
        string checkClass)
    {
        int ordinal = 0;
        foreach (GuardrailDefinition check in checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            string label = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description!;
            AppendLf(sb, $"{indent}{nodeIdPrefix}_{ordinal}[{Quote(label)}]:::{checkClass}");
            ordinal++;
        }
    }

    /// <summary>
    /// Append every container→container edge: the plan-preflights container into each DAG ROOT
    /// task's container (a task with no <c>dependsOn</c>), one edge per <c>dependsOn</c>
    /// relationship between two task containers, and each DAG LEAF task's container (a task nothing
    /// depends on) into the plan-guardrails container. The edge references the container's own
    /// subgraph id, so Mermaid clips the arrow to the container's OUTER BORDER (issue #210). A
    /// task-level preflight does not change this — the gated dependency edge is drawn exactly like
    /// any other; the preflight is only ever a check node inside the consumer's own Preflights
    /// subgraph.
    /// </summary>
    private static void AppendContainerEdges(
        DependencyGraph graph,
        IReadOnlyList<TaskNode> tasks,
        IReadOnlyDictionary<string, string> nodeIdBase,
        StringBuilder sb)
    {
        foreach (TaskNode root in tasks.Where(t => t.DependsOn.Count == 0))
        {
            AppendLf(sb, $"  {PlanPreflightsId} --> task_{nodeIdBase[root.Id]}");
        }

        foreach (TaskNode dependency in tasks)
        {
            foreach (string dependentId in graph.DependentsOf(dependency.Id)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                AppendLf(sb, $"  task_{nodeIdBase[dependency.Id]} --> task_{nodeIdBase[dependentId]}");
            }
        }

        foreach (TaskNode leaf in tasks.Where(t => graph.DependentsOf(t.Id).Count == 0))
        {
            AppendLf(sb, $"  task_{nodeIdBase[leaf.Id]} --> {PlanGuardrailsId}");
        }
    }

    /// <summary>
    /// Append <c>click</c> directives (HTML-viewer only; see <see cref="RenderInteractive"/>):
    /// each task container opens its task FOLDER (<c>tasks/&lt;id&gt;/</c>), each check node
    /// (plan-level or task-level, preflight or guardrail) opens its own file — all as
    /// plan-relative, forward-slash <c>file://</c> targets resolved relative to
    /// <c>diagram.html</c> at the plan root, opened in a new tab (<c>_blank</c>) so the diagram
    /// stays put. Emitted in the same ordinal/sorted order as the nodes for determinism, and using
    /// the SAME node-id scheme as <see cref="AppendNodesAndEdges"/> so every click target actually
    /// exists.
    /// </summary>
    private static void AppendClickDirectives(
        PlanDefinition plan, IReadOnlyDictionary<string, string> nodeIdBase, StringBuilder sb)
    {
        AppendCheckClicks(plan, plan.PlanPreflights, PlanPreflightsId, sb);

        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string @base = nodeIdBase[task.Id];
            string containerId = $"task_{@base}";
            string taskDir = ToPlanRelative(plan.PlanDirectory, task.Directory);
            AppendLf(sb, $"  click {containerId} href \"{taskDir}/\" \"{ClickTooltip(task.Id)}\" _blank");

            if (task.Preflights.Count > 0)
            {
                AppendCheckClicks(plan, task.Preflights, $"{containerId}_pf", sb);
            }

            AppendCheckClicks(plan, task.Guardrails, $"{containerId}_gr", sb);
        }

        AppendCheckClicks(plan, plan.PlanGuardrails, PlanGuardrailsId, sb);
    }

    /// <summary>
    /// Append one <c>click</c> directive per check in <paramref name="checks"/> (sorted ordinal by
    /// name, mirroring <see cref="AppendCheckNodes"/> exactly so the ordinal-suffixed node id each
    /// click targets is the one actually emitted for that check).
    /// </summary>
    private static void AppendCheckClicks(
        PlanDefinition plan, IReadOnlyList<GuardrailDefinition> checks, string nodeIdPrefix, StringBuilder sb)
    {
        int ordinal = 0;
        foreach (GuardrailDefinition check in checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            string checkPath = ToPlanRelative(plan.PlanDirectory, check.Path);
            AppendLf(sb, $"  click {nodeIdPrefix}_{ordinal} href \"{checkPath}\" \"{ClickTooltip(check.Name)}\" _blank");
            ordinal++;
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
    /// suffix in ordinal order. Every node/container family for a task (the container itself, its
    /// anchor, its nested Preflights/Guardrails subgraphs and their check nodes) derives from this
    /// unique base, so all ids stay collision-free.
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
