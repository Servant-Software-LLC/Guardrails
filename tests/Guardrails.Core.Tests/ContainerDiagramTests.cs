using Guardrails.Core.Graph;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Goldens for the container-model diagram (SSOT §10, design-of-record 09-preflight-first-class),
/// including the issue #210 fix (edges clip to container borders) and the nested-box removal
/// simplification. Every test drives the REAL <see cref="MermaidRenderer.Render"/> /
/// <see cref="GraphSourceHash.Compute"/> over a plan the four-folder loader understands: an
/// on-disk fixture with plan-level <c>&lt;plan&gt;/preflights/</c> (Full Flight Checks) +
/// <c>&lt;plan&gt;/guardrails/</c> (Terminal Gate) AND a task-level <c>tasks/&lt;id&gt;/preflights/</c>.
/// The container model is:
///   • one <c>subgraph task_&lt;id&gt;</c> per task, holding its preflight/guardrail check nodes
///     DIRECTLY inside the container — NO nested <c>Preflights</c>/<c>Guardrails</c> wrapper
///     subgraph (dropped: purely cosmetic, never referenced by edge emission, styling, or hashing) —
///     with preflight node(s), if any, ALWAYS emitted before guardrail node(s) (the emission-order
///     contract that now carries the "preflights run before, guardrails run after" temporal
///     semantic the removed boxes used to convey visually);
///   • two plan-level subgraphs — <c>plan_preflights</c> ("Full Flight Checks") at the TOP and
///     <c>plan_guardrails</c> ("Terminal Gate") at the BOTTOM — UNCHANGED by the nested-box removal
///     (one-off heterogeneous brackets, not a per-task repeated pattern);
///   • the DAG drawn <c>subgraph --&gt; subgraph</c> (container→container) so each edge clips to the
///     container's OUTER BORDER (<c>task_A --&gt; task_B</c> per <c>dependsOn</c>; plan_preflights →
///     root-task containers; leaf-task containers → plan_guardrails) — NO interior anchor nodes;
///   • NO <c>done_</c> reconvergence node and NO <c>task --&gt; guardrail</c> fan-out edge;
///   • a <c>source-sha256</c> that folds the PLAN-LEVEL folder checks, not just <c>tasks{}</c>.
/// </summary>
public sealed class ContainerDiagramTests : IDisposable
{
    private readonly string _tempRoot;

    public ContainerDiagramTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-containerdiagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // =====================================================================================
    // Behavior 1 — container structure: task-container subgraphs with nested Preflights /
    // Guardrails holding the check nodes INSIDE, plus the two plan-level subgraphs.
    // =====================================================================================

    [Fact]
    public void Render_EmitsATaskContainerSubgraphPerTask()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // Each task is a container: `subgraph task_<id>` (not a bare `task_<id>[...]` node).
        Assert.Contains(lines, l => IsTaskContainerHeader(l, "01_root"));
        Assert.Contains(lines, l => IsTaskContainerHeader(l, "02_leaf"));
    }

    [Fact]
    public void Render_TaskContainer_HoldsPreflightAndGuardrailChecksDirectlyInside_NoNestedWrapperSubgraphs()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // 02-leaf has BOTH a task-level preflight and a guardrail; both check nodes are drawn
        // DIRECTLY inside its container (their labels appear in the block) — there is NO nested
        // "Preflights"/"Guardrails" wrapper subgraph (dropped as a simplification: the wrapper was
        // purely cosmetic, never referenced by edge emission, container styling, or the source hash).
        IReadOnlyList<string> leaf = ContainerBlock(lines, l => IsTaskContainerHeader(l, "02_leaf"));
        Assert.NotEmpty(leaf); // the container itself must exist

        Assert.DoesNotContain(leaf, l => IsSubgraphLabelled(l, "Preflights"));
        Assert.DoesNotContain(leaf, l => IsSubgraphLabelled(l, "Guardrails"));
        // Drawn label is always the check's Name (issue #222), not its description.
        Assert.Contains(leaf, l => l.Contains("01-dep-delivered", StringComparison.Ordinal));
        Assert.Contains(leaf, l => l.Contains("01-verify", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_TaskContainer_EmitsPreflightCheckNodeBeforeGuardrailCheckNode()
    {
        // The emission-order contract (SSOT §10): with the nested boxes gone, source order is the
        // only surviving signal that preflights run BEFORE the task's attempt loop and guardrails
        // run AFTER it.
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));
        IReadOnlyList<string> leaf = ContainerBlock(lines, l => IsTaskContainerHeader(l, "02_leaf"));

        int preflightIndex = IndexOfLineContaining(leaf, "01-dep-delivered");
        int guardrailIndex = IndexOfLineContaining(leaf, "01-verify");

        Assert.True(preflightIndex >= 0 && guardrailIndex >= 0);
        Assert.True(preflightIndex < guardrailIndex,
            "the preflight check node must be emitted before the guardrail check node within the same task container");
    }

    [Fact]
    public void Render_EmitsTwoPlanLevelSubgraphs_FullFlightChecksAtTop_TerminalGateAtBottom()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        Assert.Contains(lines, l => l.StartsWith("subgraph plan_preflights", StringComparison.Ordinal)
                                    && l.Contains("Full Flight Checks", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("subgraph plan_guardrails", StringComparison.Ordinal)
                                    && l.Contains("Terminal Gate", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_PlanLevelChecks_AreDrawnAsNodesInsideTheirPlanSubgraphs()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        IReadOnlyList<string> planPreflights =
            ContainerBlock(lines, l => l.StartsWith("subgraph plan_preflights", StringComparison.Ordinal));
        IReadOnlyList<string> planGuardrails =
            ContainerBlock(lines, l => l.StartsWith("subgraph plan_guardrails", StringComparison.Ordinal));

        // Drawn label is always the check's Name (issue #222), not its description.
        Assert.Contains(planPreflights, l => l.Contains("01-baseline", StringComparison.Ordinal));
        Assert.Contains(planGuardrails, l => l.Contains("01-terminal", StringComparison.Ordinal));
    }

    // =====================================================================================
    // Behavior 2 — no interior anchors; container→container edges clipping to the border (#210).
    // =====================================================================================

    [Fact]
    public void Render_EmitsNoInvisibleAnchors_AndStylesEachContainerById()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // Issue #210: the interior invisible anchors and their classDef are gone entirely.
        Assert.DoesNotContain(lines, l => l.Contains("_anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(":::invisible", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("classDef invisible", StringComparison.Ordinal));

        // Each container is coloured by a `style <id> …` statement (a `class` assignment does not
        // reach an edge-endpoint subgraph in the bundled Mermaid).
        Assert.Contains(lines, l => l.StartsWith("style task_01_root fill:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style task_02_leaf fill:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style plan_preflights fill:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("style plan_guardrails fill:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_DrawsDependencyEdgesContainerToContainer_NotDoneToTask()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // 02-leaf dependsOn 01-root → the DAG edge is drawn container→container between the two
        // containers (clipping to their borders), not anchor→anchor.
        Assert.Contains("task_01_root --> task_02_leaf", lines);
        // and the retired old-model dependency edge (done_A --> task_B) must be gone.
        Assert.DoesNotContain(lines, l => l.StartsWith("done_", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_PlanPreflightsFeedsRootTaskContainers_AndLeafContainersFeedPlanGuardrails()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // Full Flight Checks sit at the TOP: the plan_preflights container points into the root task container.
        Assert.Contains("plan_preflights --> task_01_root", lines);
        // Terminal Gate sits at the BOTTOM: the leaf task container points into the plan_guardrails container.
        Assert.Contains("task_02_leaf --> plan_guardrails", lines);
    }

    [Fact]
    public void Render_TaskLevelPreflight_KeepsGatedDependsOnEdge_AndDrawsPreflightCheckInsideConsumer()
    {
        // 02-leaf dependsOn 01-root AND declares a task-level preflight. The gated dependency edge is STILL
        // drawn container→container, NOT re-routed to originate from the preflight node; and the preflight
        // renders as a check node directly inside 02-leaf's container (no nested Preflights subgraph).
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        Assert.Contains("task_01_root --> task_02_leaf", lines);

        IReadOnlyList<string> leaf = ContainerBlock(lines, l => IsTaskContainerHeader(l, "02_leaf"));
        Assert.DoesNotContain(leaf, l => IsSubgraphLabelled(l, "Preflights"));
        Assert.Contains(leaf, l => l.Contains("01-dep-delivered", StringComparison.Ordinal));
    }

    // =====================================================================================
    // Behavior 3 — the OLD model is ABSENT.
    // =====================================================================================

    [Fact]
    public void Render_ContainsNoDoneNode()
    {
        string render = Render(WriteCanonicalPlan());

        // The old model's per-task reconvergence node (done_<id>) is retired.
        Assert.DoesNotContain("done_", render);
    }

    [Fact]
    public void Render_ContainsNoTaskToGuardrailFanOutEdge()
    {
        IReadOnlyList<string> lines = Lines(Render(WriteCanonicalPlan()));

        // The old model fanned each task node out to its guardrail nodes (task_<id> --> gr_<id>_<n>);
        // in the container model the checks live INSIDE the container, so that edge is gone.
        Assert.DoesNotContain(lines, l => l.StartsWith("task_", StringComparison.Ordinal)
                                          && l.Contains(" --> gr_", StringComparison.Ordinal));
    }

    // =====================================================================================
    // Behavior 4 — determinism: byte-identical re-render on unchanged input (stable ordinal order).
    // =====================================================================================

    [Fact]
    public void Render_IsByteIdenticalOnReRender_OfTheContainerModel()
    {
        PlanDefinition plan = LoadPlan(WriteCanonicalPlan());

        string first = MermaidRenderer.Render(plan);
        string second = MermaidRenderer.Render(plan);
        Assert.Equal(first, second); // byte-identical re-render on unchanged input

        // and the deterministic output is the container model, not the retired done_/fan-out model.
        Assert.Contains(Lines(first), l => l.StartsWith("subgraph task_", StringComparison.Ordinal));
        Assert.DoesNotContain("done_", first);
    }

    // =====================================================================================
    // Behavior 5 (CRITICAL) — staleness: editing a PLAN-LEVEL folder check moves source-sha256, so
    // `guardrails graph --check` reports stale (exit 2). Core.Tests cannot reference the CLI, so we
    // drive the load-bearing decision directly (see GraphCheckReportsStale): the exit-2 signal is
    // exactly "the recomputed source-sha256 differs from the one embedded in diagram.md".
    // =====================================================================================

    [Fact]
    public void GraphCheck_RenamingPlanLevelGuardrailCheck_ReportsStale()
    {
        // Renaming a <plan>/guardrails/ (plan-level Terminal Gate) check's file changes its Name —
        // the DRAWN label (issue #222: always Name, never description) — so source-sha256 must
        // change, proving the hash folds the PLAN-LEVEL folder checks, not just tasks{}.
        string planDir = WriteCanonicalPlan();
        string storedHash = GraphSourceHash.Compute(LoadPlan(planDir)); // what diagram.md would embed

        File.Delete(Path.Combine(PlanGuardrailsDir(planDir), "01-terminal.ps1"));
        File.Delete(Path.Combine(PlanGuardrailsDir(planDir), "01-terminal.json"));
        WriteCheckFile(PlanGuardrailsDir(planDir), "01-terminal-revised", "PLAN GUARDRAIL TERMINAL", scope: "integration");

        string freshHash = GraphSourceHash.Compute(LoadPlan(planDir));

        Assert.True(GraphCheckReportsStale(storedHash, freshHash),
            "renaming a <plan>/guardrails/ check must move source-sha256 so `graph --check` exits 2 (stale); "
            + "the current renderer folds only tasks{}, leaving the plan-level Terminal Gate check invisible to the hash");
    }

    [Fact]
    public void SourceHash_AddingPlanLevelGuardrailCheck_ChangesHash_FoldingTheTerminalGateFolder()
    {
        // A membership change in the Terminal Gate folder (a second check node) must move the hash,
        // independently of any label logic. Current renderer ignores <plan>/guardrails/ → no change (red).
        string planDir = WriteCanonicalPlan();
        string before = GraphSourceHash.Compute(LoadPlan(planDir));

        WriteCheckFile(PlanGuardrailsDir(planDir), "02-extra-gate", "PLAN GUARDRAIL EXTRA", scope: "integration");

        string after = GraphSourceHash.Compute(LoadPlan(planDir));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SourceHash_RenamingPlanLevelPreflightCheck_ChangesHash_FoldingTheFullFlightFolder()
    {
        // The Full Flight Checks folder (<plan>/preflights/) is folded too: renaming a plan-level
        // preflight changes its Name — the drawn label (issue #222) — which moves the hash.
        string planDir = WriteCanonicalPlan();
        string before = GraphSourceHash.Compute(LoadPlan(planDir));

        File.Delete(Path.Combine(PlanPreflightsDir(planDir), "01-baseline.ps1"));
        File.Delete(Path.Combine(PlanPreflightsDir(planDir), "01-baseline.json"));
        WriteCheckFile(PlanPreflightsDir(planDir), "01-baseline-revised", "PLAN PREFLIGHT BASELINE", scope: null);

        string after = GraphSourceHash.Compute(LoadPlan(planDir));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SourceHash_Unchanged_WhenOnlyPlanLevelPreflightDescriptionChanges()
    {
        // Complementary to the rename test above: a description-only edit (same Name, same file)
        // must NOT move the hash — the drawn label is always Name (issue #222), never description,
        // so a description can be freely rewritten without `graph --check` reporting the plan stale.
        string planDir = WriteCanonicalPlan();
        string before = GraphSourceHash.Compute(LoadPlan(planDir));

        File.WriteAllText(Path.Combine(PlanPreflightsDir(planDir), "01-baseline.json"),
            "{ \"description\": \"PLAN PREFLIGHT BASELINE (revised)\" }");

        string after = GraphSourceHash.Compute(LoadPlan(planDir));
        Assert.Equal(before, after);
    }

    // =====================================================================================
    // Helpers — fixtures, rendering, and line/block inspection.
    // =====================================================================================

    /// <summary>
    /// Mirror of <c>GraphCommand.Check</c> (SSOT §10): <c>graph --check</c> recomputes source-sha256 and
    /// exits 2 ("stale") when it differs from the hash embedded in <c>diagram.md</c>. Core.Tests cannot
    /// reference the CLI project, so the load-bearing decision is exercised directly here.
    /// </summary>
    private static bool GraphCheckReportsStale(string storedHash, string freshHash) =>
        !string.Equals(storedHash, freshHash, StringComparison.Ordinal);

    /// <summary>
    /// Write the canonical four-folder fixture plan and return its folder:
    ///   tasks/01-root  — a DAG ROOT (guardrail drawn label "ROOT GUARDRAIL");
    ///   tasks/02-leaf  — dependsOn 01-root, a DAG LEAF (guardrail "LEAF GUARDRAIL",
    ///                    task-level preflight "LEAF PREFLIGHT DEP");
    ///   &lt;plan&gt;/preflights/ — plan-level Full Flight Check "PLAN PREFLIGHT BASELINE";
    ///   &lt;plan&gt;/guardrails/ — plan-level Terminal Gate "PLAN GUARDRAIL TERMINAL" (scope integration).
    /// </summary>
    private string WriteCanonicalPlan(string name = "canonical")
    {
        string planDir = NewPlan(name);

        WriteTaskFolder(planDir, "01-root", dependsOn: [],
            guardrails: [("01-verify", "ROOT GUARDRAIL")], preflights: []);
        WriteTaskFolder(planDir, "02-leaf", dependsOn: ["01-root"],
            guardrails: [("01-verify", "LEAF GUARDRAIL")], preflights: [("01-dep-delivered", "LEAF PREFLIGHT DEP")]);

        WriteCheckFile(PlanPreflightsDir(planDir), "01-baseline", "PLAN PREFLIGHT BASELINE", scope: null);
        WriteCheckFile(PlanGuardrailsDir(planDir), "01-terminal", "PLAN GUARDRAIL TERMINAL", scope: "integration");

        return planDir;
    }

    private string NewPlan(string name)
    {
        string planDir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{ \"version\": 1, \"maxParallelism\": 1 }");
        return planDir;
    }

    private static void WriteTaskFolder(
        string planDir,
        string id,
        string[] dependsOn,
        (string Name, string? Description)[] guardrails,
        (string Name, string? Description)[] preflights)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(taskDir);

        string deps = string.Join(", ", dependsOn.Select(d => "\"" + d + "\""));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{ \"description\": \"" + id + "\", \"dependsOn\": [" + deps + "] }");
        File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "# action\nexit 0\n");

        foreach ((string grName, string? grDesc) in guardrails)
        {
            WriteCheckFile(Path.Combine(taskDir, "guardrails"), grName, grDesc, scope: null);
        }

        foreach ((string pfName, string? pfDesc) in preflights)
        {
            WriteCheckFile(Path.Combine(taskDir, "preflights"), pfName, pfDesc, scope: null);
        }
    }

    /// <summary>
    /// Write one guardrail-shaped check file (<c>&lt;name&gt;.ps1</c> opening with the required
    /// <c>catches:</c> comment) into <paramref name="folder"/>, plus an optional metadata sidecar
    /// carrying the <c>description</c> (the DRAWN label) and/or <c>scope</c>.
    /// </summary>
    private static void WriteCheckFile(string folder, string name, string? description, string? scope)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".ps1"),
            "# catches: " + name + " guards a specific wrong implementation\n"
            + "dotnet build --nologo\nexit $LASTEXITCODE\n");

        if (description is not null || scope is not null)
        {
            var fields = new List<string>();
            if (description is not null) fields.Add("\"description\": \"" + description + "\"");
            if (scope is not null) fields.Add("\"scope\": \"" + scope + "\"");
            File.WriteAllText(Path.Combine(folder, name + ".json"), "{ " + string.Join(", ", fields) + " }");
        }
    }

    private static string PlanPreflightsDir(string planDir) => Path.Combine(planDir, "preflights");

    private static string PlanGuardrailsDir(string planDir) => Path.Combine(planDir, "guardrails");

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    private static string Render(string planDir) => MermaidRenderer.Render(LoadPlan(planDir));

    /// <summary>Split rendered Mermaid into trimmed, non-empty lines for set-based assertions.</summary>
    private static IReadOnlyList<string> Lines(string mermaid) =>
        mermaid.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// True when <paramref name="line"/> is the header of the task container <c>task_&lt;sanitizedId&gt;</c>
    /// (and NOT one of its nested <c>task_&lt;sanitizedId&gt;_...</c> subgraphs).
    /// </summary>
    private static bool IsTaskContainerHeader(string line, string sanitizedId) =>
        line.StartsWith("subgraph task_" + sanitizedId, StringComparison.Ordinal)
        && !line.StartsWith("subgraph task_" + sanitizedId + "_", StringComparison.Ordinal);

    /// <summary>True when <paramref name="line"/> is a subgraph header whose label contains <paramref name="label"/>.</summary>
    private static bool IsSubgraphLabelled(string line, string label) =>
        line.StartsWith("subgraph", StringComparison.Ordinal) && line.Contains(label, StringComparison.Ordinal);

    /// <summary>Index of the first line containing <paramref name="text"/>, or -1 if none does. Used for
    /// the emission-order assertion (preflight node line index &lt; guardrail node line index).</summary>
    private static int IndexOfLineContaining(IReadOnlyList<string> lines, string text)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(text, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The lines of the first subgraph whose header satisfies <paramref name="headerMatch"/>, inclusive of
    /// the header and its matching <c>end</c> (depth-aware, so nested subgraphs are included). Empty when no
    /// such subgraph is emitted (e.g. the current renderer emits no subgraphs at all).
    /// </summary>
    private static IReadOnlyList<string> ContainerBlock(IReadOnlyList<string> lines, Func<string, bool> headerMatch)
    {
        var block = new List<string>();
        int depth = 0;
        bool started = false;

        foreach (string line in lines)
        {
            if (!started)
            {
                if (line.StartsWith("subgraph", StringComparison.Ordinal) && headerMatch(line))
                {
                    started = true;
                    depth = 1;
                    block.Add(line);
                }
                continue;
            }

            block.Add(line);
            if (line.StartsWith("subgraph", StringComparison.Ordinal))
            {
                depth++;
            }
            else if (line.Equals("end", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }

        return block;
    }
}
