using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Validation of the per-run cost cap (SSOT §2): a present-but-non-positive
/// <c>maxCostUsd</c> is a configuration mistake — a zero/negative cap would halt the run
/// before any work — so the validator emits an ERROR with code
/// <see cref="DiagnosticCodes.CostCapNonPositive"/>. A positive cap (or no cap) produces no
/// such diagnostic. Mirrors <see cref="PlanValidatorTests"/>: build a plan, validate with a
/// fake probe that resolves everything, assert on the diagnostic code.
///
/// Authored BEFORE the feature exists: references <c>RunConfig.MaxCostUsd</c> and
/// <c>DiagnosticCodes.CostCapNonPositive</c>, both added by the implementation task. The suite
/// will not compile against this file until then — the intended failure.
/// </summary>
public sealed class CostCapValidatorTests
{
    /// <summary>A clean single-task plan whose config carries the given cap.</summary>
    private static PlanDefinition PlanWithCap(decimal? maxCostUsd)
    {
        PlanDefinition plan = Plan(Task("01-only"));
        return plan with { Config = plan.Config with { MaxCostUsd = maxCostUsd } };
    }

    // Validate with everything resolvable so interpreter probing never fires a false error —
    // the only diagnostic in play is the cost-cap rule under test.
    private static IReadOnlyList<Diagnostic> Validate(decimal? maxCostUsd) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(PlanWithCap(maxCostUsd));

    public static TheoryData<decimal> NonPositiveCaps =>
    [
        0m,
        -1.50m,
        -0.01m
    ];

    [Theory]
    [MemberData(nameof(NonPositiveCaps))]
    public void NonPositiveCap_IsCostCapNonPositiveError(decimal cap)
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate(cap);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.CostCapNonPositive);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void PositiveCap_ProducesNoCostCapDiagnostic()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate(1.50m);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.CostCapNonPositive);
    }

    [Fact]
    public void NoCap_ProducesNoCostCapDiagnostic()
    {
        IReadOnlyList<Diagnostic> diagnostics = Validate(maxCostUsd: null);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.CostCapNonPositive);
    }
}
