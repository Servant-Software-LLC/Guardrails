using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Validation of per-folder check-<c>Name</c> uniqueness (SSOT §4.5, GR2035, issue #332). A guardrail's
/// <see cref="GuardrailDefinition.Name"/> is its filename with the final extension dropped, so a portable
/// pair like <c>01-build.ps1</c> + <c>01-build.sh</c> in ONE folder both collapse to Name "01-build".
/// Every surface keyed by <c>(taskId, Name)</c> / bare Name (the #219 status badges, the journal's
/// <c>FailedGuardrail.Name</c>, the resume seed) then silently merges the two distinct checks, so a result
/// is misattributed to the wrong node. The validator rejects that ambiguity at load time with an ERROR
/// (<see cref="DiagnosticCodes.DuplicateCheckName"/>) — checked per folder for EACH of the four-folder
/// model's folders. Mirrors <see cref="CostCapValidatorTests"/>: build an in-memory plan, validate with a
/// fake probe that resolves everything, assert on the diagnostic code.
/// </summary>
public sealed class DuplicateCheckNameValidatorTests
{
    /// <summary>A script check named <paramref name="name"/> whose file has <paramref name="ext"/> under <paramref name="folder"/>.</summary>
    private static GuardrailDefinition Check(string folder, string name, string ext) => new()
    {
        Name = name,
        Path = $"/fake/{folder}/{name}{ext}",
        Kind = ActionKind.Script,
    };

    /// <summary>The real #332 case: <c>&lt;name&gt;.ps1</c> + <c>&lt;name&gt;.sh</c> in one folder → both Name <paramref name="name"/>.</summary>
    private static IReadOnlyList<GuardrailDefinition> PortablePair(string folder, string name) =>
        [Check(folder, name, ".ps1"), Check(folder, name, ".sh")];

    private static TaskNode TaskWith(
        string id,
        IReadOnlyList<GuardrailDefinition> guardrails,
        IReadOnlyList<GuardrailDefinition>? preflights = null) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"task {id}",
        Action = new ActionDefinition { Path = $"/fake/tasks/{id}/action.sh", Kind = ActionKind.Script },
        Guardrails = guardrails,
        Preflights = preflights ?? [],
    };

    private static PlanDefinition PlanWith(
        IReadOnlyList<GuardrailDefinition>? planPreflights,
        IReadOnlyList<GuardrailDefinition>? planGuardrails,
        params TaskNode[] tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks,
        PlanPreflights = planPreflights ?? [],
        PlanGuardrails = planGuardrails ?? [],
    };

    // Validate with everything resolvable so interpreter probing never fires a false error — the rule
    // under test is the only diagnostic in play.
    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    private static Diagnostic AssertSingleDuplicateNameError(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DuplicateCheckName);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        return diagnostic;
    }

    // === each of the four folders is a covered site ====================================

    [Fact]
    public void TaskGuardrailsFolder_DuplicateName_IsDuplicateCheckNameError()
    {
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a", PortablePair("tasks/01-a/guardrails", "01-build")));

        AssertSingleDuplicateNameError(Validate(plan));
    }

    [Fact]
    public void TaskPreflightsFolder_DuplicateName_IsDuplicateCheckNameError()
    {
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a",
                guardrails: [Check("tasks/01-a/guardrails", "01-check", ".sh")],
                preflights: PortablePair("tasks/01-a/preflights", "01-dep-delivered")));

        AssertSingleDuplicateNameError(Validate(plan));
    }

    [Fact]
    public void PlanLevelPreflightsFolder_DuplicateName_IsDuplicateCheckNameError()
    {
        PlanDefinition plan = PlanWith(
            planPreflights: PortablePair("preflights", "01-baseline-green"),
            planGuardrails: null,
            TaskWith("01-a", [Check("tasks/01-a/guardrails", "01-check", ".sh")]));

        AssertSingleDuplicateNameError(Validate(plan));
    }

    [Fact]
    public void PlanLevelGuardrailsFolder_DuplicateName_IsDuplicateCheckNameError()
    {
        PlanDefinition plan = PlanWith(
            planPreflights: null,
            planGuardrails: PortablePair("guardrails", "01-full-suite"),
            TaskWith("01-a", [Check("tasks/01-a/guardrails", "01-check", ".sh")]));

        AssertSingleDuplicateNameError(Validate(plan));
    }

    [Fact]
    public void WaveGuardrailsFolder_DuplicateName_IsDuplicateCheckNameError()
    {
        // Wave-level folders are guardrail-shaped too (SSOT §14.3) — the same collapse risk, so covered.
        TaskNode waveTask = TaskWith("wave-01-x/01-a", [Check("wave-01-x/tasks/01-a/guardrails", "01-check", ".sh")])
            with { WaveDir = "wave-01-x" };
        var wave = new WaveNode
        {
            Dir = "wave-01-x",
            Number = 1,
            Slug = "x",
            Directory = "/fake/plan/wave-01-x",
            Tasks = [waveTask],
            Guardrails = PortablePair("wave-01-x/guardrails", "01-full-suite"),
        };
        PlanDefinition plan = PlanWith(null, null, waveTask) with { Waves = [wave] };

        AssertSingleDuplicateNameError(Validate(plan));
    }

    // === the diagnostic message names the folder, the name, and both files =============

    [Fact]
    public void Message_NamesTheFolder_TheDuplicatedName_AndTheCollidingFiles()
    {
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a", PortablePair("tasks/01-a/guardrails", "01-build")));

        Diagnostic diagnostic = AssertSingleDuplicateNameError(Validate(plan));

        Assert.Contains("task '01-a' guardrails/", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("01-build", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("01-build.ps1", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("01-build.sh", diagnostic.Message, StringComparison.Ordinal);
    }

    // === negatives: no false positive ==================================================

    [Fact]
    public void DistinctNamesInOneFolder_ProduceNoDuplicateCheckNameError()
    {
        // The common case: 01-build.ps1 + 02-test.sh — different Names, no collapse.
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a",
                [Check("tasks/01-a/guardrails", "01-build", ".ps1"), Check("tasks/01-a/guardrails", "02-test", ".sh")]));

        Assert.DoesNotContain(Validate(plan), d => d.Code == DiagnosticCodes.DuplicateCheckName);
    }

    [Fact]
    public void SameNameInDifferentFolders_ProducesNoDuplicateCheckNameError()
    {
        // "01-build" in task-A's guardrails AND task-B's guardrails is fine — the key is (taskId, Name),
        // so different folders never collide. Only WITHIN one folder is a duplicate ambiguous.
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a", [Check("tasks/01-a/guardrails", "01-build", ".sh")]),
            TaskWith("02-b", [Check("tasks/02-b/guardrails", "01-build", ".sh")]));

        Assert.DoesNotContain(Validate(plan), d => d.Code == DiagnosticCodes.DuplicateCheckName);
    }

    [Fact]
    public void CaseOnlyDifferenceInName_IsNotACollision_Ordinal()
    {
        // Comparison is Ordinal, matching the case-sensitive keying the collapsing maps actually use:
        // "01-Build" and "01-build" stay two distinct keys, so there is no collapse and no error.
        PlanDefinition plan = PlanWith(null, null,
            TaskWith("01-a",
                [Check("tasks/01-a/guardrails", "01-Build", ".ps1"), Check("tasks/01-a/guardrails", "01-build", ".sh")]));

        Assert.DoesNotContain(Validate(plan), d => d.Code == DiagnosticCodes.DuplicateCheckName);
    }
}
