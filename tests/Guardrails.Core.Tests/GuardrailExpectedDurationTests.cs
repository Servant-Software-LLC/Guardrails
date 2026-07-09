using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// The optional per-guardrail <c>expectedDurationSeconds</c> hint (SSOT §4.1, issue #331): the loader
/// binds it off the metadata sidecar onto <see cref="GuardrailDefinition.ExpectedDurationSeconds"/>, and
/// the validator rejects a present-but-non-positive value with <see cref="DiagnosticCodes.ExpectedDurationNonPositive"/>
/// (GR2036) across all four guardrail-shaped folders. Absent (null) or positive ⇒ no diagnostic. Mirrors
/// <see cref="GuardrailScopeTests"/> (disk sidecar loading) and <see cref="CostCapValidatorTests"/>
/// (optional-positive validation via <see cref="FakeExecutableProbe"/>).
/// </summary>
public sealed class GuardrailExpectedDurationTests : IDisposable
{
    private readonly string _tempRoot;

    public GuardrailExpectedDurationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-expdur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // ── Loader binding ────────────────────────────────────────────────────────

    [Fact]
    public void Sidecar_ExpectedDurationSeconds_SurfacedOnGuardrailDefinition()
    {
        string planDir = BuildDiskPlanWithSidecar("""{ "expectedDurationSeconds": 900 }""");

        GuardrailDefinition g = Assert.Single(LoadPlan(planDir).Tasks[0].Guardrails);
        Assert.Equal(900, g.ExpectedDurationSeconds);
    }

    [Fact]
    public void Sidecar_ExpectedDurationSeconds_Absent_IsNull()
    {
        string planDir = BuildDiskPlanWithSidecar("""{ "timeoutSeconds": 600 }""");

        GuardrailDefinition g = Assert.Single(LoadPlan(planDir).Tasks[0].Guardrails);
        Assert.Null(g.ExpectedDurationSeconds);
    }

    [Fact]
    public void NoSidecar_ExpectedDurationSeconds_IsNull()
    {
        string planDir = BuildDiskPlanWithSidecar(sidecarJson: null);

        GuardrailDefinition g = Assert.Single(LoadPlan(planDir).Tasks[0].Guardrails);
        Assert.Null(g.ExpectedDurationSeconds);
    }

    // ── Validation (GR2036) ───────────────────────────────────────────────────

    public static TheoryData<int> NonPositive => [0, -1, -900];

    [Theory]
    [MemberData(nameof(NonPositive))]
    public void TaskGuardrail_NonPositiveExpectedDuration_IsGr2035Error(int seconds)
    {
        PlanDefinition plan = PlanWithTaskGuardrail(WithExpected("01-suite", seconds));

        Diagnostic d = Assert.Single(Validate(plan), x => x.Code == DiagnosticCodes.ExpectedDurationNonPositive);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Theory]
    [MemberData(nameof(NonPositive))]
    public void PlanLevelGuardrail_NonPositiveExpectedDuration_IsGr2035Error(int seconds)
    {
        // The check spans all four folders — a bad hint on a <plan>/guardrails/ terminal check fires too.
        PlanDefinition plan = Plan(Task("01-only")) with { PlanGuardrails = [WithExpected("03-terminal", seconds)] };

        Diagnostic d = Assert.Single(Validate(plan), x => x.Code == DiagnosticCodes.ExpectedDurationNonPositive);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void PositiveExpectedDuration_ProducesNoDiagnostic()
    {
        PlanDefinition plan = PlanWithTaskGuardrail(WithExpected("01-suite", 900));

        Assert.DoesNotContain(Validate(plan), d => d.Code == DiagnosticCodes.ExpectedDurationNonPositive);
    }

    [Fact]
    public void AbsentExpectedDuration_ProducesNoDiagnostic()
    {
        PlanDefinition plan = PlanWithTaskGuardrail(WithExpected("01-suite", expected: null));

        Assert.DoesNotContain(Validate(plan), d => d.Code == DiagnosticCodes.ExpectedDurationNonPositive);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    private static GuardrailDefinition WithExpected(string name, int? expected) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = ActionKind.Script,
        ExpectedDurationSeconds = expected
    };

    /// <summary>A clean single-task plan whose only task carries the given guardrail.</summary>
    private static PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = Task("01-only") with { Guardrails = [guardrail] };
        return Plan(task);
    }

    private PlanDefinition LoadPlan(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    // Minimal one-task disk plan whose single script guardrail (01-build.sh) has an optional sidecar.
    private string BuildDiskPlanWithSidecar(string? sidecarJson)
    {
        string planDir = Path.Combine(_tempRoot, "plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "maxParallelism": 1 }""");

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        string grDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(grDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "task", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0");
        File.WriteAllText(Path.Combine(grDir, "01-build.sh"), "exit 0");

        if (sidecarJson is not null)
        {
            File.WriteAllText(Path.Combine(grDir, "01-build.json"), sidecarJson);
        }

        return planDir;
    }
}
