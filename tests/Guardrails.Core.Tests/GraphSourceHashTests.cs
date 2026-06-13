using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="GraphSourceHash.Compute"/> (SSOT §10): the staleness key over a
/// plan's diagram-relevant shape. It must be deterministic, order-independent for inputs the
/// renderer sorts (guardrails, dependsOn, task enumeration), and must change when the DAG's
/// shape changes (add/remove task, add/remove guardrail file, change dependsOn).
/// </summary>
public sealed class GraphSourceHashTests
{
    private static GuardrailDefinition Guardrail(string name) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = ActionKind.Script
    };

    private static TaskNode TaskWith(
        string id,
        IReadOnlyList<GuardrailDefinition> guardrails,
        params string[] dependsOn) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"fixture task {id}",
        DependsOn = dependsOn,
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
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
        // Same two guardrails, supplied in different order — the hash sorts basenames ordinal.
        PlanDefinition ascending = Plan(
            TaskWith("01-a", [Guardrail("01-build"), Guardrail("02-test")]));
        PlanDefinition descending = Plan(
            TaskWith("01-a", [Guardrail("02-test"), Guardrail("01-build")]));

        Assert.Equal(GraphSourceHash.Compute(ascending), GraphSourceHash.Compute(descending));
    }

    [Fact]
    public void Compute_UnchangedByDependsOnInputOrder()
    {
        // 03-c dependsOn (01-a, 02-b) vs (02-b, 01-a) — dependsOn is sorted ordinal.
        PlanDefinition one = Plan(
            Task("01-a"),
            Task("02-b"),
            TaskWith("03-c", [Guardrail("01-check")], "01-a", "02-b"));
        PlanDefinition two = Plan(
            Task("01-a"),
            Task("02-b"),
            TaskWith("03-c", [Guardrail("01-check")], "02-b", "01-a"));

        Assert.Equal(GraphSourceHash.Compute(one), GraphSourceHash.Compute(two));
    }

    [Fact]
    public void Compute_UnchangedByTaskEnumerationOrder()
    {
        // Same plan, tasks listed in different order — Compute sorts tasks ordinal.
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
    public void Compute_Changes_WhenGuardrailFileRenamed()
    {
        // Same count, different basename → different DAG-relevant shape.
        PlanDefinition before = Plan(TaskWith("01-a", [Guardrail("01-build")]));
        PlanDefinition after = Plan(TaskWith("01-a", [Guardrail("01-compile")]));

        Assert.NotEqual(GraphSourceHash.Compute(before), GraphSourceHash.Compute(after));
    }

    [Fact]
    public void Compute_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GraphSourceHash.Compute(null!));
    }
}
