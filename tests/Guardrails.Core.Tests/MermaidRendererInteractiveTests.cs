using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.RenderInteractive"/> (issue #33) under the deliverable-7
/// CONTAINER model (SSOT §10): the click-augmented source used ONLY by the local <c>diagram.html</c>
/// viewer. The clean <see cref="MermaidRenderer.Render"/> (and thus <c>diagram.md</c> and the staleness
/// hash) stays click-free; the interactive output is the clean container-model source plus deterministic
/// <c>click</c> directives.
///
/// The container-model assertions FAIL against the current (old-model) renderer, which emits bare
/// <c>task_&lt;id&gt;</c>/<c>gr_</c>/<c>done_</c> nodes rather than task-container subgraphs, and go green once
/// the renderer is rewritten. Tagged Category=Preflights (class-level) so these deliberately-red tests are
/// excluded from the green baseline (<c>--filter "Category!=Preflights"</c>).
/// </summary>
[Trait("Category", "Preflights")]
public sealed class MermaidRendererInteractiveTests
{
    // A plan whose task folders are nested UNDER the plan dir (as on disk), so any emitted click hrefs
    // are clean "tasks/<id>/…" relatives rather than "../…".
    private static PlanDefinition NestedPlan() => new()
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
                Action = new ActionDefinition { Path = "/fake/plan/tasks/01-a/action.sh", Kind = ActionKind.Script },
                Guardrails =
                [
                    new GuardrailDefinition
                    {
                        Name = "01-check",
                        Path = "/fake/plan/tasks/01-a/guardrails/01-check.sh",
                        Kind = ActionKind.Script
                    }
                ]
            }
        ]
    };

    [Fact]
    public void Render_IsClickFree_AndIsTheContainerModel()
    {
        // diagram.md / the staleness hash must never carry click directives (GitHub disables them and the
        // targets are file://-local); and the clean render is the container model.
        string clean = MermaidRenderer.Render(NestedPlan());

        Assert.DoesNotContain("click ", clean);
        Assert.Contains("subgraph task_01_a", clean); // RED on the current renderer (bare task_ node, no subgraph)
        Assert.DoesNotContain("done_", clean);
    }

    [Fact]
    public void RenderInteractive_IsTheContainerModelPlusClickDirectives()
    {
        string interactive = MermaidRenderer.RenderInteractive(NestedPlan());

        Assert.Contains("subgraph task_01_a", interactive); // RED on the current renderer
        Assert.DoesNotContain("done_", interactive);
        Assert.Contains("click ", interactive); // nodes still click through to their source (issue #33)
    }

    [Fact]
    public void RenderInteractive_IsDeterministic()
    {
        PlanDefinition plan = NestedPlan();
        Assert.Equal(MermaidRenderer.RenderInteractive(plan), MermaidRenderer.RenderInteractive(plan));
    }
}
