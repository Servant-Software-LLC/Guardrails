using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// In-memory plan/task fixtures for scheduler and graph tests — no disk, no processes.
/// </summary>
public static class PlanFixtures
{
    public static TaskNode Task(string id, params string[] dependsOn) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"fixture task {id}",
        DependsOn = dependsOn,
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-check",
                Path = $"/fake/tasks/{id}/guardrails/01-check.sh",
                Kind = ActionKind.Script
            }
        ]
    };

    public static TaskNode Exclusive(string id, params string[] dependsOn) =>
        Task(id, dependsOn) with { WriteScope = ["**"] };

    public static PlanDefinition Plan(params TaskNode[] tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks
    };
}
