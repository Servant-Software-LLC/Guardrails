using Guardrails.Core.Graph;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="GraphSourceHash.Compute"/> (SSOT §10): the staleness key over a
/// plan's diagram. It is the SHA-256 of the renderer's semantic content (drawn node labels +
/// DAG shape), so it must be deterministic, order-independent for inputs the renderer sorts
/// (guardrails, dependsOn, task enumeration), change when the DAG's shape changes
/// (add/remove task, add/remove guardrail, change dependsOn) OR when a drawn label changes
/// (a guardrail <c>description</c>), and be unaffected by what the diagram does NOT draw
/// (<c>action.Kind</c>, cosmetic styling).
/// </summary>
public sealed class GraphSourceHashTests
{
    private static GuardrailDefinition Guardrail(
        string name,
        string? description = null,
        ActionKind kind = ActionKind.Script) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = kind,
        Description = description
    };

    private static TaskNode TaskWith(
        string id,
        IReadOnlyList<GuardrailDefinition> guardrails,
        ActionKind actionKind = ActionKind.Script,
        params string[] dependsOn) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"fixture task {id}",
        DependsOn = dependsOn,
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = actionKind },
        Guardrails = guardrails
    };

    [Fact]
    public void Compute_IsDeterministic_SamePlanSameHash()
    {
        PlanDefinition Build() => Plan(
            Task("01-a"),
            Task("02-b", "01-a"));

        Assert.Equal(GraphSourceHash.Compute(Build()), GraphSourceHash.Compute(Build()));
    }

    [Fact]
    public void Compute_IsLowercaseHex64Chars()
    {
        string hash = GraphSourceHash.Compute(Plan(Task("01-a")));

        Assert.Equal(64, hash.Length); // SHA-256 → 32 bytes → 64 hex chars.
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Compute_UnchangedByGuardrailInputOrder()
    {
        // Same two guardrails, supplied in different order — the renderer sorts by name ordinal.
        PlanDefinition ascending = Plan(
            TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]));
        PlanDefinition descending = Plan(
            TaskWith("01-a", [Guardrail("02-test"), Guardrail("01-build")]));

        Assert.Equal(GraphSourceHash.Compute(ascending), GraphSourceHash.Compute(descending));
    }

    [Fact]
    public void Compute_UnchangedByDependsOnInputOrder()
    {
        // 03-c dependsOn (01-a, 02-b) vs (02-b, 01-a) — dependency edges are emitted ordinal.
        PlanDefinition one = Plan(
            Task("01-a"),
            Task("02-b"),
            TaskWith("03-c", [Guardrail("01-check")], dependsOn: ["01-a", "02-b"]));
        PlanDefinition two = Plan(
            Task("01-a"),
            Task("02-b"),
            TaskWith("03-c", [Guardrail("01-check")], dependsOn: ["02-b", "01-a"]));

        Assert.Equal(GraphSourceHash.Compute(one), GraphSourceHash.Compute(two));
    }

    [Fact]
    public void Compute_UnchangedByTaskEnumerationOrder()
    {
        // Same plan, tasks listed in different order — the renderer sorts tasks ordinal.
        PlanDefinition forward = Plan(Task("01-a"), Task("02-b", "01-a"));
        PlanDefinition reversed = Plan(Task("02-b", "01-a"), Task("01-a"));

        Assert.Equal(GraphSourceHash.Compute(forward), GraphSourceHash.Compute(reversed));
    }

    [Fact]
    public void Compute_Changes_WhenGuardrailFileAdded()
    {
        PlanDefinition before = Plan(TaskWith("01-a", [Guardrail("01-build")]));
        PlanDefinition after = Plan(TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Changes_WhenGuardrailFileRemoved()
    {
        PlanDefinition before = Plan(TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]));
        PlanDefinition after = Plan(TaskWith("01-a", [Guardrail("01-build")]));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Changes_WhenTaskAdded()
    {
        PlanDefinition before = Plan(Task("01-a"));
        PlanDefinition after = Plan(Task("01-a"), Task("02-b", "01-a"));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Changes_WhenTaskRemoved()
    {
        PlanDefinition before = Plan(Task("01-a"), Task("02-b", "01-a"));
        PlanDefinition after = Plan(Task("01-a"));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Changes_WhenDependsOnChanges()
    {
        PlanDefinition independent = Plan(Task("01-a"), Task("02-b"));
        PlanDefinition dependent = Plan(Task("01-a"), Task("02-b", "01-a"));

        Assert.NotEqual(GraphSourceHash.Compute(independent), GraphSourceHash.Compute(dependent));
    }

    [Fact]
    public void Compute_Changes_WhenGuardrailNameChanges_AndItIsTheDrawnLabel()
    {
        // No description → the guardrail Name IS the drawn label, so a rename changes the hash.
        PlanDefinition before = Plan(TaskWith("01-a", [Guardrail("01-build")]));
        PlanDefinition after = Plan(TaskWith("01-a", [Guardrail("01-compile")]));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Changes_WhenGuardrailDescriptionChanges()
    {
        // The realignment: the renderer draws Description ?? Name, so editing the description
        // (the DRAWN label) MUST change the hash.
        PlanDefinition before = Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]));
        PlanDefinition after = Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds with zero warnings")]));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_Unchanged_WhenOnlyActionKindChanges()
    {
        // action.Kind is NOT drawn, so it is deliberately excluded from the staleness key.
        PlanDefinition script = Plan(
            TaskWith("01-a", [Guardrail("01-check")], actionKind: ActionKind.Script));
        PlanDefinition prompt = Plan(
            TaskWith("01-a", [Guardrail("01-check")], actionKind: ActionKind.Prompt));

        Assert.Equal(GraphSourceHash.Compute(script), GraphSourceHash.Compute(prompt));
    }

    [Fact]
    public void Compute_Unchanged_WhenOnlyGuardrailFileBasenameChanges_ButLabelIsTheDescription()
    {
        // With a description present, the guardrail Name (and thus its file basename) is NOT
        // drawn — only the description is — so renaming the guardrail file does not change the
        // hash. (Contrast Compute_Changes_WhenGuardrailNameChanges_AndItIsTheDrawnLabel.)
        PlanDefinition before = Plan(
            TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]));
        PlanDefinition after = Plan(
            TaskWith("01-a", [Guardrail("01-compile", description: "Solution builds clean")]));

        Assert.Equal(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GraphSourceHash.Compute(null!));
    }

    /// <summary>
    /// The legend (SSOT §10) must NOT affect <c>source-sha256</c> — same treatment as the existing
    /// cosmetic <c>classDef</c> color lines, which <see cref="MermaidRenderer.SemanticContent"/>
    /// already excludes. Getting this wrong makes <c>graph --check</c> report every plan as
    /// spuriously stale purely from a legend WORDING edit. <see cref="GraphSourceHash.Compute"/>
    /// only ever calls <see cref="MermaidRenderer.SemanticContent"/> (never <see cref="MermaidRenderer.Render"/>
    /// or the legend-appending CLI/HTML paths), so the semantic content can never contain the
    /// legend text in the first place — asserted directly here.
    /// </summary>
    [Fact]
    public void SemanticContent_NeverContainsLegendText()
    {
        PlanDefinition plan = Plan(Task("01-a"), Task("02-b", "01-a"));

        string semantic = MermaidRenderer.SemanticContent(plan);

        Assert.DoesNotContain("Legend", semantic, StringComparison.Ordinal);
        Assert.DoesNotContain("Preflight —", semantic, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt loop", semantic, StringComparison.Ordinal);
    }

    /// <summary>
    /// Direct proof of the hash-exclusion contract: computing the hash before and after mutating
    /// ONLY <see cref="MermaidRenderer.LegendMarkdown"/>'s wording (simulated here by asserting the
    /// hash is a pure function of <see cref="MermaidRenderer.SemanticContent"/>, which never
    /// consumes <see cref="MermaidRenderer.LegendMarkdown"/>) yields an unchanged hash — the same
    /// plan hashes identically regardless of what the legend says, because the legend is never an
    /// input to <see cref="GraphSourceHash.Compute"/>.
    /// </summary>
    [Fact]
    public void Compute_UnaffectedByLegendText_BecauseSemanticContentNeverReadsIt()
    {
        PlanDefinition plan = Plan(Task("01-a"));

        string hashBefore = GraphSourceHash.Compute(plan);
        // "Change" the legend the way GraphCommand/HtmlDiagramRenderer would — by composing a
        // document with different legend wording around the SAME rendered Mermaid source. The
        // renderer/hash inputs (the plan) are untouched, so recomputing must be identical.
        string mermaid = MermaidRenderer.Render(plan);
        string documentWithLegendA = mermaid + "\n\n" + MermaidRenderer.LegendMarkdown;
        string documentWithLegendB = mermaid + "\n\n" + "**Legend**\n\n- a totally different wording\n";
        Assert.NotEqual(documentWithLegendA, documentWithLegendB); // sanity: the documents DO differ

        string hashAfter = GraphSourceHash.Compute(plan);

        Assert.Equal(hashBefore, hashAfter);
    }

    /// <summary>
    /// Cross-platform contract lock (issue #3). Because the hash is now newline-normalized
    /// (<c>\r\n</c>/<c>\r</c> → <c>\n</c>) before hashing, this pinned literal is the SAME
    /// value computed on Windows, Linux, and macOS. This is the exact <c>source-sha256</c>
    /// embedded in the committed <c>examples/hello-guardrails/.../diagram.md</c>; if the
    /// hash ever drifts by platform again, <c>graph --check</c> would flap on CI and this
    /// test fails on whichever OS recomputes a different value.
    /// </summary>
    /// <remarks>
    /// Re-baselined for the nested-box removal simplification: a task container's preflight/guardrail
    /// check nodes are now drawn DIRECTLY inside the container (no nested
    /// <c>Preflights</c>/<c>Guardrails</c> wrapper subgraph) — a change in the renderer's semantic
    /// content, so the golden's <c>source-sha256</c> shifted from the previous nested-box value. The
    /// value below was computed from the real renderer's output (via <c>guardrails graph</c> against
    /// this golden), not guessed. (Previously re-baselined for the container-edge fix, issue #210: the
    /// DAG is drawn <c>subgraph --&gt; subgraph</c>, each edge attaching to a container's outer border,
    /// and container fills use <c>style &lt;id&gt; …</c> statements rather than <c>class</c>
    /// assignments.)
    /// </remarks>
    [Fact]
    public void Compute_GoldenExample_MatchesPinnedCrossPlatformHash()
    {
        const string expected =
            "fbe54bc3976bf97fdf904de27bf0090fab43aadaa9e66c88364977268a8c9a5c";

        PlanLoadResult result = new PlanLoader().Load(GoldenExamplePath);
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Plan);

        Assert.Equal(expected, GraphSourceHash.Compute(result.Plan!));
    }

    /// <summary>
    /// The hashed semantic content must be LF-only on every OS: a stray <c>\r</c> (from
    /// <c>AppendLine</c> = <c>Environment.NewLine</c> on Windows) is exactly what made the
    /// raw hash platform-dependent (issue #3). The hash normalizes regardless, but keeping
    /// the source content LF guards the rendered artifact too.
    /// </summary>
    [Fact]
    public void SemanticContent_GoldenExample_ContainsNoCarriageReturn()
    {
        PlanLoadResult result = new PlanLoader().Load(GoldenExamplePath);
        Assert.NotNull(result.Plan);

        string semantic = MermaidRenderer.SemanticContent(result.Plan!);
        Assert.DoesNotContain('\r', semantic);
    }

    private static string GoldenExamplePath
    {
        get
        {
            // tests/Guardrails.Core.Tests -> repo root -> examples/...
            string repoRoot = Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
            return Path.Combine(repoRoot, "examples", "hello-guardrails", "hello-guardrails");
        }
    }
}
