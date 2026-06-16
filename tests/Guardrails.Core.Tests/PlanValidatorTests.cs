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

    [Fact]
    public void CaptureHashes_EscapingPath_ReportsGr2013_NormalPathDoesNot()
    {
        // An escaping entry (../../etc/passwd) resolves outside the workspace → GR2013 naming the
        // task and path; a normal workspace-relative test path is clean.
        PlanDefinition plan = PlanWithCaptureHashes(
            ("10-author", ["../../etc/passwd", "tests/Foo/BarTests.cs"]));

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Diagnostic escape = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.CaptureHashEscapesWorkspace);
        Assert.Contains("10-author", escape.Message);
        Assert.Contains("../../etc/passwd", escape.Message);
        // The normal path never trips GR2013 (only one diagnostic, for the escaping entry).
        Assert.DoesNotContain("tests/Foo/BarTests.cs", escape.Message);
    }

    [Fact]
    public void CaptureHashes_AbsolutePath_ReportsGr2013()
    {
        // A rooted path ignores the workspace base under Path.Combine and reaches an absolute
        // location — rejected regardless of where it points.
        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/passwd";
        PlanDefinition plan = PlanWithCaptureHashes(("10-author", [absolute]));

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.CaptureHashEscapesWorkspace);
    }

    [Fact]
    public void CaptureHashes_NormalRelativePath_NoDiagnostics()
    {
        PlanDefinition plan = PlanWithCaptureHashes(("10-author", ["tests/Foo/BarTests.cs"]));

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.CaptureHashEscapesWorkspace);
    }

    /// <summary>
    /// Build an in-memory script-only plan whose single task declares the given captureHashes, so the
    /// GR2013 check runs in isolation (real interpreter probing is satisfied by FakeExecutableProbe.All
    /// at the call site). Workspace is an absolute temp dir so path resolution is realistic.
    /// </summary>
    private static PlanDefinition PlanWithCaptureHashes(params (string Id, string[] Paths)[] tasks)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-gr2013-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(planDir, "workspace");

        var nodes = tasks.Select(t => new TaskNode
        {
            Id = t.Id,
            Directory = Path.Combine(planDir, "tasks", t.Id),
            Description = "fixture",
            CaptureHashes = t.Paths,
            Action = new ActionDefinition
            {
                Path = Path.Combine(planDir, "tasks", t.Id, "action.ps1"),
                Kind = ActionKind.Script
            },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(planDir, "tasks", t.Id, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        }).ToList();

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = nodes,
            Workspace = workspace
        };
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
