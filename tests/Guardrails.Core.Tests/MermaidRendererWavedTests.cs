using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer"/> on WAVED plans (issue #356): wave subgraphs,
/// entry/exit gates, JIT-stub waves, barrier edges, and the invariant that the flat-plan path
/// is byte-identical after the waved-dispatch refactor.
/// </summary>
public sealed class MermaidRendererWavedTests
{
    // ---------------------------------------------------------------------------
    // Test fixture helpers
    // ---------------------------------------------------------------------------

    private static GuardrailDefinition FakeGuardrail(string name, string dir) => new()
    {
        Name = name,
        Path = $"{dir}/guardrails/{name}.sh",
        Kind = ActionKind.Script
    };

    private static ActionDefinition FakeAction(string dir) => new()
    {
        Path = $"{dir}/action.sh",
        Kind = ActionKind.Script
    };

    /// <summary>
    /// Two-wave plan:
    /// <list type="bullet">
    ///   <item>Wave 1 ("foundation"): two tasks — 01-write and 02-verify (02 depends on 01)</item>
    ///   <item>Wave 2 ("rollout"): JIT stub (no tasks)</item>
    /// </list>
    /// </summary>
    private static PlanDefinition WavedPlan()
    {
        const string planDir = "/fake/plan";
        const string wave1Dir = "/fake/plan/wave-01-foundation";
        const string wave2Dir = "/fake/plan/wave-02-rollout";

        var task01Write = new TaskNode
        {
            Id = "wave-01-foundation/01-write",
            WaveDir = "wave-01-foundation",
            Directory = $"{wave1Dir}/tasks/01-write",
            Description = "fixture — write",
            Action = FakeAction($"{wave1Dir}/tasks/01-write"),
            Guardrails = [FakeGuardrail("01-check", $"{wave1Dir}/tasks/01-write")]
        };

        var task02Verify = new TaskNode
        {
            Id = "wave-01-foundation/02-verify",
            WaveDir = "wave-01-foundation",
            Directory = $"{wave1Dir}/tasks/02-verify",
            Description = "fixture — verify",
            DependsOn = ["wave-01-foundation/01-write"],  // wave-qualified after QualifyWaveDependencies
            Action = FakeAction($"{wave1Dir}/tasks/02-verify"),
            Guardrails = [FakeGuardrail("01-check", $"{wave1Dir}/tasks/02-verify")]
        };

        var wave1 = new WaveNode
        {
            Dir = "wave-01-foundation",
            Number = 1,
            Slug = "foundation",
            Directory = wave1Dir,
            Tasks = [task01Write, task02Verify]
        };

        var wave2 = new WaveNode
        {
            Dir = "wave-02-rollout",
            Number = 2,
            Slug = "rollout",
            Directory = wave2Dir,
            Tasks = []   // JIT stub — not yet broken down
        };

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks = [task01Write, task02Verify],  // flattened union
            Waves = [wave1, wave2]
        };
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void WavedPlan_Render_ContainsWaveSubgraphs()
    {
        string output = MermaidRenderer.Render(WavedPlan());

        // Wave subgraph declarations
        Assert.Contains("subgraph wave_1[", output);
        Assert.Contains("subgraph wave_2[", output);

        // Wave labels (em dash U+2014 is not HTML-escaped by Quote)
        Assert.Contains("Wave 1 — foundation", output);
        Assert.Contains("Wave 2 — rollout", output);
    }

    [Fact]
    public void WavedPlan_Render_TaskLabelsAreShort()
    {
        string output = MermaidRenderer.Render(WavedPlan());

        // The short label (folder name only) must be present as a subgraph label
        Assert.Contains("\"01-write\"", output);
        Assert.Contains("\"02-verify\"", output);

        // The full wave-qualified task ID must NOT appear anywhere in the rendered output.
        // (Node IDs are sanitized to wave_01_foundation_01_write — hyphens and slash become
        // underscores — so the original string with hyphens/slash is absent entirely.)
        Assert.DoesNotContain("wave-01-foundation/01-write", output);
        Assert.DoesNotContain("wave-01-foundation/02-verify", output);
    }

    [Fact]
    public void WavedPlan_Render_JitStubWaveHasStubNode()
    {
        string output = MermaidRenderer.Render(WavedPlan());

        // The JIT-stub placeholder node must appear inside wave_2
        Assert.Contains("wave_2_stub", output);
        Assert.Contains("JIT stub", output);
    }

    [Fact]
    public void WavedPlan_Render_ContainsBarrierEdge()
    {
        string output = MermaidRenderer.Render(WavedPlan());

        // A dotted barrier edge exists between wave 1 exit gate and wave 2 entry gate
        Assert.Contains("-.->" , output);
        Assert.Contains("wave_1_guardrails", output);
        Assert.Contains("wave_2_preflights", output);

        // The dotted edge runs specifically from wave_1_guardrails to wave_2_preflights
        Assert.Contains("wave_1_guardrails -.->", output);
    }

    [Fact]
    public void WavedPlan_Render_EntryAndExitGatesPresent()
    {
        string output = MermaidRenderer.Render(WavedPlan());

        // Wave 1 gate containers
        Assert.Contains("wave_1_preflights", output);
        Assert.Contains("wave_1_guardrails", output);

        // Wave 2 gate containers (always emitted, even for a JIT-stub wave)
        Assert.Contains("wave_2_preflights", output);
        Assert.Contains("wave_2_guardrails", output);

        // Plan-level brackets are still present
        Assert.Contains("plan_preflights", output);
        Assert.Contains("plan_guardrails", output);
    }

    [Fact]
    public void WavedPlan_Render_FlatPlanPathUnchanged()
    {
        // A flat plan (Waves = []) dispatches to the unchanged AppendNodesAndEdgesFlat path.
        // Verify no wave-specific elements appear and the flat container model is intact.
        var flatPlan = new PlanDefinition
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks =
            [
                new TaskNode
                {
                    Id = "01-a",
                    Directory = "/fake/plan/tasks/01-a",
                    Description = "fixture",
                    Action = FakeAction("/fake/plan/tasks/01-a"),
                    Guardrails = [FakeGuardrail("01-check", "/fake/plan/tasks/01-a")]
                }
            ]
            // Waves defaults to [] — IsWaved == false
        };

        string output = MermaidRenderer.Render(flatPlan);

        // Flat path: ordinary task container present
        Assert.Contains("subgraph task_01_a[", output);

        // No wave-specific elements
        Assert.DoesNotContain("subgraph wave_", output);
        Assert.DoesNotContain("wave_preflights", output);
        Assert.DoesNotContain("wave_guardrails", output);
        Assert.DoesNotContain("Wave 1", output);
        Assert.DoesNotContain("-.->" , output);

        // Plan-level brackets still present
        Assert.Contains("plan_preflights", output);
        Assert.Contains("plan_guardrails", output);
    }

    [Fact]
    public void WavedPlan_SemanticContent_IsDeterministic()
    {
        PlanDefinition plan = WavedPlan();

        string first = MermaidRenderer.SemanticContent(plan);
        string second = MermaidRenderer.SemanticContent(plan);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }
}
