using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.Render"/> (SSOT §10). Pure string mapping — no
/// disk, no processes. The renderer fans each task out to its guardrail nodes, merges those
/// into a per-task "Finished" node, and renders dependency edges as <c>done_A --> task_B</c>.
/// </summary>
public sealed class MermaidRendererTests
{
    // --- small helpers to build tasks with explicit guardrails -------------------------

    private static GuardrailDefinition Guardrail(string name, string? description = null) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = ActionKind.Script,
        Description = description
    };

    private static TaskNode TaskWith(string id, IReadOnlyList<GuardrailDefinition> guardrails, params string[] dependsOn) =>
        new()
        {
            Id = id,
            Directory = $"/fake/tasks/{id}",
            Description = $"fixture task {id}",
            DependsOn = dependsOn,
            Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
            Guardrails = guardrails
        };

    /// <summary>Split rendered Mermaid into trimmed, non-empty lines for set-based assertions.</summary>
    private static IReadOnlyList<string> Lines(string mermaid) =>
        mermaid.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [Fact]
    public void Render_StartsWithFlowchartTd()
    {
        string mermaid = MermaidRenderer.Render(Plan(Task("01-a")));
        Assert.Equal("flowchart TD", Lines(mermaid)[0]);
    }

    [Fact]
    public void Render_FansTaskOutToEachGuardrailThenMergesToDone()
    {
        // 01-a has two guardrails; assert the fan-out (task --> gr) and merge (gr --> done).
        TaskNode task = TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        // Fan-out: the task node points at both guardrail nodes.
        Assert.Contains("task_01_a --> gr_01_a_0", lines);
        Assert.Contains("task_01_a --> gr_01_a_1", lines);

        // Merge: every guardrail node points at the single done node.
        Assert.Contains("gr_01_a_0 --> done_01_a", lines);
        Assert.Contains("gr_01_a_1 --> done_01_a", lines);
    }

    [Fact]
    public void Render_EmitsFinishedNodeOncePerTask()
    {
        TaskNode task = TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        int finishedNodeCount = lines.Count(l =>
            l.StartsWith("done_01_a[", StringComparison.Ordinal) && l.Contains("✓ Finished", StringComparison.Ordinal));

        Assert.Equal(1, finishedNodeCount);
        Assert.Contains("done_01_a[\"01-a ✓ Finished\"]:::done", lines);
    }

    [Fact]
    public void Render_DependencyEdge_GoesFromDoneOfDependencyToDependentTask()
    {
        // 02-b dependsOn 01-a → edge done_01-a --> task_02-b, and NOT task_01-a --> task_02-b.
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(
            Task("01-a"),
            Task("02-b", "01-a"))));

        Assert.Contains("done_01_a --> task_02_b", lines);
        Assert.DoesNotContain("task_01_a --> task_02_b", lines);
    }

    [Fact]
    public void Render_EmitsTheThreeClassDefs()
    {
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(Task("01-a"))));

        Assert.Contains(lines, l => l.StartsWith("classDef task ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("classDef guardrail ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("classDef done ", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_SanitizesAwkwardIds_IntoSafeNodeIds()
    {
        // An id with dots, slashes, spaces, and a quote — every non-alphanumeric → '_'.
        TaskNode task = TaskWith("a.b/c d\"e", [Guardrail("01-x")]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        // Node ids use the sanitized form (one '_' per awkward char); no raw punctuation leaks
        // into an identifier position.
        Assert.Contains("task_a_b_c_d_e[", string.Join("\n", lines));
        Assert.Contains("done_a_b_c_d_e[", string.Join("\n", lines));
        Assert.Contains("task_a_b_c_d_e --> gr_a_b_c_d_e_0", lines);
        Assert.Contains("gr_a_b_c_d_e_0 --> done_a_b_c_d_e", lines);

        // The literal id still appears as a *quoted label*, with its embedded quote escaped.
        Assert.Contains(lines, l => l.Contains("\"a.b/c d&quot;e ✓ Finished\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailWithDescription_UsesDescriptionAsLabel()
    {
        TaskNode task = TaskWith("01-a", [Guardrail("01-build", description: "Solution builds clean")]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        Assert.Contains("gr_01_a_0[\"Solution builds clean\"]:::guardrail", lines);
        // The bare Name is NOT used when a description is present.
        Assert.DoesNotContain(lines, l => l.Contains("[\"01-build\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_GuardrailWithoutDescription_UsesNameAsLabel()
    {
        TaskNode task = TaskWith("01-a", [Guardrail("01-build", description: null)]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        Assert.Contains("gr_01_a_0[\"01-build\"]:::guardrail", lines);
    }

    [Fact]
    public void Render_WhitespaceDescription_FallsBackToName()
    {
        // Description present but blank → renderer treats it as absent and uses the Name.
        TaskNode task = TaskWith("01-a", [Guardrail("01-build", description: "   ")]);
        IReadOnlyList<string> lines = Lines(MermaidRenderer.Render(Plan(task)));

        Assert.Contains("gr_01_a_0[\"01-build\"]:::guardrail", lines);
    }

    /// <summary>
    /// Golden-shape lock for a tiny 2-task fixture (01-a, and 02-b dependsOn 01-a, one
    /// guardrail each). Locks the EXACT emitted line set so a future renderer drift is caught.
    /// </summary>
    [Fact]
    public void Render_TwoTaskFixture_MatchesGoldenLineSet()
    {
        IReadOnlyList<string> actual = Lines(MermaidRenderer.Render(Plan(
            TaskWith("01-a", [Guardrail("01-check")]),
            TaskWith("02-b", [Guardrail("01-check")], "01-a"))));

        string[] expected =
        [
            "flowchart TD",
            // task 01-a
            "task_01_a[\"01-a\"]:::task",
            "gr_01_a_0[\"01-check\"]:::guardrail",
            "task_01_a --> gr_01_a_0",
            "gr_01_a_0 --> done_01_a",
            "done_01_a[\"01-a ✓ Finished\"]:::done",
            // task 02-b
            "task_02_b[\"02-b\"]:::task",
            "gr_02_b_0[\"01-check\"]:::guardrail",
            "task_02_b --> gr_02_b_0",
            "gr_02_b_0 --> done_02_b",
            "done_02_b[\"02-b ✓ Finished\"]:::done",
            // dependency edge: 02-b dependsOn 01-a
            "done_01_a --> task_02_b",
            // class defs
            "classDef task fill:#cfe8ff,stroke:#1b6ec2,color:#0b2545;",
            "classDef guardrail fill:#fff3cd,stroke:#b8860b,color:#3d2c00;",
            "classDef done fill:#d4edda,stroke:#2e7d32,color:#10341a;",
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Render_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MermaidRenderer.Render(null!));
    }
}
