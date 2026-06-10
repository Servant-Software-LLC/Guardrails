using Guardrails.Core.Loading;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

public sealed class ValidatorCycleTests
{
    [Fact]
    public void Cycle_ReportsGR2007_WithPath()
    {
        var plan = Plan(
            Task("01-a", "02-b"),
            Task("02-b", "01-a"));

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Diagnostic cycle = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DependencyCycle);
        Assert.Contains("01-a", cycle.Message);
        Assert.Contains("02-b", cycle.Message);
        Assert.Contains("->", cycle.Message);
    }

    [Fact]
    public void Acyclic_NoCycleDiagnostic()
    {
        var plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"));

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.DependencyCycle);
    }
}
