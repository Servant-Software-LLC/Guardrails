using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// #764 — how a <c>kind: "cursor"</c> block loads and validates: the loader round-trip, the <c>agent</c>
/// command default and its GR2009 PATH probe (runner command not on PATH), GR2044 (a kind with no
/// implementation) staying silent for it, and GR2080 (a cursor block runs without a tool allowlist or
/// containment hook), which must name every Claude-only key the block declares.
/// </summary>
public sealed class CursorRunnerValidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-cursor-val-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void TheCodeIsGr2080() => Assert.Equal("GR2080", DiagnosticCodes.CursorRunnerUngoverned);

    [Fact]
    public void CursorKind_RoundTrips_AndDefaultsItsCommandToAgent()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": { "default": "fallback", "fallback": { "kind": "cursor", "model": "gpt-5" } }
            }
            """);

        PromptRunnerConfig runner = loaded.Runner("fallback");
        Assert.Equal(PromptRunnerKind.Cursor, runner.Kind);
        Assert.Equal("cursor", PromptRunnerKinds.Token(runner.Kind));

        // Not the block NAME ("fallback"): a cursor block runs Cursor's binary whatever it is called.
        Assert.Equal(CursorPromptRunner.DefaultCommand, runner.Command);
        Assert.Equal("agent", runner.Command);
        Assert.Equal("gpt-5", runner.Settings.Model);
    }

    [Fact]
    public void ExplicitCommand_OnACursorBlock_IsHonoured()
    {
        Loaded loaded = Load("""
            { "version": 1, "promptRunners": { "cursor": { "kind": "cursor", "command": "/opt/cursor/agent" } } }
            """);

        Assert.Equal("/opt/cursor/agent", loaded.Runner("cursor").Command);
    }

    [Fact]
    public void ClaudeBlock_StillDefaultsItsCommandToTheBlockName()
    {
        Loaded loaded = Load("""{ "version": 1, "promptRunners": { "claude": { } } }""");

        Assert.Equal("claude", loaded.Runner("claude").Command);
    }

    /// <summary>GR2044 is for kinds with no runner class — cursor has one, so neither half may fire.</summary>
    [Fact]
    public void CursorKind_IsImplemented_Gr2044DoesNotFire()
    {
        // maxParallelism 1 keeps GR2015 (worktree mode needs a git root) out: the temp plan is not a repo.
        Loaded loaded = Load(
            """{ "version": 1, "maxParallelism": 1, "promptRunners": { "cursor": { "kind": "cursor" } } }""");

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.InvalidPromptRunnerKind);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// The GR2044 message for a still-reserved kind lists what this build CAN serve — which now includes
    /// cursor, so an operator reaching for Codex is shown the Cursor route.
    /// </summary>
    [Fact]
    public void Gr2044ForAReservedKind_ListsCursorAsImplemented()
    {
        Loaded loaded = Load("""{ "version": 1, "promptRunners": { "primary": { "kind": "codex", "command": "codex" } } }""");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.InvalidPromptRunnerKind);
        Assert.Contains("'cursor'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2009_ProbesAgent_ForACursorBlockWithNoCommand()
    {
        Loaded missing = Load("""{ "version": 1, "promptRunners": { "cursor": { "kind": "cursor" } } }""",
            FakeExecutableProbe.With("claude"));
        Diagnostic warning = Assert.Single(missing.Diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
        Assert.Contains("'agent'", warning.Message, StringComparison.Ordinal);

        Loaded present = Load("""{ "version": 1, "promptRunners": { "cursor": { "kind": "cursor" } } }""",
            FakeExecutableProbe.With("agent"));
        Assert.DoesNotContain(present.Diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
    }

    /// <summary>
    /// The #764 operator's exact move: a Claude block copied and flipped to <c>kind: "cursor"</c>. Every
    /// Claude-only key it carries — base and <c>guardrailOverrides</c> — must be NAMED, because each one
    /// silently stopped applying. Keys Cursor does honour (<c>model</c>, <c>extraArgs</c>, <c>env</c>) must not.
    /// </summary>
    [Fact]
    public void Gr2080_NamesEveryIgnoredKeyTheBlockDeclares()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "cursor",
                "cursor": {
                  "kind": "cursor",
                  "model": "gpt-5",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Edit", "Bash(dotnet *)"],
                  "maxTurns": 50,
                  "maxOutputTokens": 32000,
                  "extraArgs": ["--sandbox", "enabled"],
                  "env": { "X": "1" },
                  "guardrailOverrides": { "permissionMode": "default", "allowedTools": ["Read"], "maxTurns": 20 }
                }
              }
            }
            """);

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorRunnerUngoverned);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("promptRunners.cursor", warning.Message, StringComparison.Ordinal);
        Assert.Contains("--force", warning.Message, StringComparison.Ordinal);
        Assert.Contains("git-diff", warning.Message, StringComparison.Ordinal);

        string declared = warning.Message[warning.Message.IndexOf("This block declares", StringComparison.Ordinal)..];
        foreach (string key in new[]
                 {
                     "'permissionMode'", "'allowedTools'", "'maxTurns'", "'maxOutputTokens'",
                     "'guardrailOverrides.permissionMode'", "'guardrailOverrides.allowedTools'",
                     "'guardrailOverrides.maxTurns'"
                 })
        {
            Assert.Contains(key, declared, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("'model'", declared, StringComparison.Ordinal);
        Assert.DoesNotContain("'extraArgs'", declared, StringComparison.Ordinal);
        Assert.DoesNotContain("'env'", declared, StringComparison.Ordinal);
    }

    /// <summary>A bare cursor block still gets the warning — the full-access grant is true of every one.</summary>
    [Fact]
    public void Gr2080_FiresForABareCursorBlock_AndSaysItDeclaresNone()
    {
        Loaded loaded = Load("""{ "version": 1, "promptRunners": { "cursor": { "kind": "cursor" } } }""");

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorRunnerUngoverned);
        Assert.Contains("declares none of them", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2080_FiresOncePerCursorBlock_AndNeverForClaude()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "maxTurns": 50 },
                "cursor-a": { "kind": "cursor" },
                "cursor-b": { "kind": "cursor", "maxTurns": 10 }
              }
            }
            """);

        Diagnostic[] warnings = [.. loaded.Diagnostics.Where(d => d.Code == DiagnosticCodes.CursorRunnerUngoverned)];
        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, w => w.Message.Contains("promptRunners.cursor-a ", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("promptRunners.cursor-b ", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Message.Contains("promptRunners.claude ", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredSettingsKeys_RecordsOnlyWhatTheJsonDeclared()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "claude": { "maxTurns": 50, "guardrailOverrides": { "allowedTools": ["Read"] } }
              }
            }
            """);

        PromptRunnerConfig runner = loaded.Runner("claude");
        Assert.Equal(["maxTurns", "guardrailOverrides.allowedTools"], runner.DeclaredSettingsKeys);

        // The default-applied settings cannot tell the two apart — which is why the list exists.
        Assert.Equal("acceptEdits", runner.Settings.PermissionMode);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record Loaded(PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public PromptRunnerConfig Runner(string name) => Plan!.Config.PromptRunners[name];
    }

    private Loaded Load(string guardrailsJson, FakeExecutableProbe? probe = null)
    {
        string plan = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N")[..8]);
        string taskDir = Path.Combine(plan, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(plan, "guardrails.json"), guardrailsJson);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(plan);
        List<Diagnostic> diagnostics = [.. result.Diagnostics];
        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(probe ?? FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }
}
