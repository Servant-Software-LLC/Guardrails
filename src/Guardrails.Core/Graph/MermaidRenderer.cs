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
/// Shape: each task is a <c>subgraph task_&lt;id&gt;</c> container holding its check nodes
/// (preflight and guardrail leaves) DIRECTLY inside the container — no nested
/// <c>Preflights</c>/<c>Guardrails</c> wrapper subgraph, no free guardrail/preflight nodes outside
/// a container, no per-task <c>done_&lt;id&gt;</c> reconvergence node, no <c>task --&gt; guardrail</c>
/// fan-out edge. (A per-task nested-subgraph wrapper was tried and dropped — see "Nested boxes
/// dropped" below.) Two mandatory plan-level containers bracket the whole DAG: <c>plan_preflights</c>
/// ("Full Flight Checks") at the top, <c>plan_guardrails</c> ("Terminal Gate") at the bottom —
/// always emitted, even when their folder is empty, because they are structural brackets, not
/// conditional content. These two stay titled subgraphs (unaffected by the nested-box removal
/// below): they are one-off heterogeneous brackets on the whole DAG, not a per-task repeated
/// pattern.
/// </para>
/// <para>
/// <b>Nested boxes dropped (simplification).</b> A task container previously nested a
/// "Guardrails" sub-container (and, when present, a "Preflights" sub-container) around its leaf
/// check nodes — nesting-within-nesting that made a real generated diagram look busy for no
/// semantic gain: the wrapper subgraph id was never referenced by edge emission, container
/// styling, or <see cref="GraphSourceHash"/> — purely cosmetic. Leaf check nodes are now emitted
/// as direct children of the task container; the existing <c>:::preflight</c>/<c>:::guardrail</c>
/// <c>classDef</c> fill remains the only visual category distinction. Removing the boxes also
/// removes their visual "preflights run before, guardrails run after" cue, so that temporal fact
/// is now preserved two other ways: (1) a GUARANTEED emission-order contract — a task's preflight
/// leaf node(s), if any, are always emitted before its guardrail leaf node(s) within the
/// container (see <see cref="AppendTaskContainer"/>), so position is a stable, tested convention,
/// not a rendering accident; and (2) the legend (see <see cref="LegendMarkdown"/>) states the
/// before/after timing in words, not just a bare colour-category name.
/// </para>
/// <para>
/// <b>Legend lives outside the Mermaid graph.</b> A Mermaid-native legend (a disconnected
/// subgraph of dummy colour-swatch nodes) was prototyped and rendered BROKEN headless against the
/// exact bundled <c>mermaid@11.4.1</c>: dagre lays out a disconnected subgraph as a phantom extra
/// "task" overlapping the real DAG. The only approach that renders correctly is content OUTSIDE
/// the Mermaid source entirely: an HTML overlay `&lt;div&gt;` in <see cref="HtmlDiagramRenderer"/>
/// for <c>diagram.html</c>, and the shared <see cref="LegendMarkdown"/> block placed by the CLI's
/// <c>graph</c> command AFTER the fenced <c>```mermaid```</c> block for <c>diagram.md</c> (GitHub's
/// Mermaid sandbox has no overlay option). Neither destination is part of <see cref="Render"/>'s or
/// <see cref="RenderInteractive"/>'s returned Mermaid source, and — same treatment as the existing
/// cosmetic <c>classDef</c> color lines (added by those two methods, never by
/// <see cref="SemanticContent"/>) — the legend never reaches <see cref="SemanticContent"/> either,
/// so legend-only wording changes never move <see cref="GraphSourceHash"/> and never make
/// `graph --check` spuriously report a plan stale.
/// </para>
/// <para>
/// <b>Preflight labels are truncated with click-for-detail.</b> A task-level preflight's full
/// descriptive text (which can run to many words) is too long to draw as an inline node label
/// without dwarfing the rest of the diagram, so the drawn label is a short synthesized name (see
/// <see cref="PreflightShortLabel"/>) while the full <see cref="GuardrailDefinition.Description"/>
/// remains reachable via the SAME <c>click</c>-directive mechanism <see cref="RenderInteractive"/>
/// already uses for every other node (source-file click-through, issue #33) — the tooltip
/// argument of that directive carries the full text, so no new mechanism was introduced.
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
/// <b>A task container's click targets a click-only anchor node (issue #211), NOT the subgraph
/// itself.</b> The #210 edge fix above only changed how DAG EDGES attach to a container; it did
/// not change (and never claimed to change) how the container's own <c>click href</c> directive
/// resolves. Real headless-Chrome verification against the bundled <c>mermaid@11.4.1</c> — clicking
/// the container body, its title text, and its fill rect, then checking whether a real navigation
/// (a <c>window.open</c>/popup) actually fired — proved it NEVER does: Mermaid wraps a clickable
/// LEAF node in a real <c>&lt;a href&gt; </c> element (confirmed firing), but never wraps a
/// <c>&lt;g class="cluster"&gt;</c> (subgraph) in one, regardless of what id a <c>click</c>
/// directive names. This is a genuine, still-open upstream Mermaid limitation (not a regression
/// from #210, and not fixable by choosing a different id): mermaid-js/mermaid#1637 ("Let subgraph
/// handle clicks") and #5428 ("click action for subgraphs") are both open feature requests as of
/// this writing. The fix: <see cref="RenderInteractive"/> ONLY (never <see cref="Render"/>) emits
/// one invisible click-only anchor node per task container — the LAST line inside the container
/// block, so it never disturbs the preflight-before-guardrail emission-order contract — and the
/// container's <c>click</c> directive targets that anchor instead of the container id. The anchor
/// carries no edge (the #210 container→container edges are untouched) and no `class` assignment
/// beyond `:::invisible`, so it is invisible and inert except as a click target.
/// </para>
/// <para>
/// A task-level preflight still gates its producer's <c>dependsOn</c> edge: the edge remains
/// drawn container→container exactly as any other dependency edge, and the preflight renders as an
/// ordinary check node inside the consumer's own container (before its guardrail check nodes) —
/// it is never re-routed to originate from the preflight node itself.
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

    /// <summary>
    /// The legend text placed OUTSIDE the Mermaid graph (see class remarks, "Legend lives
    /// outside the Mermaid graph"). States both the colour mapping and the before/after timing —
    /// a bare category name would not preserve the ordering semantic the removed nested boxes used
    /// to convey visually. Consumed by <c>GraphCommand</c> (a plain Markdown block placed after the
    /// fenced <c>```mermaid```</c> block in <c>diagram.md</c>) and by <see cref="HtmlDiagramRenderer"/>
    /// (rendered into the HTML overlay div for <c>diagram.html</c>). Public because both consumers
    /// live outside this assembly's <c>InternalsVisibleTo</c> set (the CLI project). Deliberately NOT
    /// part of <see cref="Render"/>/<see cref="RenderInteractive"/>'s returned Mermaid source or
    /// <see cref="SemanticContent"/> — see class remarks.
    /// </summary>
    public const string LegendMarkdown =
        "**Legend**\n\n"
        + "- 🟣 **Preflight** — verified BEFORE the task's attempt loop; gates entry "
        + "(dependency-delivery precondition)\n"
        + "- 🟡 **Guardrail** — verified AFTER the task's action; must pass for the task to finish\n"
        + "- 🟢 Plan-level containers (\"Full Flight Checks\" top, \"Terminal Gate\" bottom) run the "
        + "same two checks once for the whole plan, at the very start and very end.\n";

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
        AppendNodesAndEdges(plan, sb, includeClickAnchors: false);
        AppendClassDefs(sb, includeInvisible: false);
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
    /// <remarks>
    /// <b>Task-container clicks target a click-only anchor node, not the subgraph itself
    /// (issue #211).</b> Real headless-Chrome verification against the bundled
    /// <c>mermaid@11.4.1</c> proved a <c>click</c> directive targeting a subgraph/cluster id
    /// never fires — Mermaid wraps a clickable LEAF node in a real <c>&lt;a href&gt;</c> element,
    /// but never does so for a <c>&lt;g class="cluster"&gt;</c> (subgraph); this matches Mermaid's
    /// own still-open upstream limitation (mermaid-js/mermaid#1637, #5428: "let subgraph handle
    /// clicks" / "click action for subgraphs"). So <see cref="RenderInteractive"/> emits ONE extra
    /// invisible anchor node per task container (<paramref name="includeClickAnchors"/> true, this
    /// method only) as the LAST line inside the container block, and the container's <c>click</c>
    /// directive targets that anchor instead of the container id. This is click-target-only: the
    /// #210 container→container DAG edges are UNCHANGED (still <c>subgraph --&gt; subgraph</c>,
    /// still clip to the outer border) — the anchor carries no edge. <see cref="Render"/> (and thus
    /// <c>diagram.md</c> and <see cref="SemanticContent"/>/the staleness hash) never sets this flag,
    /// so the clean container model stays exactly as issue #210 left it: no interior anchor, no
    /// <c>:::invisible</c> class, at all.
    /// </remarks>
    public static string RenderInteractive(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        AppendLf(sb, "flowchart TD");
        IReadOnlyDictionary<string, string> nodeIdBase = AppendNodesAndEdges(plan, sb, includeClickAnchors: true);
        AppendClassDefs(sb, includeInvisible: true);
        AppendClickDirectives(plan, nodeIdBase, sb);
        return sb.ToString();
    }

    /// <summary>
    /// The cosmetic <c>classDef</c> lines (colors) for the LEAF check nodes — preflight check
    /// and guardrail check — which reference them inline via <c>:::preflight</c>/<c>:::guardrail</c>.
    /// The task-container and plan-level-container fills are applied per-container by a
    /// <c>style &lt;id&gt; …</c> statement instead (see <see cref="TaskStyle"/> /
    /// <see cref="PlanLevelStyle"/>), because a <c>class</c> assignment does not reach a subgraph
    /// that is an edge endpoint in the bundled Mermaid. Shared by <see cref="Render"/> and
    /// <see cref="RenderInteractive"/>; deliberately EXCLUDED from the staleness key (see
    /// <see cref="SemanticContent"/>), which is why <see cref="SemanticContent"/> does not call it.
    /// <paramref name="includeInvisible"/> additionally emits the <c>invisible</c> classDef the
    /// click-only anchor nodes use (issue #211) — <see cref="RenderInteractive"/> only; the clean
    /// <see cref="Render"/> output never declares an anchor node, so it never needs this class.
    /// <c>fill:transparent</c> (NOT <c>fill:none</c>) is deliberate: real headless-Chrome
    /// verification proved an SVG shape with <c>fill:none</c> is invisible to hit-testing too — the
    /// browser's default <c>pointer-events: visiblePainted</c> only counts a shape as "painted" (and
    /// therefore clickable) when it has an actual fill/stroke, so a <c>fill:none</c> anchor let every
    /// click pass straight through to whatever sat underneath it and silently ate the click. A fully
    /// transparent (alpha-0) fill still paints nothing visually but DOES count as painted for
    /// hit-testing, so the anchor stays invisible AND becomes reliably clickable.
    /// </summary>
    private static void AppendClassDefs(StringBuilder sb, bool includeInvisible)
    {
        AppendLf(sb, "  classDef preflight fill:#e6d7ff,stroke:#6f42c1,color:#2e1065;");
        AppendLf(sb, "  classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;");
        if (includeInvisible)
        {
            AppendLf(sb, "  classDef invisible fill:transparent,stroke:none;");
        }
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
        AppendNodesAndEdges(plan, sb, includeClickAnchors: false);
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
    /// <param name="includeClickAnchors">
    /// When true (<see cref="RenderInteractive"/> only), each task container additionally declares
    /// one invisible click-only anchor node (issue #211) — see <see cref="AppendTaskContainer"/>.
    /// Never true for <see cref="Render"/>/<see cref="SemanticContent"/>: the clean container model
    /// (and the staleness hash) must stay exactly as issue #210 left it.
    /// </param>
    private static IReadOnlyDictionary<string, string> AppendNodesAndEdges(
        PlanDefinition plan, StringBuilder sb, bool includeClickAnchors)
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
            AppendTaskContainer(sb, task, nodeIdBase[task.Id], includeClickAnchors);
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
        AppendCheckNodes(sb, "    ", $"{containerId}", checks, checkClass, truncatePreflightLabel: false);
        AppendLf(sb, "  end");
        AppendLf(sb, $"  style {containerId} {PlanLevelStyle}");
    }

    /// <summary>
    /// Append one task container: its preflight leaf check node(s) (if any), THEN its guardrail
    /// leaf check node(s) — both DIRECTLY inside the container, with no nested
    /// <c>Preflights</c>/<c>Guardrails</c> wrapper subgraph (see class remarks, "Nested boxes
    /// dropped") — then the container's <c>style</c> fill. The DAG edge attaches to this
    /// container's own subgraph id (no interior anchor; unaffected by
    /// <paramref name="includeClickAnchor"/> — see below).
    /// </summary>
    /// <remarks>
    /// <b>Emission-order contract (load-bearing, tested):</b> preflight check nodes are ALWAYS
    /// emitted before guardrail check nodes within a container. This is not a rendering accident —
    /// with the nested boxes gone, source order is the only surviving signal that preflights run
    /// BEFORE the task's attempt loop and guardrails run AFTER it, so callers (and the legend) may
    /// rely on it as a stable convention.
    /// </remarks>
    /// <param name="includeClickAnchor">
    /// When true (<see cref="RenderInteractive"/> only, issue #211), appends one invisible
    /// <c>{containerId}_anchor[" "]:::invisible</c> node as the LAST line inside the container
    /// block — AFTER every check node, so it never disturbs the emission-order contract above.
    /// This is a CLICK TARGET ONLY: no edge ever attaches to it (the #210 container→container DAG
    /// edges are untouched). It exists because real headless-Chrome verification proved Mermaid
    /// 11.4.1 never fires a <c>click</c> directive targeting a subgraph/cluster id — clickable
    /// LEAF nodes are wrapped in a real <c>&lt;a href&gt;</c>, but a <c>&lt;g class="cluster"&gt;</c>
    /// never is (matches the still-open mermaid-js/mermaid#1637 / #5428). A leaf node's `click`
    /// still targets the leaf itself, unaffected.
    /// </param>
    private static void AppendTaskContainer(StringBuilder sb, TaskNode task, string @base, bool includeClickAnchor)
    {
        string containerId = $"task_{@base}";

        AppendLf(sb, $"  subgraph {containerId}[{Quote(task.Id)}]");

        // Preflights BEFORE guardrails — see remarks: this order is the emission-order contract
        // that now carries the "before the attempt loop" / "after the action" temporal semantic
        // the removed nested boxes used to convey visually.
        AppendCheckNodes(sb, "    ", $"{containerId}_pf", task.Preflights, "preflight", truncatePreflightLabel: true);
        AppendCheckNodes(sb, "    ", $"{containerId}_gr", task.Guardrails, "guardrail", truncatePreflightLabel: false);

        if (includeClickAnchor)
        {
            AppendLf(sb, $"    {containerId}_anchor[\" \"]:::invisible");
        }

        AppendLf(sb, "  end");
        AppendLf(sb, $"  style {containerId} {TaskStyle}");
    }

    /// <summary>
    /// Append <paramref name="checks"/> (sorted ordinal by name, for input-order independence) as
    /// <c>{nodeIdPrefix}_{ordinal}[label]:::{checkClass}</c> node lines, one per check. Drawn label
    /// is <c>Description ?? Name</c>, EXCEPT for a task-level preflight
    /// (<paramref name="truncatePreflightLabel"/> true) whose long descriptive text is truncated
    /// to a short synthesized name (see <see cref="PreflightShortLabel"/>) — the full description
    /// remains reachable via the node's <c>click</c> tooltip in <see cref="RenderInteractive"/>
    /// (issue #33's existing click mechanism, reused rather than inventing a new one).
    /// </summary>
    private static void AppendCheckNodes(
        StringBuilder sb,
        string indent,
        string nodeIdPrefix,
        IReadOnlyList<GuardrailDefinition> checks,
        string checkClass,
        bool truncatePreflightLabel)
    {
        int ordinal = 0;
        foreach (GuardrailDefinition check in checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            string full = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description!;
            string label = truncatePreflightLabel ? PreflightShortLabel(check) : full;
            AppendLf(sb, $"{indent}{nodeIdPrefix}_{ordinal}[{Quote(label)}]:::{checkClass}");
            ordinal++;
        }
    }

    /// <summary>
    /// Longest drawn label before <see cref="PreflightShortLabel"/> truncates to an ellipsis. Long
    /// enough to keep a short synthesized phrase intact, short enough that a task-level preflight
    /// node no longer dwarfs the rest of the diagram (the owner-reported "busy" symptom).
    /// </summary>
    private const int PreflightLabelMaxChars = 40;

    /// <summary>
    /// A short drawn label for a task-level preflight check node: <c>Description ?? Name</c>,
    /// truncated to <see cref="PreflightLabelMaxChars"/> characters (word-boundary where
    /// possible) with a trailing ellipsis when it would otherwise overflow. The FULL text is never
    /// lost — it remains reachable via the node's <c>click</c> tooltip (see
    /// <see cref="AppendClickDirectives"/>/<see cref="ClickTooltip"/>), which already carries the
    /// check's full description for every node kind.
    /// </summary>
    private static string PreflightShortLabel(GuardrailDefinition check)
    {
        string full = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description!;
        string collapsed = full
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (collapsed.Length <= PreflightLabelMaxChars)
        {
            return collapsed;
        }

        // Prefer breaking at the last space within budget so the truncation reads as whole words;
        // fall back to a hard cut when the first "word" alone exceeds the budget.
        int cut = collapsed.LastIndexOf(' ', Math.Min(PreflightLabelMaxChars, collapsed.Length) - 1);
        string head = cut > 0 ? collapsed[..cut] : collapsed[..PreflightLabelMaxChars];
        return head.TrimEnd() + "…";
    }

    /// <summary>
    /// Append every container→container edge: the plan-preflights container into each DAG ROOT
    /// task's container (a task with no <c>dependsOn</c>), one edge per <c>dependsOn</c>
    /// relationship between two task containers, and each DAG LEAF task's container (a task nothing
    /// depends on) into the plan-guardrails container. The edge references the container's own
    /// subgraph id, so Mermaid clips the arrow to the container's OUTER BORDER (issue #210). A
    /// task-level preflight does not change this — the gated dependency edge is drawn exactly like
    /// any other; the preflight is only ever a check node inside the consumer's own container
    /// (emitted before the container's guardrail check nodes — see <see cref="AppendTaskContainer"/>).
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
    /// exists. A task-level preflight's click tooltip carries its FULL description (not just its
    /// name) since <see cref="AppendCheckNodes"/> draws only a truncated label for it — the
    /// tooltip is the click-for-detail mechanism that keeps the full text from being lost.
    /// </summary>
    /// <remarks>
    /// <b>A task container's click targets its anchor node, not the container id (issue #211).</b>
    /// <see cref="RenderInteractive"/> always declares the container's <c>_anchor</c> node (see
    /// <see cref="AppendTaskContainer"/>'s <c>includeClickAnchor</c>), so this method can always
    /// target it — real headless-Chrome verification proved a <c>click</c> directive on the
    /// subgraph/cluster id itself never fires in the bundled Mermaid. Leaf check-node clicks are
    /// unaffected: they still target the leaf node itself, which Mermaid DOES wrap in a real
    /// clickable <c>&lt;a href&gt;</c>.
    /// </remarks>
    private static void AppendClickDirectives(
        PlanDefinition plan, IReadOnlyDictionary<string, string> nodeIdBase, StringBuilder sb)
    {
        AppendCheckClicks(plan, plan.PlanPreflights, PlanPreflightsId, sb, tooltipIsFullDescription: false);

        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string @base = nodeIdBase[task.Id];
            string containerId = $"task_{@base}";
            string taskDir = ToPlanRelative(plan.PlanDirectory, task.Directory);
            // Target the container's invisible click-only anchor (issue #211), not the container
            // (subgraph) id itself — Mermaid never fires a click directive on a cluster element.
            AppendLf(sb, $"  click {containerId}_anchor href \"{taskDir}/\" \"{ClickTooltip(task.Id)}\" _blank");

            if (task.Preflights.Count > 0)
            {
                AppendCheckClicks(plan, task.Preflights, $"{containerId}_pf", sb, tooltipIsFullDescription: true);
            }

            AppendCheckClicks(plan, task.Guardrails, $"{containerId}_gr", sb, tooltipIsFullDescription: false);
        }

        AppendCheckClicks(plan, plan.PlanGuardrails, PlanGuardrailsId, sb, tooltipIsFullDescription: false);
    }

    /// <summary>
    /// Append one <c>click</c> directive per check in <paramref name="checks"/> (sorted ordinal by
    /// name, mirroring <see cref="AppendCheckNodes"/> exactly so the ordinal-suffixed node id each
    /// click targets is the one actually emitted for that check). The tooltip is normally the
    /// check's <c>Name</c>; when <paramref name="tooltipIsFullDescription"/> is set (task-level
    /// preflights, whose drawn label is truncated by <see cref="PreflightShortLabel"/>) the
    /// tooltip carries <c>Description ?? Name</c> in full instead, so hovering/clicking the node
    /// surfaces the complete text the truncated label dropped.
    /// </summary>
    private static void AppendCheckClicks(
        PlanDefinition plan,
        IReadOnlyList<GuardrailDefinition> checks,
        string nodeIdPrefix,
        StringBuilder sb,
        bool tooltipIsFullDescription)
    {
        int ordinal = 0;
        foreach (GuardrailDefinition check in checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            string checkPath = ToPlanRelative(plan.PlanDirectory, check.Path);
            string tooltipText = tooltipIsFullDescription
                ? (string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description!)
                : check.Name;
            AppendLf(sb, $"  click {nodeIdPrefix}_{ordinal} href \"{checkPath}\" \"{ClickTooltip(tooltipText)}\" _blank");
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

    /// <summary>
    /// Make a label safe inside a double-quoted Mermaid <c>click</c> tooltip: collapse any line
    /// break to a space (a <c>click</c> directive is line-oriented, so a raw newline — now
    /// possible here since a task-level preflight's tooltip can carry its full, sometimes
    /// multi-line, description — would silently truncate or corrupt the directive) and escape
    /// embedded double quotes.
    /// </summary>
    private static string ClickTooltip(string text) =>
        text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

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
    /// click-only anchor when <see cref="RenderInteractive"/> emits one, and its preflight/guardrail
    /// check nodes) derives from this unique base, so all ids stay collision-free.
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
