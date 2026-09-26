using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 stage 2 — a gateway dispatch through the REAL <see cref="ProcessRunner"/> to a fake, OS-picked
/// <c>claude</c> that records its argv and environment: the scrub, the owned values, the placeholder token, the
/// alias and subagent pins, the isolated <c>CLAUDE_CONFIG_DIR</c>, exactly one <c>--settings</c> carrying the merged
/// containment hook, owned keys dropped from another block's settings, the null cost and the gateway summary
/// suffix — and a plain claude block launching exactly as before.
/// </summary>
public sealed class ClaudeGatewayRunnerTests : IDisposable
{
    private const string Gateway = "http://127.0.0.1:4000";

    private static readonly string ResultStream = string.Join("\n",
        """{"type":"system","subtype":"init","model":"Qwen"}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":1.23,"num_turns":2,"usage":{"input_tokens":1000,"output_tokens":200}}""");

    private readonly string _root = Directory.CreateTempSubdirectory("gr-gw-run-").FullName;
    private readonly string _fake;
    private readonly string _canary = "ANTHROPIC_GR782_CANARY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public ClaudeGatewayRunnerTests()
    {
        _fake = WriteFakeClaude(_root, Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "stream.jsonl"), ResultStream + "\n");
        Environment.SetEnvironmentVariable(_canary, "leaked");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_canary, null);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task PlainClaudeBlock_LaunchesExactlyAsBefore_NoScrubNoSettings()
    {
        var runner = new ClaudePromptRunner("claude", _fake, new ProcessRunner());
        PromptInvocation invocation = Invocation(new PromptRunnerSettings { Model = "claude-sonnet-4-5" });

        PromptResult result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(ClaudePromptRunner.BuildArguments(invocation), ReadArgs());
        Dictionary<string, string> env = ReadEnv();
        Assert.Equal("leaked", env[_canary]);                                   // no scrub
        Assert.False(env.ContainsKey(ClaudeGatewayEnvironment.BaseUrl) && env[ClaudeGatewayEnvironment.BaseUrl] == Gateway);
        Assert.Equal(1.23m, result.CostUsd);                                   // the CLI's cost stands
        Assert.Null(result.Gateway);
        Assert.Null(result.BackendModel);
        Assert.DoesNotContain("via gateway", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_SetsTheOwnedValues_ScrubsInherited_AndIsolatesTheConfigDir()
    {
        string configDir = Path.Combine(_root, "claude-config");
        ClaudePromptRunner runner = GatewayRunner(new ClaudeGatewayRunContext { ConfigDirectory = configDir }, contextTokens: 32768);

        await runner.RunAsync(Invocation(new PromptRunnerSettings { Model = "Qwen" }), CancellationToken.None);

        Dictionary<string, string> env = ReadEnv();
        Assert.Equal(Gateway, env[ClaudeGatewayEnvironment.BaseUrl]);
        Assert.Equal(ClaudeGatewayEnvironment.PlaceholderToken, env[ClaudeGatewayEnvironment.AuthToken]);
        foreach (string alias in ClaudeGatewayEnvironment.ModelAliasNames)
        {
            Assert.Equal("Qwen", env[alias]);
        }

        Assert.Equal("Qwen", env["CLAUDE_CODE_SUBAGENT_MODEL"]);
        Assert.Equal("32768", env[ClaudeGatewayEnvironment.MaxContextTokens]);
        Assert.Equal("1", env[ClaudeGatewayEnvironment.DisableNonessentialTraffic]);
        Assert.Equal(configDir, env[ClaudeGatewayEnvironment.ConfigDir]);
        Assert.True(Directory.Exists(configDir));
        Assert.False(env.ContainsKey(_canary), "an inherited ANTHROPIC_* variable reached the gateway child");
    }

    [Fact]
    public async Task Gateway_ScrubsALowercaseInheritedName_OnlyWhereTheOsFoldsCase()
    {
        string lower = _canary.ToLowerInvariant() + "_lc";
        Environment.SetEnvironmentVariable(lower, "leaked-lower");
        try
        {
            await GatewayRunner().RunAsync(Invocation(new PromptRunnerSettings { Model = "Qwen" }), CancellationToken.None);

            bool present = ReadEnv().Keys.Any(k => string.Equals(k, lower, StringComparison.OrdinalIgnoreCase));
            // Windows folds env names, so Claude Code would read it as ANTHROPIC_…: scrubbed. POSIX does not.
            Assert.Equal(!OperatingSystem.IsWindows(), present);
        }
        finally
        {
            Environment.SetEnvironmentVariable(lower, null);
        }
    }

    [Fact]
    public async Task Gateway_PassesExactlyOneSettings_WithEnvModelFallbackAndTheMergedContainmentHook()
    {
        string logDir = Path.Combine(_root, "attempt-1");
        Directory.CreateDirectory(logDir);
        string worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        string hookSettings = WorktreeContainmentHook.WriteHookFiles(logDir, worktree);

        PromptInvocation invocation = Invocation(
            new PromptRunnerSettings { Model = "Qwen", ExtraArgs = ["--verbose-extra", "--settings", hookSettings] },
            logDir);

        await GatewayRunner().RunAsync(invocation, CancellationToken.None);

        IReadOnlyList<string> args = ReadArgs();
        Assert.Single(args, a => a.StartsWith("--settings", StringComparison.Ordinal));
        Assert.Contains("--verbose-extra", args);
        string composed = args[args.ToList().IndexOf("--settings") + 1];
        Assert.NotEqual(hookSettings, composed);
        Assert.Equal(Path.Combine(logDir, "claude-stream" + ClaudeGatewayLaunch.SettingsFileSuffix), composed);

        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(composed));
        JsonElement root = settings.RootElement;
        Assert.Equal("Qwen", root.GetProperty("model").GetString());
        Assert.Equal("Qwen", root.GetProperty("fallbackModel").GetString());
        JsonElement env = root.GetProperty("env");
        Assert.Equal(Gateway, env.GetProperty(ClaudeGatewayEnvironment.BaseUrl).GetString());
        Assert.Equal(ClaudeGatewayEnvironment.PlaceholderToken, env.GetProperty(ClaudeGatewayEnvironment.AuthToken).GetString());
        Assert.Equal(string.Empty, env.GetProperty("ANTHROPIC_API_KEY").GetString());
        Assert.Equal(string.Empty, env.GetProperty("CLAUDE_CODE_USE_BEDROCK").GetString());

        // The containment hook survived the merge — nothing lost, because the hook file only emits hooks.PreToolUse.
        using JsonDocument hook = JsonDocument.Parse(File.ReadAllText(hookSettings));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(hook.RootElement.GetProperty("hooks").GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty("hooks").GetRawText())));
    }

    [Fact]
    public async Task Gateway_DropsOwnedAndScrubbedKeys_ArrivingFromAnotherBlocksSettings_AndLogsTheNames()
    {
        string logDir = Path.Combine(_root, "attempt-2");
        var settings = new PromptRunnerSettings
        {
            Model = "Qwen",
            Env = new Dictionary<string, string>
            {
                ["anthropic_base_url"] = "http://evil.example",
                ["CLAUDE_CODE_USE_BEDROCK"] = "1",
                ["ANTHROPIC_API_KEY"] = "sk-canary",
                ["KEEP_ME"] = "kept"
            }
        };

        await GatewayRunner().RunAsync(Invocation(settings, logDir), CancellationToken.None);

        Dictionary<string, string> env = ReadEnv();
        Assert.Equal(Gateway, env[ClaudeGatewayEnvironment.BaseUrl]);
        Assert.Equal("kept", env["KEEP_ME"]);
        Assert.DoesNotContain(env, kv => kv.Value == "sk-canary" || kv.Value == "http://evil.example");
        Assert.False(env.TryGetValue("CLAUDE_CODE_USE_BEDROCK", out string? bedrock) && bedrock == "1");

        string first = File.ReadLines(Path.Combine(logDir, "claude-stream.jsonl")).First();
        using JsonDocument preamble = JsonDocument.Parse(first);
        Assert.Equal("guardrails_gateway", preamble.RootElement.GetProperty("type").GetString());
        string[] dropped = [.. preamble.RootElement.GetProperty("dropped_env").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(["anthropic_base_url", "CLAUDE_CODE_USE_BEDROCK", "ANTHROPIC_API_KEY"], dropped);
        Assert.DoesNotContain("sk-canary", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_NullsTheCostAtTheSource_KeepsTokens_AndNamesTheGateway()
    {
        PromptResult result = await GatewayRunner().RunAsync(
            Invocation(new PromptRunnerSettings { Model = "Qwen" }), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Null(result.CostUsd);
        Assert.Equal(1000, result.Usage!.InputTokens);
        Assert.Equal(200, result.Usage.OutputTokens);
        Assert.DoesNotContain("cost $", result.Summary, StringComparison.Ordinal);
        Assert.EndsWith($"(via gateway {Gateway})", result.Summary, StringComparison.Ordinal);
        Assert.Equal(Gateway, result.Gateway);
        Assert.Equal(ClaudeGatewayConfig.UnverifiedBackend, result.BackendModel);
    }

    [Fact]
    public async Task Gateway_RecordsTheResolvedBackendIdentity()
    {
        var context = new ClaudeGatewayRunContext
        {
            ConfigDirectory = Path.Combine(_root, "cfg"),
            BackendIdentities = new Dictionary<string, string>
            {
                [ClaudeGatewayRunContext.IdentityKey(Gateway, "Qwen")] = "http://127.0.0.1:8080 Qwen3.6-35B-A3B"
            }
        };

        PromptResult result = await GatewayRunner(context).RunAsync(
            Invocation(new PromptRunnerSettings { Model = "Qwen" }), CancellationToken.None);

        Assert.Equal("http://127.0.0.1:8080 Qwen3.6-35B-A3B", result.BackendModel);
    }

    [Fact]
    public async Task Gateway_ARealToken_ReachesTheChild_ButIsNeverWrittenToTheSettingsFile()
    {
        string variable = "GR782_TOKEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "real-secret-token");
        try
        {
            string logDir = Path.Combine(_root, "attempt-3");
            await GatewayRunner(authTokenEnv: variable).RunAsync(
                Invocation(new PromptRunnerSettings { Model = "Qwen" }, logDir), CancellationToken.None);

            Assert.Equal("real-secret-token", ReadEnv()[ClaudeGatewayEnvironment.AuthToken]);
            string composed = File.ReadAllText(Path.Combine(logDir, "claude-stream" + ClaudeGatewayLaunch.SettingsFileSuffix));
            Assert.DoesNotContain("real-secret-token", composed, StringComparison.Ordinal);
            Assert.DoesNotContain(ClaudeGatewayEnvironment.AuthToken, composed, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Gateway_AnUnsetAuthTokenEnv_RefusesBeforeLaunch()
    {
        PromptResult result = await GatewayRunner(authTokenEnv: "GR782_NEVER_SET_" + Guid.NewGuid().ToString("N")[..6])
            .RunAsync(Invocation(new PromptRunnerSettings { Model = "Qwen" }), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.Contains("unset or empty", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "args.txt")), "the child was launched despite the refusal");
    }

    [Theory]
    [InlineData("--settings=/tmp/x.json")]
    [InlineData("--settings")]
    public async Task Gateway_AUserSettingsFlag_RefusesBeforeLaunch(string flag)
    {
        PromptResult result = await GatewayRunner().RunAsync(
            Invocation(new PromptRunnerSettings { Model = "Qwen", ExtraArgs = [flag] }), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.False(File.Exists(Path.Combine(_root, "args.txt")));
    }

    [Fact]
    public void ApplyEnvironment_Scrub_RemovesEveryInheritedAuthorityName_AndKeepsTheRest()
    {
        var child = new Dictionary<string, string?>(ProcessRunnerComparer())
        {
            ["ANTHROPIC_API_KEY"] = "k",
            ["ANTHROPIC_BASE_URL"] = "http://evil",
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "o",
            ["CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"] = "1",
            ["CLAUDE_CONFIG_DIR"] = "/home/op/.claude",
            ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "1",
            ["PATH"] = "/usr/bin"
        };

        ProcessRunner.ApplyEnvironment(
            child,
            new Dictionary<string, string> { [ClaudeGatewayEnvironment.BaseUrl] = Gateway },
            ClaudeGatewayLaunch.ScrubInherited);

        Assert.Equal(["ANTHROPIC_BASE_URL", "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "PATH"], child.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Gateway, child["ANTHROPIC_BASE_URL"]);
    }

    private static StringComparer ProcessRunnerComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private ClaudePromptRunner GatewayRunner(
        ClaudeGatewayRunContext? context = null, int? contextTokens = null, string? authTokenEnv = null) =>
        new("qwen", _fake, new ProcessRunner(),
            new ClaudeGatewayConfig
            {
                BlockName = "qwen", BaseUrl = Gateway, Model = "Qwen", ContextTokens = contextTokens, AuthTokenEnv = authTokenEnv
            },
            context ?? new ClaudeGatewayRunContext { ConfigDirectory = Path.Combine(_root, "run-config") });

    private PromptInvocation Invocation(PromptRunnerSettings settings, string? logDir = null)
    {
        string dir = logDir ?? Path.Combine(_root, "log-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return new PromptInvocation
        {
            ComposedPrompt = "Do the thing.",
            Role = PromptRole.Action,
            WorkingDirectory = _root,
            PlanDirectory = _root,
            Environment = new Dictionary<string, string> { ["GUARDRAILS_TASK_ID"] = "01-task" },
            Settings = settings,
            Timeout = TimeSpan.FromMinutes(2),
            StreamLogPath = Path.Combine(dir, "claude-stream.jsonl")
        };
    }

    private IReadOnlyList<string> ReadArgs() => File.ReadAllLines(Path.Combine(_root, "args.txt"));

    private Dictionary<string, string> ReadEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(_root, "env.txt")))
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                env[line[..eq]] = line[(eq + 1)..];
            }
        }

        return env;
    }

    /// <summary>
    /// A fake <c>claude</c>: records argv (one per line) to <c>args.txt</c> and its whole environment to
    /// <c>env.txt</c>, drains stdin, replays <c>stream.jsonl</c>, exits 0. OS-picked: a <c>.cmd</c> over PowerShell
    /// on Windows, an executable bash script elsewhere.
    /// </summary>
    private static string WriteFakeClaude(string root, string dir)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.Combine(dir, "claude.ps1");
            string cmd = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(ps1,
                $"$d = '{root}'\r\n" +
                "[IO.File]::WriteAllLines(\"$d\\args.txt\", [string[]]$args)\r\n" +
                "$e = Get-ChildItem env: | ForEach-Object { \"$($_.Name)=$($_.Value)\" }\r\n" +
                "[IO.File]::WriteAllLines(\"$d\\env.txt\", [string[]]$e)\r\n" +
                "$null = [Console]::In.ReadToEnd()\r\n" +
                "foreach ($l in [IO.File]::ReadAllLines(\"$d\\stream.jsonl\")) { [Console]::Out.WriteLine($l) }\r\n" +
                "[Console]::Out.Flush()\r\n" +
                "exit 0\r\n");
            File.WriteAllText(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\nexit /b %ERRORLEVEL%\r\n");
            return cmd;
        }

        string sh = Path.Combine(dir, "claude");
        File.WriteAllText(sh,
            "#!/usr/bin/env bash\n" +
            $"d='{root}'\n" +
            "printf '%s\\n' \"$@\" > \"$d/args.txt\"\n" +
            "env > \"$d/env.txt\"\n" +
            "cat > /dev/null\n" +
            "cat \"$d/stream.jsonl\"\n" +
            "exit 0\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return sh;
    }
}
