using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Validator tests for the three scope-ownership diagnostics introduced in M3
/// (Plan 05 §8 / §10): GR2015 (subsumption guard, ERROR), GR2016 (independent-task
/// scope overlap, WARNING), GR2017 (malformed writeScope glob, ERROR).
///
/// Authored BEFORE the feature exists: references <c>TaskNode.WriteScope</c> and
/// the three new <see cref="DiagnosticCodes"/> constants — all added by the
/// implementation task. The suite will not compile against this file until then
/// — the intended failure.
/// </summary>
public sealed class ScopeValidationTests
{
    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    // Clone a task with an explicit writeScope (raw string list, null = absent = universal).
    private static TaskNode WithScope(TaskNode task, params string[] globs) =>
        task with { WriteScope = globs };

    // ── GR2015 — Subsumption guard ───────────────────────────────────────────
    // When a dependent's effective writeScope overlaps a (narrow-scoped) ancestor's
    // declared outputs, the ancestor's test-file ownership is unprotected after the
    // retired triad is removed.  GR2015 is a HARD ERROR — these four test cases are
    // its only proof of soundness.

    [Fact]
    public void GR2015a_DependentUniversalScope_FiresError()
    {
        // Ancestor (test-author) owns ["tests/Feature/**"]; dependent claims ["**"].
        // Universal overlaps every non-empty scope → GR2015 Error.
        TaskNode author = WithScope(Task("01-author"), "tests/Feature/**");
        TaskNode impl   = WithScope(Task("02-impl", "01-author"), "**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(author, impl));

        Diagnostic diag = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.WriteScopeSubsumptionViolation);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("02-impl", diag.Message);
    }

    [Fact]
    public void GR2015b_DependentAbsentScope_FiresError()
    {
        // Absent writeScope resolves to universal (§4.1 Plan 05).
        // Universal overlaps every non-empty ancestor scope → GR2015 Error.
        TaskNode author = WithScope(Task("01-author"), "tests/Feature/**");
        TaskNode impl   = Task("02-impl", "01-author"); // WriteScope = null → absent → universal

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(author, impl));

        Diagnostic diag = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.WriteScopeSubsumptionViolation);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("02-impl", diag.Message);
    }

    [Fact]
    public void GR2015c_DependentScopeIntersectsAncestorOutputs_FiresError()
    {
        // Ancestor owns ["tests/Feature/**"]; dependent claims ["src/**","tests/Feature/Helpers/**"].
        // tests/Feature/Helpers/** ⊂ tests/Feature/** → overlap → GR2015 Error.
        TaskNode author = WithScope(Task("01-author"), "tests/Feature/**");
        TaskNode impl   = WithScope(Task("02-impl", "01-author"), "src/**", "tests/Feature/Helpers/**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(author, impl));

        Diagnostic diag = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.WriteScopeSubsumptionViolation);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("02-impl", diag.Message);
    }

    [Fact]
    public void GR2015d_DependentProperlyExcludesAncestorOutputs_NoGR2015()
    {
        // Ancestor owns ["tests/Feature/**"]; dependent claims only ["src/**"].
        // ["src/**"] and ["tests/Feature/**"] are disjoint → GR2015 does NOT fire.
        TaskNode author = WithScope(Task("01-author"), "tests/Feature/**");
        TaskNode impl   = WithScope(Task("02-impl", "01-author"), "src/**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(author, impl));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.WriteScopeSubsumptionViolation);
    }

    // ── GR2016 — Independent-task scope overlap ──────────────────────────────
    // Two tasks with no DAG path between them whose writeScopes overlap are a plan
    // smell (lost parallelism / potential write-write conflict).  A WARNING only —
    // the harness serialises them, no data is lost.

    [Fact]
    public void GR2016_IndependentTasksWithOverlappingScopes_FiresWarning()
    {
        // No DAG edge between the two; src/Feature/Sub/** ⊂ src/Feature/** → overlap → GR2016 Warning.
        TaskNode a = WithScope(Task("01-task-a"), "src/Feature/**");
        TaskNode b = WithScope(Task("02-task-b"), "src/Feature/Sub/**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(a, b));

        Diagnostic diag = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.IndependentTaskScopeOverlap);
        Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
    }

    [Fact]
    public void GR2016_IndependentTasksWithDisjointScopes_NoGR2016()
    {
        // No DAG edge; src/A/** and src/B/** are disjoint sibling dirs → no GR2016.
        TaskNode a = WithScope(Task("01-task-a"), "src/A/**");
        TaskNode b = WithScope(Task("02-task-b"), "src/B/**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(a, b));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.IndependentTaskScopeOverlap);
    }

    // ── GR2017 — Malformed writeScope glob ──────────────────────────────────
    // A writeScope entry containing ?, brace-expansion, or negation is an ERROR at
    // validate time (Plan 05 §8).  Detected when the validator tries to parse the
    // raw glob strings — the same characters WriteScope.Parse already rejects via
    // ArgumentException.

    [Theory]
    [InlineData("src/?.cs")]     // single-char wildcard
    [InlineData("src/{A,B}/**")] // brace expansion
    [InlineData("!src/**")]      // negation
    public void GR2017_MalformedGlob_FiresError(string malformedGlob)
    {
        TaskNode task = WithScope(Task("01-only"), malformedGlob);

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(task));

        Diagnostic diag = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.MalformedWriteScopeGlob);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("01-only", diag.Message);
    }

    [Fact]
    public void GR2017_ValidGlob_NoGR2017()
    {
        TaskNode task = WithScope(Task("01-only"), "src/Feature/**");

        IReadOnlyList<Diagnostic> diagnostics = Validate(Plan(task));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.MalformedWriteScopeGlob);
    }
}
