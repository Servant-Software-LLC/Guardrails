using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Loader round-trip and validation coverage for the task.json <c>action.model</c> override (issue
/// #200): mirrors <c>action.maxTurns</c> exactly — a per-task model override, resolved with task.json
/// <c>action.model</c> &gt; <c>promptRunners.&lt;name&gt;.model</c> &gt; the CLI's own default. The
/// resolution-precedence and provenance behavior are covered by
/// <c>Guardrails.Integration.Tests.ActionModelResolutionTests</c> (needs a real
/// <see cref="Execution.TaskExecutor"/>/<see cref="Execution.Scheduler"/> run); this file covers what
/// is reachable at the loader/validator layer alone.
/// </summary>
public sealed class ActionModelOverrideTests : IDisposable
{
    private readonly string _root;

    public ActionModelOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-actionmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string PlanWith(string guardrailsJson, string taskJson)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);
        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");
        return _root;
    }

    private const string RunnerConfigJson =
        """
        {
          "version": 1,
          "promptRunners": {
            "default": "claude",
            "claude": { "command": "claude", "model": "claude-sonnet-5" }
          }
        }
        """;

    [Fact]
    public void TaskJsonActionModel_RoundTripsIntoActionDefinition()
    {
        const string taskJson =
            """
            {
              "description": "t",
              "dependsOn": [],
              "action": { "model": "claude-haiku-4-5" }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Equal("claude-haiku-4-5", task.Action.Model);
    }

    [Fact]
    public void TaskJsonWithoutActionModel_LeavesActionDefinitionModelNull()
    {
        const string taskJson = """{ "description": "t", "dependsOn": [] }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Null(task.Action.Model);
    }

    // --- GR2030 validation: promptRunners.<name>.model ---------------------------------------

    [Fact]
    public void EmptyRunnerModel_IsGR2030Error()
    {
        const string json =
            """
            { "version": 1, "promptRunners": { "default": "claude", "claude": { "command": "claude", "model": "" } } }
            """;
        PlanLoadResult result = new PlanLoader().Load(
            PlanWith(json, """{ "description": "t", "dependsOn": [] }"""));
        Assert.False(result.HasErrors, "loading should still succeed structurally");

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Diagnostic error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("promptRunners.claude.model", error.Message);
    }

    [Fact]
    public void WhitespaceOnlyRunnerModel_IsGR2030Error()
    {
        const string json =
            """
            { "version": 1, "promptRunners": { "default": "claude", "claude": { "command": "claude", "model": "   " } } }
            """;
        PlanLoadResult result = new PlanLoader().Load(
            PlanWith(json, """{ "description": "t", "dependsOn": [] }"""));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
    }

    // --- GR2030 validation: promptRunners.<name>.guardrailOverrides.model -------------------

    [Fact]
    public void EmptyGuardrailOverrideModel_IsGR2030Error()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "model": "claude-sonnet-5",
                  "guardrailOverrides": { "model": "  " }
                }
              }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(
            PlanWith(json, """{ "description": "t", "dependsOn": [] }"""));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Diagnostic error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
        Assert.Contains("guardrailOverrides.model", error.Message);
    }

    // --- GR2030 validation: task.json action.model ------------------------------------------

    [Fact]
    public void EmptyTaskActionModel_IsGR2030Error()
    {
        const string taskJson =
            """
            { "description": "t", "dependsOn": [], "action": { "model": "" } }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));
        Assert.False(result.HasErrors, "loading should still succeed structurally");

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Diagnostic error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
        Assert.Contains("action.model", error.Message);
        Assert.Contains("01-task", error.Message);
    }

    [Fact]
    public void WhitespaceOnlyTaskActionModel_IsGR2030Error()
    {
        const string taskJson =
            """
            { "description": "t", "dependsOn": [], "action": { "model": "   " } }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
    }

    [Fact]
    public void EmbeddedWhitespaceTaskActionModel_IsGR2030Error()
    {
        const string taskJson =
            """
            { "description": "t", "dependsOn": [], "action": { "model": "claude sonnet 5" } }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
    }

    // --- Negative cases: valid or absent models never fire GR2030 --------------------------

    [Fact]
    public void ValidNonEmptyModels_DoNotFireGR2030()
    {
        const string taskJson =
            """
            { "description": "t", "dependsOn": [], "action": { "model": "claude-haiku-4-5" } }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(RunnerConfigJson, taskJson));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
    }

    [Fact]
    public void AbsentModel_AtAllThreeSites_DoesNotFireGR2030()
    {
        const string json =
            """
            { "version": 1, "promptRunners": { "default": "claude", "claude": { "command": "claude" } } }
            """;
        const string taskJson = """{ "description": "t", "dependsOn": [] }""";

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, taskJson));

        var diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.ModelInvalid);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
