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
/// <b>Every check node's drawn label is its short, stable <c>Name</c> — never its (possibly long)
/// <c>Description</c>.</b> A prior version drew a task-level preflight's full descriptive text
/// (which can run to many words) and truncated it to fit; the owner asked instead for the same
/// short, meaningful identifier the node's own click target already opens, uniformly for every
/// check kind at both scopes (issue #222) — so no truncation heuristic is needed anywhere. The
/// full <see cref="GuardrailDefinition.Description"/> remains reachable via the SAME
/// <c>click</c>-directive mechanism <see cref="RenderInteractive"/> already uses for every node
/// (source-file click-through, issue #33) — the tooltip argument of that directive carries the
/// full text for every check, so no new mechanism was introduced.
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
/// <b>A task container's click target is a POST-RENDER SVG overlay on its title band (issue
/// #232/#233 superseded, issue #235), NOT a Mermaid-source mechanism at all.</b> The #210 edge fix
/// above only changed how DAG EDGES attach to a container; it did not change (and never claimed to
/// change) how the container's own click target resolves. Real headless-Chrome verification against
/// the bundled <c>mermaid@11.4.1</c> — clicking the container body, its title text, and its fill
/// rect, then checking whether a real navigation (a popup) actually fired — proved a Mermaid
/// <c>click</c> directive targeting a subgraph/cluster id NEVER fires: Mermaid wraps a clickable LEAF
/// node in a real <c>&lt;a href&gt;</c> element (confirmed firing), but never wraps a
/// <c>&lt;g class="cluster"&gt;</c> (subgraph) in one, regardless of what id a <c>click</c> directive
/// names. This is a genuine, still-open upstream Mermaid limitation: mermaid-js/mermaid#1637 ("Let
/// subgraph handle clicks") and #5428 ("click action for subgraphs") are both open feature requests
/// as of this writing.
/// </para>
/// <para>
/// <b>Why the first fix (#232/#233, an invisible anchor NODE) was insufficient.</b> That fix added
/// one <c>{containerId}_anchor[" "]:::invisible</c> node per container and pointed the container's
/// <c>click</c> directive at it instead of the subgraph id — which DOES fire (Mermaid wraps it in a
/// real <c>&lt;a href&gt;</c>, like any leaf node) but is USELESS in practice: dagre (Mermaid's layout
/// engine) sizes a <c>[" "]</c>-labelled node to a tiny default (~39×20px) and packs it wherever ITS
/// OWN layout algorithm decides — for a container with several guardrail leaf boxes packed
/// side-by-side, that is a thin sliver squeezed into whatever gap remains, not centered and not
/// where a user would naturally click. Measured on a real 4-guardrail task container: the anchor
/// covered 0.44% of the container's area, in a narrow strip near the container's right edge, and
/// none of 4 realistic click points (dead-center, near-title, left-margin, bottom-strip) landed on
/// it — dead-center instead landed on a leaf guardrail box's own click target and opened THAT
/// guardrail's source file instead of the task folder. Forcing the anchor wider via a padded label
/// does not fix this either (verified): dagre still packs it into its own slot rather than centering
/// or spanning it, and a content-dense container has almost no empty background region to reliably
/// overlay in the first place. This whole "shape the anchor node's content to control its size/
/// position" direction was abandoned as unfixable via the Mermaid-source anchor-node mechanism.
/// </para>
/// <para>
/// <b>The fix: an SVG overlay on the container's TITLE/LABEL ROW, injected via JavaScript AFTER
/// Mermaid's render completes.</b> Mermaid always renders a cluster (task container) as
/// <c>&lt;g class="cluster" id="..."&gt;</c> with exactly two children: a background <c>&lt;rect&gt;</c>
/// and a <c>&lt;g class="cluster-label"&gt;</c> (the title text) — the label always sits in its own
/// reserved header strip ABOVE where any leaf node begins (measured on a real container: label
/// spanned y=310.06→341.4, first leaf node did not start until y=373.7 — a genuine ~32px full-width
/// gap). This band is empty BY CONSTRUCTION regardless of how many/how large a task's checks are, so
/// it is a reliable click target no matter how content-dense the container is — unlike a dagre-packed
/// interior node. <see cref="HtmlDiagramRenderer"/>'s embedded script (never
/// <see cref="MermaidRenderer"/>/this Mermaid SOURCE, which stays exactly the container-model shape
/// issue #210 left it) computes, for every task container, a full-width band from the container's
/// bounding box down to just past the label's bottom edge, and appends a real
/// <c>&lt;a href="..." target="_blank"&gt;&lt;rect fill="transparent"&gt;&lt;/a&gt;</c> covering that
/// band as the cluster group's LAST child — appended (not inserted first), so it paints ON TOP of
/// the cluster's own background rect (the cluster's only two original children) while still living
/// in a different top-level SVG group from every leaf node's own clickable <c>&lt;a&gt;</c>, so it can
/// never occlude or be occluded by a leaf-node click target. The Mermaid source contributes NO
/// anchor node, NO <c>:::invisible</c> classDef, and no <c>click</c> directive for the container at
/// all now — the task→folder path data the overlay script needs is threaded through as a small
/// embedded JSON side-table (see <see cref="HtmlDiagramRenderer"/>), not a Mermaid <c>click</c>
/// directive. Leaf check-node clicks are completely unaffected — they still use Mermaid's native
/// <c>click href</c> mechanism, which already works.
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
        AppendNodesAndEdges(plan, sb);
        AppendClassDefs(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Render the plan PLUS <c>click</c> directives that open each CHECK node's source under the
    /// plan folder (a preflight/guardrail node opens its own file — issue #33). Used ONLY for the
    /// local <c>diagram.html</c> viewer:
    /// <list type="bullet">
    ///   <item>GitHub renders Mermaid in a sandboxed mode that disables clicks, so these would be
    ///         inert in <c>diagram.md</c> — and the targets are <c>file://</c>-local anyway.</item>
    ///   <item>They are deliberately NOT in <c>diagram.md</c> and NOT in the staleness hash
    ///         (<see cref="SemanticContent"/>); the targets are derived deterministically from the
    ///         same plan, as plan-relative forward-slash paths, so the output is byte-identical on
    ///         every OS (no timestamp, no absolute path).</item>
    /// </list>
    /// A TASK CONTAINER's click target is NOT a Mermaid <c>click</c> directive at all (see class
    /// remarks, "A task container's click target is a POST-RENDER SVG overlay..." — issue
    /// #232/#233 superseded, issue #235): <see cref="HtmlDiagramRenderer"/>'s embedded script injects
    /// a title-band overlay after Mermaid's render completes, using <see cref="TaskFolderTargets"/>
    /// for the folder path data. This method emits no anchor node, no <c>invisible</c> classDef, and
    /// no container <c>click</c> directive — only CHECK-node clicks, identical in shape to
    /// <see cref="Render"/>'s Mermaid source plus the click lines.
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
    /// The task-id → plan-relative folder path map <see cref="HtmlDiagramRenderer"/> needs to inject
    /// the title-band click overlay (see class remarks, issue #235): a task container's click target
    /// is no longer a Mermaid-source mechanism, so this is how the same path data
    /// <see cref="AppendClickDirectives"/> used to feed the removed anchor's <c>click href</c> now
    /// reaches the post-render JavaScript instead — as a small embedded JSON side-table keyed by the
    /// SAME container id (<c>task_&lt;base&gt;</c>) <see cref="AppendTaskContainer"/> emits, so the
    /// overlay script can look a cluster's target up by its own DOM id. Plan-relative, forward-slash
    /// paths (not OS separators, not absolute) for the same byte-identical-across-OSes reason as
    /// <see cref="ToPlanRelative"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TaskFolderTargets(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<TaskNode> tasks = plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, string> nodeIdBase = AllocateNodeIdBases(tasks);

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TaskNode task in tasks)
        {
            string containerId = $"task_{nodeIdBase[task.Id]}";
            targets[containerId] = ToPlanRelative(plan.PlanDirectory, task.Directory) + "/";
        }

        return targets;
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
    /// No longer emits an <c>invisible</c> classDef (issue #211's anchor-node mechanism was removed
    /// entirely — issue #235; see class remarks): the task-container click target is now a
    /// post-render SVG overlay <see cref="HtmlDiagramRenderer"/> injects via JavaScript, not a
    /// Mermaid node/class at all.
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
    /// <see cref="Render"/>, <see cref="SemanticContent"/>, and <see cref="RenderInteractive"/> —
    /// all three now emit the IDENTICAL container/node shape (issue #235 removed the interactive-only
    /// anchor node), so <see cref="Render"/> and <see cref="RenderInteractive"/> differ ONLY in
    /// whether <c>click</c> directives for CHECK nodes are appended afterward. Returns the
    /// task-id → node-id-base map so the caller can emit click directives against the SAME node ids
    /// (the emitted bytes are unchanged whether or not the caller uses the return value).
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
    /// Append one task container: its preflight leaf check node(s) (if any), THEN its guardrail
    /// leaf check node(s) — both DIRECTLY inside the container, with no nested
    /// <c>Preflights</c>/<c>Guardrails</c> wrapper subgraph (see class remarks, "Nested boxes
    /// dropped") — then the container's <c>style</c> fill. The DAG edge attaches to this
    /// container's own subgraph id (no interior anchor).
    /// </summary>
    /// <remarks>
    /// <b>Emission-order contract (load-bearing, tested):</b> preflight check nodes are ALWAYS
    /// emitted before guardrail check nodes within a container. This is not a rendering accident —
    /// with the nested boxes gone, source order is the only surviving signal that preflights run
    /// BEFORE the task's attempt loop and guardrails run AFTER it, so callers (and the legend) may
    /// rely on it as a stable convention.
    /// </remarks>
    /// <remarks>
    /// No anchor node of any kind is emitted here (issue #211's mechanism was removed entirely —
    /// issue #235; see class remarks). <see cref="Render"/> and <see cref="RenderInteractive"/> now
    /// emit byte-identical container/node shape for every task; the container's click target lives
    /// entirely in <see cref="HtmlDiagramRenderer"/>'s post-render JavaScript overlay instead.
    /// </remarks>
    private static void AppendTaskContainer(StringBuilder sb, TaskNode task, string @base)
    {
        string containerId = $"task_{@base}";

        AppendLf(sb, $"  subgraph {containerId}[{Quote(task.Id)}]");

        // Preflights BEFORE guardrails — see remarks: this order is the emission-order contract
        // that now carries the "before the attempt loop" / "after the action" temporal semantic
        // the removed nested boxes used to convey visually.
        AppendCheckNodes(sb, "    ", $"{containerId}_pf", task.Preflights, "preflight");
        AppendCheckNodes(sb, "    ", $"{containerId}_gr", task.Guardrails, "guardrail");

        AppendLf(sb, "  end");
        AppendLf(sb, $"  style {containerId} {TaskStyle}");
    }

    /// <summary>
    /// Append <paramref name="checks"/> (sorted ordinal by name, for input-order independence) as
    /// <c>{nodeIdPrefix}_{ordinal}[label]:::{checkClass}</c> node lines, one per check. Drawn label
    /// is ALWAYS <see cref="GuardrailDefinition.Name"/> — the check's own short, stable identifier
    /// (the file-derived name, e.g. <c>01-core-tests-green-excluding-target</c>), never the
    /// (possibly long) <see cref="GuardrailDefinition.Description"/>. A prior version drew the
    /// description (truncated for task-level preflights only, issue #211) — the owner asked
    /// instead for the same short, meaningful identifier every box's click target already opens,
    /// uniformly for every check kind at both scopes (issue #222). The full description remains
    /// reachable via the node's <c>click</c> tooltip in <see cref="RenderInteractive"/> (issue
    /// #33's existing click mechanism, unaffected by this change) — no truncation heuristic is
    /// needed anywhere now.
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
            AppendLf(sb, $"{indent}{nodeIdPrefix}_{ordinal}[{Quote(check.Name)}]:::{checkClass}");
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
    /// Append <c>click</c> directives (HTML-viewer only; see <see cref="RenderInteractive"/>) for
    /// every CHECK node — plan-level or task-level, preflight or guardrail — opening its own file
    /// as a plan-relative, forward-slash <c>file://</c> target resolved relative to
    /// <c>diagram.html</c> at the plan root, opened in a new tab (<c>_blank</c>) so the diagram
    /// stays put. Emitted in the same ordinal/sorted order as the nodes for determinism, and using
    /// the SAME node-id scheme as <see cref="AppendNodesAndEdges"/> so every click target actually
    /// exists. A task-level preflight's click tooltip carries its FULL description (not just its
    /// name) since <see cref="AppendCheckNodes"/> draws only a truncated label for it — the
    /// tooltip is the click-for-detail mechanism that keeps the full text from being lost.
    /// </summary>
    /// <remarks>
    /// <b>No container click directive is emitted here (issue #235).</b> A task container's click
    /// target is no longer a Mermaid mechanism at all — see class remarks, "A task container's click
    /// target is a POST-RENDER SVG overlay...". <see cref="HtmlDiagramRenderer"/> injects it via
    /// JavaScript after Mermaid's render completes, using <see cref="TaskFolderTargets"/> for the
    /// folder path data. Leaf check-node clicks are unaffected here: they still target the leaf node
    /// itself, which Mermaid DOES wrap in a real clickable <c>&lt;a href&gt;</c>.
    /// </remarks>
    private static void AppendClickDirectives(
        PlanDefinition plan, IReadOnlyDictionary<string, string> nodeIdBase, StringBuilder sb)
    {
        AppendCheckClicks(plan, plan.PlanPreflights, PlanPreflightsId, sb);

        foreach (TaskNode task in plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string @base = nodeIdBase[task.Id];
            string containerId = $"task_{@base}";

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
    /// click targets is the one actually emitted for that check). The tooltip is ALWAYS
    /// <c>Description ?? Name</c> in full, for every check kind at both scopes — since the drawn
    /// label is now always the short <c>Name</c> (issue #222), the tooltip is the one place the
    /// full description lives; there is no longer a "some checks get the full text, others don't"
    /// distinction.
    /// </summary>
    private static void AppendCheckClicks(
        PlanDefinition plan,
        IReadOnlyList<GuardrailDefinition> checks,
        string nodeIdPrefix,
        StringBuilder sb)
    {
        int ordinal = 0;
        foreach (GuardrailDefinition check in checks.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            string checkPath = ToPlanRelative(plan.PlanDirectory, check.Path);
            string tooltipText = string.IsNullOrWhiteSpace(check.Description) ? check.Name : check.Description!;
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
    /// suffix in ordinal order. Every node/container family for a task (the container itself and
    /// its preflight/guardrail check nodes) derives from this unique base, so all ids stay
    /// collision-free. Also used by <see cref="TaskFolderTargets"/> so its JSON side-table is keyed
    /// by the SAME container id this method produces for <see cref="AppendNodesAndEdges"/>.
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
