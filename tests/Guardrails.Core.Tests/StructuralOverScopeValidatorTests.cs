using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2042 — the deterministic STRUCTURAL over-scope lint (issue #378, SSOT §3.4). A WARNING keyed on the
/// co-occurring over-scope fingerprint sitting in the emitted <c>task.json</c> (writeScope cardinality +
/// <c>action.maxTurns</c> + <c>dependsOn</c> fan-in), so the fan-in / composition-root-wiring SINK archetype
/// (the motivating task-15 shape) is caught at author/validate time — LEFT of the run — even when the
/// authoring prompt rationalizes past it. Three clauses (any fires):
/// <list type="bullet">
///   <item>(i) <c>maxTurns &gt;= </c><see cref="PlanValidator.OverScopeTurnThreshold"/> AND <c>writeScope &gt;= 4</c>;</item>
///   <item>(ii) <c>writeScope &gt;= 6</c> regardless of budget;</item>
///   <item>(iii) <c>dependsOn &gt;= 5</c> AND <c>writeScope &gt;= 3</c> (a fan-in sink).</item>
/// </list>
/// These tests pin BOTH halves: the committed <c>over-scope-fanin-sink</c> fixture (the faithful task-15
/// shape) fires on the SINK only, and the <c>over-scope-near-miss</c> fixture (just under every threshold)
/// stays clean; plus per-clause boundary tests that a non-writing / sub-threshold task never trips it.
/// </summary>
public sealed class StructuralOverScopeValidatorTests
{
    private static IReadOnlyList<Diagnostic> Validate(string fixture) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(LoadPlan(fixture));

    private static PlanDefinition LoadPlan(string fixture)
    {
        PlanLoadResult result = new PlanLoader().Load(TestPaths.Fixture(fixture));
        Assert.NotNull(result.Plan);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        return result.Plan!;
    }

    // ---- Committed fixtures: the faithful task-15 shape fires; the near-miss stays clean ----------

    [Fact]
    public void FaninSink_FiresGR2042_OnTheSinkTaskOnly()
    {
        IReadOnlyList<Diagnostic> diags = Validate("over-scope-fanin-sink");

        Diagnostic gr2042 = Assert.Single(diags, d => d.Code == DiagnosticCodes.StructuralOverScope);
        Assert.Equal(DiagnosticSeverity.Warning, gr2042.Severity);
        // Fires on the SINK, never a producer.
        Assert.Contains("06-wire-into-composition-root", gr2042.Path);
        // Names the offending signals + the split remedy.
        Assert.Contains("maxTurns 75", gr2042.Message);
        Assert.Contains("fans in 5", gr2042.Message);
        Assert.Contains("Split it", gr2042.Message);
        Assert.Contains("composition-root", gr2042.Message);
    }

    [Fact]
    public void FaninSink_TheWarningIsTheOnlyDiagnostic()
    {
        // The fixture is an otherwise-clean plan: its ONLY concern is the over-scoped sink, so a single
        // GR2042 warning is the entire diagnostic set (no producer trips it, no errors).
        Diagnostic only = Assert.Single(Validate("over-scope-fanin-sink"));
        Assert.Equal(DiagnosticCodes.StructuralOverScope, only.Code);
    }

    [Fact]
    public void NearMiss_DoesNotFireGR2042_AndIsClean()
    {
        // 4 writeScope paths, maxTurns 50 (< 60), no fan-in — just under every clause. Fully clean.
        Assert.Empty(Validate("over-scope-near-miss"));
    }

    // ---- Per-clause boundary tests (temp single-task plan; no fan-in) -----------------------------

    [Fact]
    public void ClauseII_WideBlastRadius_SixPaths_FiresRegardlessOfBudget()
    {
        // writeScope of 6, no maxTurns, no dependsOn — clause (ii) alone.
        AssertFires(paths: 6, maxTurns: null);
    }

    [Fact]
    public void ClauseI_TurnHeavyPlusMultiFile_FiresAtThreshold()
    {
        // maxTurns exactly at the threshold (60) with 4 paths — clause (i).
        AssertFires(paths: 4, maxTurns: PlanValidator.OverScopeTurnThreshold);
    }

    [Fact]
    public void ClauseI_JustBelowTurnThreshold_DoesNotFire()
    {
        // 4 paths but maxTurns one below the threshold — the turn teeth have a real boundary.
        AssertDoesNotFire(paths: 4, maxTurns: PlanValidator.OverScopeTurnThreshold - 1);
    }

    [Fact]
    public void ClauseI_TurnHeavyButOnlyThreePaths_DoesNotFire()
    {
        // maxTurns 75 but only 3 paths — clause (i) needs >= 4, and clause (ii) needs >= 6.
        AssertDoesNotFire(paths: 3, maxTurns: 75);
    }

    [Fact]
    public void FivePaths_NoBudget_NoFanIn_DoesNotFire()
    {
        // 5 paths sits between clause (i)'s 4 (but no turn-heavy budget) and clause (ii)'s 6 — clean.
        AssertDoesNotFire(paths: 5, maxTurns: null);
    }

    [Fact]
    public void NonWritingTask_EmptyWriteScope_NeverFires()
    {
        // A read-only / verification task ([]  = writes nothing) can never trip any clause (Count 0).
        AssertDoesNotFire(paths: 0, maxTurns: 75);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void AssertFires(int paths, int? maxTurns)
    {
        string planDir = BuildSingleTaskPlan(paths, maxTurns);
        try
        {
            IReadOnlyList<Diagnostic> diags =
                new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);
            Assert.Contains(diags, d => d.Code == DiagnosticCodes.StructuralOverScope);
        }
        finally { Cleanup(planDir); }
    }

    private static void AssertDoesNotFire(int paths, int? maxTurns)
    {
        string planDir = BuildSingleTaskPlan(paths, maxTurns);
        try
        {
            IReadOnlyList<Diagnostic> diags =
                new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);
            Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.StructuralOverScope);
        }
        finally { Cleanup(planDir); }
    }

    /// <summary>
    /// Builds a minimal single-task plan in a temp dir with <paramref name="paths"/> writeScope entries and
    /// an optional <c>action.maxTurns</c>. No <c>dependsOn</c> (fan-in is exercised by the committed fixture),
    /// so the plan is otherwise valid and the only signals under test are writeScope cardinality + maxTurns.
    /// </summary>
    private static string BuildSingleTaskPlan(int paths, int? maxTurns)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-378-os-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-do-thing");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");

        string scope = string.Join(", ", Enumerable.Range(0, paths).Select(i => $"\"src/File{i}.cs\""));
        string actionLine = maxTurns is int t ? $"  \"action\": {{ \"maxTurns\": {t} }},\n" : "";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{\n" + actionLine +
            $"  \"description\": \"Do the one thing\",\n  \"dependsOn\": [],\n  \"writeScope\": [{scope}]\n}}\n");

        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho ran\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: the action produced no evidence it ran\nexit 0\n");

        return planDir;
    }

    private static void Cleanup(string planDir)
    {
        try { if (Directory.Exists(planDir)) Directory.Delete(planDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}
