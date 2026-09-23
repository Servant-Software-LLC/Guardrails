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

        // Round 2 (D9): the honest inventory — what is enforced, what is not, and the cost cap that cannot trip.
        Assert.Contains("'maxCostUsd' can never trip", warning.Message, StringComparison.Ordinal);
        Assert.Contains("definition files are hashed", warning.Message, StringComparison.Ordinal);
        Assert.Contains("stale verdict files are deleted", warning.Message, StringComparison.Ordinal);
        Assert.Contains("the plan folder via --add-dir", warning.Message, StringComparison.Ordinal);
        Assert.Contains("never serves the overwatcher", warning.Message, StringComparison.Ordinal);

        int start = warning.Message.IndexOf("This block declares", StringComparison.Ordinal);
        int end = warning.Message.IndexOf("NO effect on a cursor runner.", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, warning.Message);
        string declared = warning.Message[start..end];
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

    // ── #767: approvalMode ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheApprovalCodesAreGr2081AndGr2082()
    {
        Assert.Equal("GR2081", DiagnosticCodes.CursorApprovalModeInvalid);
        Assert.Equal("GR2082", DiagnosticCodes.CursorApprovalFlagInExtraArgs);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("\"force\"", CursorApprovalMode.Force)]
    [InlineData("\"auto-review\"", CursorApprovalMode.AutoReview)]
    [InlineData("\" Auto-Review \"", CursorApprovalMode.AutoReview)]
    [InlineData("\"none\"", CursorApprovalMode.None)]
    public void ApprovalMode_RoundTrips_AndAbsentMeansUnset(string? json, CursorApprovalMode? expected)
    {
        string key = json is null ? string.Empty : $"\"approvalMode\": {json}, ";
        Loaded loaded = Load(
            $$"""{ "version": 1, "maxParallelism": 1, "promptRunners": { "cursor": { {{key}}"kind": "cursor" } } }""");

        Assert.Equal(expected, loaded.Runner("cursor").ApprovalMode);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>An unrecognised mode is REPORTED and never silently served as force (the mode an admin may refuse).</summary>
    [Fact]
    public void Gr2081_AnUnknownApprovalMode_IsAnError_NamingTheLegalValues()
    {
        Loaded loaded = Load("""{ "version": 1, "promptRunners": { "cursor": { "kind": "cursor", "approvalMode": "sandbox" } } }""");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorApprovalModeInvalid);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("'sandbox'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'force', 'auto-review', 'none'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2081_ApprovalModeOnAClaudeBlock_IsAnError()
    {
        Loaded loaded = Load("""{ "version": 1, "promptRunners": { "claude": { "approvalMode": "auto-review" } } }""");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorApprovalModeInvalid);
        Assert.Contains("promptRunners.claude.kind is 'claude'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only a kind 'cursor' block honours", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// GR2082: an approval flag in extraArgs duplicates or contradicts approvalMode. The combination the CLI
    /// itself refuses (--auto-review with --force) is named as such; base and guardrailOverrides are both read;
    /// the <c>--flag=value</c> spelling counts.
    /// </summary>
    [Theory]
    [InlineData(null, "[\"--force\"]", "extraArgs", "DUPLICATES approvalMode 'force'")]
    [InlineData("auto-review", "[\"--force\"]", "extraArgs", "\"pick one\"")]
    [InlineData("auto-review", "[\"--yolo\"]", "extraArgs", "\"pick one\"")]
    [InlineData("force", "[\"--auto-review\"]", "extraArgs", "\"pick one\"")]
    [InlineData("auto-review", "[\"--auto-review\"]", "extraArgs", "DUPLICATES approvalMode 'auto-review'")]
    [InlineData("none", "[\"--sandbox\", \"enabled\", \"--force\"]", "extraArgs", "contradicts approvalMode 'none'")]
    [InlineData("none", "[\"--auto-review=true\"]", "extraArgs", "contradicts approvalMode 'none'")]
    public void Gr2082_AnApprovalFlagInExtraArgs_IsAnError(string? mode, string extraArgs, string key, string expected)
    {
        string modeKey = mode is null ? string.Empty : $"\"approvalMode\": \"{mode}\", ";
        Loaded loaded = Load(
            $$"""{ "version": 1, "promptRunners": { "cursor": { {{modeKey}}"kind": "cursor", "extraArgs": {{extraArgs}} } } }""");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorApprovalFlagInExtraArgs);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains($"promptRunners.cursor.{key} carries", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2082_ReadsGuardrailOverridesExtraArgsToo()
    {
        Loaded loaded = Load("""
            { "version": 1, "promptRunners": { "cursor": {
                "kind": "cursor", "approvalMode": "auto-review",
                "guardrailOverrides": { "extraArgs": ["--force"] } } } }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorApprovalFlagInExtraArgs);
        Assert.Contains("promptRunners.cursor.guardrailOverrides.extraArgs carries '--force'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The sandbox flag is not an approval flag, and a claude block's extraArgs is not GR2082's business.</summary>
    [Fact]
    public void Gr2082_StaysSilent_ForTheSandboxFlag_AndForNonCursorBlocks()
    {
        Loaded loaded = Load("""
            { "version": 1, "maxParallelism": 1, "promptRunners": {
                "default": "cursor",
                "cursor": { "kind": "cursor", "approvalMode": "none", "extraArgs": ["--sandbox", "enabled"] },
                "claude": { "extraArgs": ["--force"] } } }
            """);

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorApprovalFlagInExtraArgs);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>GR2080 now states what the CHOSEN mode grants — honest per mode, not a blanket "--force".</summary>
    [Theory]
    [InlineData(null, "approvalMode 'force' (the default): the harness grants it FULL write and shell access (--force")]
    [InlineData("auto-review", "approvalMode 'auto-review': the harness launches it with --auto-review")]
    [InlineData("none", "Cursor REFUSES EVERY SHELL COMMAND")]
    public void Gr2080_StatesWhatTheApprovalModeGrants(string? mode, string expected)
    {
        string modeKey = mode is null ? string.Empty : $"\"approvalMode\": \"{mode}\", ";
        Loaded loaded = Load($$"""{ "version": 1, "promptRunners": { "cursor": { {{modeKey}}"kind": "cursor" } } }""");

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorRunnerUngoverned);
        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
        if (mode is not null)
        {
            Assert.DoesNotContain("FULL write and shell access (--force", warning.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Gr2080_ForModeNoneWithTheSandbox_SaysShellRunsInsideIt()
    {
        Loaded loaded = Load("""
            { "version": 1, "promptRunners": { "cursor": {
                "kind": "cursor", "approvalMode": "none", "extraArgs": ["--sandbox", "enabled"] } } }
            """);

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.CursorRunnerUngoverned);
        Assert.Contains("shell runs INSIDE Cursor's sandbox", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUSES EVERY SHELL COMMAND", warning.Message, StringComparison.Ordinal);
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
