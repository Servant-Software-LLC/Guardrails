using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

public sealed class PlanValidatorTests
{
    private static PlanDefinition LoadPlan(string fixture)
    {
        PlanLoadResult result = new PlanLoader().Load(TestPaths.Fixture(fixture));
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    // Validate with everything resolvable, so interpreter probing never fires false errors.
    private static IReadOnlyList<Diagnostic> Validate(string fixture) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(LoadPlan(fixture));

    [Fact]
    public void ValidMinimal_HasNoDiagnostics()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate("valid-minimal");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DanglingDependsOn_ReportsUnknownDependency()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate("dangling-depends-on");

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.UnknownDependency, diagnostic.Code);
        Assert.Contains("99-does-not-exist", diagnostic.Message);
    }

    [Fact]
    public void ZeroGuardrails_ReportsNoGuardrails()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate("zero-guardrails");

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.NoGuardrails);
    }

    [Fact]
    public void UsedExtensionWithNoInterpreter_ReportsUnresolvable()
    {
        // bash is NOT in the fake probe → the .sh action/guardrail extension is unresolvable.
        PlanDefinition plan = LoadPlan("valid-minimal");
        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.None).Validate(plan);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.UnresolvableInterpreter);
        Assert.Contains(".sh", diagnostic.Message);
    }

    [Fact]
    public void ResolvableInterpreter_ProducesNoInterpreterError()
    {
        PlanDefinition plan = LoadPlan("valid-minimal");
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.With("bash")).Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.UnresolvableInterpreter);
    }

    [Fact]
    public void GoldenExample_ValidatesCleanWithRealInterpreters()
    {
        // The golden example uses .ps1 scripts; assume the relevant interpreter resolves.
        PlanDefinition plan = LoadGolden();
        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Assert.Empty(diagnostics);
    }

    private static PlanDefinition LoadGolden()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
        string golden = Path.Combine(repoRoot, "examples", "hello-guardrails", "hello-guardrails");
        PlanLoadResult result = new PlanLoader().Load(golden);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }
}
