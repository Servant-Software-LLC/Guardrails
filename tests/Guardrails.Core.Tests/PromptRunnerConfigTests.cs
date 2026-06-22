using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Config parsing of <c>promptRunners</c> (full configs + guardrailOverrides merge) and the
/// GR2008 "prompt tasks but no runners" validation rule. Builds tiny plan folders on disk.
/// </summary>
public sealed class PromptRunnerConfigTests : IDisposable
{
    private readonly string _root;

    public PromptRunnerConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-prcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string PlanWith(string guardrailsJson, bool promptTask)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);
        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        if (promptTask)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        }
        else
        {
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0\n");
        }

        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");
        return _root;
    }

    [Fact]
    public void FullRunnerConfig_IsParsed()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Edit", "Write"],
                  "maxTurns": 50,
                  "model": "claude-sonnet",
                  "extraArgs": ["--foo"],
                  "guardrailOverrides": {
                    "permissionMode": "default",
                    "allowedTools": ["Read", "Grep"],
                    "maxTurns": 20
                  }
                }
              }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        RunConfig config = result.Plan!.Config;
        Assert.Equal("claude", config.DefaultPromptRunner);
        Assert.Single(config.PromptRunners);

        PromptRunnerConfig claude = config.PromptRunners["claude"];
        Assert.Equal("claude", claude.Command);
        Assert.Equal("acceptEdits", claude.Settings.PermissionMode);
        Assert.Equal(["Read", "Edit", "Write"], claude.Settings.AllowedTools);
        Assert.Equal(50, claude.Settings.MaxTurns);
        Assert.Equal("claude-sonnet", claude.Settings.Model);
        Assert.Equal(["--foo"], claude.Settings.ExtraArgs);
    }

    [Fact]
    public void GuardrailOverrides_MergeOverBaseSettings()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Edit", "Write"],
                  "maxTurns": 50,
                  "model": "base-model",
                  "guardrailOverrides": {
                    "permissionMode": "default",
                    "allowedTools": ["Read"],
                    "maxTurns": 20
                  }
                }
              }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        PromptRunnerConfig claude = result.Plan!.Config.PromptRunners["claude"];

        PromptRunnerSettings action = claude.EffectiveSettings(isGuardrail: false);
        Assert.Equal("acceptEdits", action.PermissionMode);
        Assert.Equal(50, action.MaxTurns);

        PromptRunnerSettings guardrail = claude.EffectiveSettings(isGuardrail: true);
        Assert.Equal("default", guardrail.PermissionMode);          // overridden
        Assert.Equal(["Read"], guardrail.AllowedTools);             // overridden
        Assert.Equal(20, guardrail.MaxTurns);                       // overridden
        Assert.Equal("base-model", guardrail.Model);                // inherited (not in overrides)
    }

    [Fact]
    public void MaxOutputTokens_AndEnv_AreParsed_WithDefaults()
    {
        // #114: maxOutputTokens + env passthrough parse; absent maxOutputTokens defaults above 32k.
        const string json =
            """
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "maxOutputTokens": 96000,
                  "env": { "ANTHROPIC_LOG": "debug" }
                },
                "other": { "command": "other" }
              }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        PromptRunnerConfig claude = result.Plan!.Config.PromptRunners["claude"];
        Assert.Equal(96_000, claude.Settings.MaxOutputTokens);
        Assert.Equal("debug", claude.Settings.Env["ANTHROPIC_LOG"]);

        // An unspecified maxOutputTokens defaults above Claude Code's 32 000 default (issue #114).
        PromptRunnerConfig other = result.Plan!.Config.PromptRunners["other"];
        Assert.Equal(PromptRunnerSettings.DefaultMaxOutputTokens, other.Settings.MaxOutputTokens);
        Assert.True(other.Settings.MaxOutputTokens > 32_000);
        Assert.Empty(other.Settings.Env);
    }

    [Fact]
    public void MaxOutputTokens_GuardrailOverride_IsApplied()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "maxOutputTokens": 96000,
                  "guardrailOverrides": { "maxOutputTokens": 16000 }
                }
              }
            }
            """;

        PromptRunnerConfig claude = new PlanLoader().Load(PlanWith(json, promptTask: true)).Plan!.Config.PromptRunners["claude"];

        Assert.Equal(96_000, claude.EffectiveSettings(isGuardrail: false).MaxOutputTokens);
        Assert.Equal(16_000, claude.EffectiveSettings(isGuardrail: true).MaxOutputTokens);   // tighter verifier profile
    }

    [Fact]
    public void NonPositiveMaxOutputTokens_IsGR2022ValidationError()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude", "maxOutputTokens": 0 } }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        Assert.False(result.HasErrors, "loading should still succeed structurally");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Diagnostic error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.MaxOutputTokensNonPositive);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void RunnerWithoutCommand_DefaultsCommandToName()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "maxTurns": 30 } }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        PromptRunnerConfig claude = result.Plan!.Config.PromptRunners["claude"];

        Assert.Equal("claude", claude.Command);
        Assert.Equal(30, claude.Settings.MaxTurns);
        Assert.Equal("acceptEdits", claude.Settings.PermissionMode); // documented default
        Assert.Null(claude.GuardrailOverrides);
    }

    [Fact]
    public void PromptTaskButNoRunners_IsGR2008ValidationError()
    {
        const string json = """{ "version": 1 }""";
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));
        Assert.False(result.HasErrors, "loading should still succeed structurally");

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.NoPromptRunners);
    }

    [Fact]
    public void ScriptOnlyPlan_NoRunners_IsNotAnError()
    {
        const string json = """{ "version": 1 }""";
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: false));

        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.NoPromptRunners);
    }

    [Fact]
    public void DeclaredRunnerCommandNotOnPath_IsGR2009Warning()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));

        // bash resolves (for the .sh guardrail) but the runner command 'claude' does not.
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.With("bash")).Validate(result.Plan!);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("claude", warning.Message);
    }

    [Fact]
    public void DeclaredRunnerCommandOnPath_ProducesNoGR2009()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));

        // Everything resolves → no prompt-runner warning.
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
    }

    [Fact]
    public void GR2009IsWarningOnly_PlanStillValidatesClean()
    {
        // A missing runner command must NOT fail validation (the plan may run elsewhere).
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.With("bash")).Validate(result.Plan!);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code != DiagnosticCodes.WorkspaceNotGitRoot);
    }

    [Fact]
    public void Registry_BuildsRunnersAndResolvesDefault()
    {
        const string json =
            """
            {
              "version": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """;
        PlanLoadResult result = new PlanLoader().Load(PlanWith(json, promptTask: true));

        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(result.Plan!.Config, new ProcessRunner());

        Assert.Equal("claude", registry.DefaultRunnerName);
        IPromptRunner runner = registry.Resolve(null);
        Assert.Equal("claude", runner.Name);
        Assert.Equal("claude", registry.ResolveConfig(null).Command);
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
