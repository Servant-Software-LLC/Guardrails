using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// #389 — <c>writeScope</c> is REQUIRED on every task, with three states (SSOT §3.4):
/// <list type="bullet">
///   <item><c>["src/Foo/"]</c> — writes those paths;</item>
///   <item><c>[]</c> — a DELIBERATE "writes nothing to the repo" declaration, VALID (never flagged);</item>
///   <item>ABSENT / null — a validation ERROR, <c>GR2041</c> (the "lazy planning" this forbids).</item>
/// </list>
/// These tests pin BOTH halves of the contract: the loader PRESERVES the null-vs-empty distinction
/// (a <c>Count: &gt; 0</c> coalesce there would collapse <c>[]</c> to null and defeat GR2041), and
/// the validator fires GR2041 on null while letting <c>[]</c> and a real scope fall through clean.
/// </summary>
public sealed class WriteScopeRequiredTests
{
    /// <summary>
    /// Builds a minimal single-task plan in a temp dir, mirroring the <c>valid-minimal</c> fixture,
    /// with the task's <c>writeScope</c> line set to <paramref name="writeScopeLine"/> (or omitted
    /// when null). Returns the plan folder path; the caller deletes it.
    /// </summary>
    private static string BuildPlan(string? writeScopeLine)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-389-ws-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-do-thing");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");

        string scopeField = writeScopeLine is null ? "" : $"  {writeScopeLine}\n";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{\n" + scopeField + "  \"description\": \"Do the one thing\",\n  \"dependsOn\": []\n}\n");

        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho \"did the thing\"\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-it-worked.sh"),
            "# catches: the action printed nothing / produced no evidence it ran\nexit 0\n");

        return planDir;
    }

    private static void Cleanup(string planDir)
    {
        try { if (Directory.Exists(planDir)) Directory.Delete(planDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    // ---- Loader: null-vs-empty is PRESERVED (pins PlanLoader binding) -----------------------

    [Fact]
    public void Loader_AbsentWriteScope_BindsToNull()
    {
        string planDir = BuildPlan(writeScopeLine: null);
        try
        {
            PlanLoadResult result = new PlanLoader().Load(planDir);
            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
            TaskNode task = Assert.Single(result.Plan!.Tasks);
            Assert.Null(task.WriteScope);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void Loader_EmptyWriteScope_BindsToEmptyNonNullList()
    {
        string planDir = BuildPlan("\"writeScope\": [],");
        try
        {
            PlanLoadResult result = new PlanLoader().Load(planDir);
            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
            TaskNode task = Assert.Single(result.Plan!.Tasks);
            Assert.NotNull(task.WriteScope);          // present-empty must NOT collapse to null
            Assert.Empty(task.WriteScope!);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void Loader_RealWriteScope_BindsToList()
    {
        string planDir = BuildPlan("\"writeScope\": [\"src/Foo/\"],");
        try
        {
            PlanLoadResult result = new PlanLoader().Load(planDir);
            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
            TaskNode task = Assert.Single(result.Plan!.Tasks);
            Assert.Equal(["src/Foo/"], task.WriteScope!);
        }
        finally { Cleanup(planDir); }
    }

    // ---- Validator: GR2041 fires on absent, silent on [] and real --------------------------

    [Fact]
    public void Validator_AbsentWriteScope_ReportsGR2041Error()
    {
        string planDir = BuildPlan(writeScopeLine: null);
        try
        {
            PlanDefinition plan = new PlanLoader().Load(planDir).Plan!;
            IReadOnlyList<Diagnostic> diags = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

            Diagnostic gr2041 = Assert.Single(diags, d => d.Code == DiagnosticCodes.MissingWriteScope);
            Assert.Equal(DiagnosticSeverity.Error, gr2041.Severity);
            Assert.Contains("writeScope", gr2041.Message);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void Validator_EmptyWriteScope_IsValid_NoGR2041()
    {
        string planDir = BuildPlan("\"writeScope\": [],");
        try
        {
            PlanDefinition plan = new PlanLoader().Load(planDir).Plan!;
            IReadOnlyList<Diagnostic> diags = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

            Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.MissingWriteScope);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void Validator_RealWriteScope_IsValid_NoGR2041()
    {
        string planDir = BuildPlan("\"writeScope\": [\"src/Foo/\"],");
        try
        {
            PlanDefinition plan = new PlanLoader().Load(planDir).Plan!;
            IReadOnlyList<Diagnostic> diags = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

            Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.MissingWriteScope);
        }
        finally { Cleanup(planDir); }
    }
}
