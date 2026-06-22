using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2024 validation for the <c>stagingOutputs</c> contract (SSOT §3.5, issue #130). All causes
/// share one error code with a specific reason string. Exercised through the public
/// <see cref="PlanValidator"/> with in-memory plans (no disk).
///
/// Causes (all error):
///   - the array is present but empty
///   - an entry has a missing/empty from
///   - an entry has a missing/empty to
///   - a to does not normalize under .claude/
///   - a to escapes the workspace (absolute / ..)
///   - a from escapes the staging root (absolute / ..)
/// A well-formed stagingOutputs produces NO GR2024.
/// </summary>
public sealed class StagingOutputsValidatorTests
{
    private const string Gr2024 = "GR2024";

    [Fact]
    public void WellFormedStagingOutputs_ProducesNoGr2024()
    {
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = ".claude/skills/foo/" });

        Assert.DoesNotContain(Validate(plan), d => d.Code == Gr2024);
    }

    [Fact]
    public void NestedClaudeTo_ProducesNoGr2024()
    {
        // A nested .claude/ destination and a leading "./" both normalize under .claude/.
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "agent.md", To = ".claude/agents/reviewer.md" },
            new StagingOutput { From = "cmd.md", To = "./.claude/commands/do.md" });

        Assert.DoesNotContain(Validate(plan), d => d.Code == Gr2024);
    }

    [Fact]
    public void AbsentStagingOutputs_ProducesNoGr2024()
    {
        // The off-switch: a task with no stagingOutputs (null) is the unchanged default.
        PlanDefinition plan = InMemoryStagingPlan(StagingTask("01-a", stagingOutputs: null));

        Assert.DoesNotContain(Validate(plan), d => d.Code == Gr2024);
    }

    [Fact]
    public void EmptyArray_ProducesGr2024_Error()
    {
        // Present but empty: declares staging, stages nothing — almost certainly a mistake.
        PlanDefinition plan = InMemoryStagingPlan(StagingTask("01-a", stagingOutputs: []));

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MissingFrom_ProducesGr2024_Error()
    {
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "", To = ".claude/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MissingTo_ProducesGr2024_Error()
    {
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = "" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ToNotUnderClaude_ProducesGr2024_Error()
    {
        // The load-bearing check: stagingOutputs exists only to land .claude/ deliverables; a
        // non-.claude/ destination (use a normal action write) is rejected.
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = "src/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ToLookalikePrefix_NotUnderClaude_ProducesGr2024_Error()
    {
        // ".claudex" is not ".claude" — the first segment must be exactly .claude.
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = ".claudex/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ToEscapesWorkspace_DotDot_ProducesGr2024_Error()
    {
        // A '..' segment climbs out of the workspace — same family as GR2019 for writeScope.
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = "../.claude/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ToAbsolutePath_ProducesGr2024_Error()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\evil\.claude\skills\foo" : "/evil/.claude/skills/foo";
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "skill/**", To = absolute });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FromEscapesStagingRoot_DotDot_ProducesGr2024_Error()
    {
        // 'from' must stay within the staging root; '..' would read outside it.
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = "../escape/**", To = ".claude/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FromAbsolutePath_ProducesGr2024_Error()
    {
        string absolute = OperatingSystem.IsWindows() ? @"C:\elsewhere\skill" : "/elsewhere/skill";
        PlanDefinition plan = PlanWithStaging(
            new StagingOutput { From = absolute, To = ".claude/skills/foo/" });

        Assert.Contains(Validate(plan), d => d.Code == Gr2024 && d.Severity == DiagnosticSeverity.Error);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    /// <summary>A single-task serial-mode plan whose one task declares the given staging outputs.</summary>
    private static PlanDefinition PlanWithStaging(params StagingOutput[] stagingOutputs) =>
        InMemoryStagingPlan(StagingTask("01-a", stagingOutputs));

    /// <summary>
    /// Serial mode (maxParallelism 1) so the worktree-only gates (GR2015/GR2017/GR2018) never fire —
    /// keeping GR2024 the only diagnostic under test. Workspace is a fake path that is never probed.
    /// </summary>
    private static PlanDefinition InMemoryStagingPlan(params TaskNode[] tasks) =>
        new()
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake/workspace",
            Config = new RunConfig { Version = 1, MaxParallelism = 1 },
            Tasks = tasks
        };

    private static TaskNode StagingTask(string id, IReadOnlyList<StagingOutput>? stagingOutputs) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"staging task {id}",
        DependsOn = [],
        StagingOutputs = stagingOutputs,
        // A script action so the plan needs no promptRunners config (keeps GR2024 the only code
        // under test; a prompt action would add GR2008). stagingOutputs is action-kind-agnostic.
        Action = new ActionDefinition
        {
            Path = $"/fake/tasks/{id}/action.sh",
            Kind = ActionKind.Script
        },
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
}
