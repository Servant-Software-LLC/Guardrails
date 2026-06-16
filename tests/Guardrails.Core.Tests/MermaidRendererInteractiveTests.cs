using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="MermaidRenderer.RenderInteractive"/> (issue #33): the click-augmented
/// source used ONLY by the local <c>diagram.html</c> viewer. The clean <see cref="MermaidRenderer.Render"/>
/// (and thus <c>diagram.md</c> and the staleness hash) must stay click-free; the interactive output is
/// the clean source plus deterministic, plan-relative, forward-slash <c>click</c> directives.
/// </summary>
public sealed class MermaidRendererInteractiveTests
{
    // A plan whose task folders are nested UNDER the plan dir (as on disk), so the emitted click
    // hrefs are clean "tasks/<id>/…" relatives rather than "../…".
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
    public void Render_IsClickFree()
    {
        // diagram.md / the staleness hash must never carry click directives (GitHub disables them
        // and the targets are file://-local).
        Assert.DoesNotContain("click ", MermaidRenderer.Render(NestedPlan()));
    }

    [Fact]
    public void RenderInteractive_IsCleanSourcePlusClicks()
    {
        PlanDefinition plan = NestedPlan();
        string clean = MermaidRenderer.Render(plan);
        string interactive = MermaidRenderer.RenderInteractive(plan);

        // The interactive output is exactly the clean diagram with click directives appended.
        Assert.StartsWith(clean, interactive);
        Assert.Contains("click ", interactive);
    }

    [Fact]
    public void RenderInteractive_TaskNode_OpensTaskFolder()
    {
        Assert.Contains(
            "click task_01_a href \"tasks/01-a/\" \"01-a\" _blank",
            MermaidRenderer.RenderInteractive(NestedPlan()));
    }

    [Fact]
    public void RenderInteractive_GuardrailNode_OpensGuardrailFile()
    {
        Assert.Contains(
            "click gr_01_a_0 href \"tasks/01-a/guardrails/01-check.sh\" \"01-check\" _blank",
            MermaidRenderer.RenderInteractive(NestedPlan()));
    }

    [Fact]
    public void RenderInteractive_IsDeterministic()
    {
        PlanDefinition plan = NestedPlan();
        Assert.Equal(MermaidRenderer.RenderInteractive(plan), MermaidRenderer.RenderInteractive(plan));
    }
}
