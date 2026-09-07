using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

public sealed class PlanLoaderTests
{
    private static PlanLoadResult Load(string fixture) =>
        new PlanLoader().Load(TestPaths.Fixture(fixture));

    [Fact]
    public void ValidMinimal_LoadsOneTaskWithActionAndGuardrail()
    {
        PlanLoadResult result = Load("valid-minimal");

        Assert.False(result.HasErrors, DiagnosticDump(result));
        Assert.NotNull(result.Plan);

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Equal("01-do-thing", task.Id);
        Assert.Equal("Do the one thing", task.Description);
        Assert.Empty(task.DependsOn);
        Assert.Equal(ActionKind.Script, task.Action.Kind);
        Assert.EndsWith("action.sh", task.Action.Path);

        GuardrailDefinition guardrail = Assert.Single(task.Guardrails);
        Assert.Equal("01-it-worked", guardrail.Name);
        Assert.Equal(ActionKind.Script, guardrail.Kind);
    }

    [Fact]
    public void ValidMinimal_AppliesConfigDefaults()
    {
        PlanLoadResult result = Load("valid-minimal");

        RunConfig config = result.Plan!.Config;
        Assert.Equal(1, config.Version);
        Assert.Equal(3, config.MaxParallelism);
        Assert.Equal(2, config.DefaultRetries);
        Assert.Equal(1800, config.DefaultTimeoutSeconds);
        Assert.Equal(GuardrailMode.FailFast, config.GuardrailMode);

        // #526: an ABSENT workspace resolves to the enclosing git repository ROOT. This fixture lives
        // inside this repository, so the resolved value climbs to it — NOT to the fixture's parent, which
        // is what the old unconditional ".." produced and what this assertion used to pin.
        string? repoRoot = RepoRootAbove(TestPaths.Fixture("valid-minimal"));
        Assert.NotNull(repoRoot);
        Assert.Equal(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(Path.Combine(TestPaths.Fixture("valid-minimal"), config.Workspace)));
    }

    /// <summary>
    /// The other half of #526, and the one that keeps the change confined: an EXPLICIT workspace wins,
    /// including an explicit <c>".."</c>. Without this, "resolve the default better" could quietly become
    /// "override what the author chose", which is a much larger and much worse change.
    /// </summary>
    [Fact]
    public void AnExplicitWorkspace_IsHonouredVerbatim()
    {
        string planDir = WriteMinimalPlan(workspaceLine: "  \"workspace\": \"..\",");
        try
        {
            Assert.Equal("..", new PlanLoader().Load(planDir).Plan!.Config.Workspace);
        }
        finally { DeleteTree(planDir); }
    }

    /// <summary>
    /// And the fallback: a plan OUTSIDE any git repository keeps the historical <c>".."</c>. The walk has
    /// to terminate somewhere, and terminating on a guess would be worse than the default it replaced.
    /// </summary>
    [Fact]
    public void APlanOutsideAnyRepository_KeepsTheHistoricalDefault()
    {
        Assert.SkipUnless(RepoRootAbove(Path.GetTempPath()) is null,
            "the temp root is itself inside a git repository on this machine, so the no-repo case cannot "
            + "be constructed here");

        string planDir = WriteMinimalPlan(workspaceLine: null);
        try
        {
            Assert.Equal("..", new PlanLoader().Load(planDir).Plan!.Config.Workspace);
        }
        finally { DeleteTree(planDir); }
    }

    /// <summary>A loadable one-task plan in a temp directory, with an optional explicit workspace line.</summary>
    private static string WriteMinimalPlan(string? workspaceLine)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr526-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-only");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string config = workspaceLine is null
            ? "{ \"version\": 1 }"
            : "{" + Environment.NewLine + workspaceLine + Environment.NewLine + "  \"version\": 1 }";
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), config);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{ \"description\": \"only\", \"dependsOn\": [], \"writeScope\": [] }");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh" + Environment.NewLine + "exit 0");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: nothing ran" + Environment.NewLine + "exit 0");

        return planDir;
    }

    /// <summary>The nearest enclosing git repository root, or null when there is none.</summary>
    private static string? RepoRootAbove(string start)
    {
        DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) { return dir.FullName; }
            dir = dir.Parent;
        }

        return null;
    }

    private static void DeleteTree(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingPlanFolder_ReportsMissingFile()
    {
        PlanLoadResult result = Load("does-not-exist-anywhere");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.MissingFile, diagnostic.Code);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void TwoActionFiles_ReportsAmbiguousAction()
    {
        PlanLoadResult result = Load("two-action-files");

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.AmbiguousActionFile);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ZeroActionFiles_ReportsNoAction()
    {
        PlanLoadResult result = Load("zero-action-files");

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.NoActionFile);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void EmptyTasksDirectory_ReportsNoTasks()
    {
        // An empty tasks/ dir can't be a committed fixture (git drops empty dirs), and it must
        // NOT load clean — otherwise it would "run" 0/0 green. Build it on disk and assert GR1009.
        string planDir = Path.Combine(Path.GetTempPath(), "gr-empty-tasks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{ \"version\": 1 }");

            PlanLoadResult result = new PlanLoader().Load(planDir);

            Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.NoTasks);
            Assert.True(result.HasErrors);
        }
        finally
        {
            if (Directory.Exists(planDir))
            {
                Directory.Delete(planDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CommentsAndTrailingCommas_ParseSuccessfully()
    {
        PlanLoadResult result = Load("comments-and-commas");

        Assert.False(result.HasErrors, DiagnosticDump(result));
        Assert.NotNull(result.Plan);
        Assert.Equal(GuardrailMode.RunAll, result.Plan!.Config.GuardrailMode);
        Assert.Equal(2, result.Plan.Config.MaxParallelism);
        Assert.True(result.Plan.Config.Interpreters.ContainsKey(".sh"));
    }

    [Fact]
    public void DeterministicSidecar_LoadsDescriptionArgsAndTimeout()
    {
        PlanLoadResult result = Load("comments-and-commas");

        GuardrailDefinition guardrail = Assert.Single(result.Plan!.Tasks[0].Guardrails);
        Assert.Equal("01-build", guardrail.Name);
        Assert.Equal("Solution builds clean", guardrail.Description);
        Assert.Equal(["--configuration", "Release"], guardrail.Args);
        Assert.Equal(600, guardrail.TimeoutSeconds);
    }

    [Fact]
    public void OrphanGuardrailJson_IsReportedAndRealGuardrailStillLoads()
    {
        PlanLoadResult result = Load("orphan-sidecar");

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.OrphanGuardrailMetadata);

        // The orphan .json is not counted as a guardrail; the real .sh still is.
        GuardrailDefinition guardrail = Assert.Single(result.Plan!.Tasks[0].Guardrails);
        Assert.Equal("01-real", guardrail.Name);
    }

    [Fact]
    public void GoldenExample_LoadsWithoutStructuralErrors()
    {
        // The committed golden example (with prompt tasks) must load clean structurally.
        var loader = new PlanLoader();
        PlanLoadResult result = loader.Load(GoldenExamplePath);

        Assert.False(result.HasErrors, DiagnosticDump(result));
        Assert.Equal(3, result.Plan!.Tasks.Count);

        // Task 02 has a prompt action; task 03 has a prompt guardrail — both recognized by extension.
        TaskNode generate = result.Plan.Tasks.Single(t => t.Id == "02-generate-greeting");
        Assert.Equal(ActionKind.Prompt, generate.Action.Kind);

        TaskNode quality = result.Plan.Tasks.Single(t => t.Id == "03-quality-check");
        Assert.Contains(quality.Guardrails, g => g.Kind == ActionKind.Prompt && g.Name == "02-tone-is-friendly");
    }

    private static string GoldenExamplePath
    {
        get
        {
            // tests/Guardrails.Core.Tests -> repo root -> examples/...
            string repoRoot = Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
            return Path.Combine(repoRoot, "examples", "hello-guardrails", "hello-guardrails");
        }
    }

    private static string DiagnosticDump(PlanLoadResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
}
