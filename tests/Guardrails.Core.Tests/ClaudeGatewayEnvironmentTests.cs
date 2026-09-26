using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Serializes the test classes that plant FIXED-NAME variables (<c>CLAUDE_CONFIG_DIR</c>,
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c>…) in the test PROCESS's environment. A child inherits
/// the whole process environment, so a parallel test spawning its own child would inherit them too; a
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> collection runs alone, after every
/// parallel collection has finished, which also makes whole-environment byte comparisons stable.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClaudeGatewayProcessEnvironmentCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "claude-gateway-process-environment";
}

/// <summary>
/// #782 §1.1–§1.2, the environment AUTHORITY of a gateway dispatch (D1), asserted on the bytes a real child
/// received — its argv and its whole environment, recorded by <see cref="GatewayFakeClaude"/> — and on the bytes of
/// the one composed settings file:
/// <list type="bullet">
/// <item>every inherited <c>ANTHROPIC_*</c> / <c>CLAUDE_CODE_USE_*</c> / OAuth / host-managed / <c>CLAUDE_CONFIG_DIR</c>
/// variable is scrubbed (and, on Windows only, their case variants), and the owned values are set;</item>
/// <item>the ONLY <c>ANTHROPIC_*</c> names the child sees are the six the harness owns;</item>
/// <item>exactly one <c>--settings</c> reaches the child, carrying the merged containment hooks and nothing a
/// spliced file tried to smuggle in;</item>
/// <item>a real token reaches the child's environment and NO file on disk;</item>
/// <item>a non-gateway block's <c>env</c> (and the harness's own set) can never re-introduce an owned or scrubbed
/// variable into a gateway dispatch;</item>
/// <item>and a plain claude block produces a child byte-identical to the pre-#782 launch formula, hostile
/// environment and all.</item>
/// </list>
/// </summary>
[Collection(ClaudeGatewayProcessEnvironmentCollection.Name)]
public sealed class ClaudeGatewayEnvironmentTests : IDisposable
{
    private const string Gateway = "http://127.0.0.1:4000";
    private const string Model = "Qwen";

    private readonly string _root = Directory.CreateTempSubdirectory("gr782-env-").FullName;
    private readonly GatewayFakeClaude _fake;
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);
    private readonly List<string> _planted = [];

    public ClaudeGatewayEnvironmentTests() => _fake = new GatewayFakeClaude(Path.Combine(_root, "fake"));

    public void Dispose()
    {
        // Remove what was planted FIRST, then restore the originals: on Windows a planted lower-case name IS the
        // upper-case variable, so the order is what makes the restore exact.
        foreach (string name in _planted)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach ((string name, string? value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>The hostile inherited environment an operator's shell could hand the harness.</summary>
    private static readonly (string Name, string Value)[] HostileInherited =
    [
        ("ANTHROPIC_API_KEY", "canary-inherited-api-key"),
        ("ANTHROPIC_BASE_URL", "http://canary-inherited.invalid"),
        ("ANTHROPIC_MODEL", "claude-opus-canary"),
        ("ANTHROPIC_CUSTOM_HEADERS", "x-canary: inherited"),
        ("ANTHROPIC_DEFAULT_SONNET_MODEL", "claude-sonnet-canary"),
        ("CLAUDE_CODE_USE_BEDROCK", "canary-bedrock"),
        ("CLAUDE_CODE_USE_VERTEX", "canary-vertex"),
        ("CLAUDE_CODE_USE_GR782CANARY", "canary-future-provider"),
        ("CLAUDE_CODE_OAUTH_TOKEN", "canary-oauth"),
        ("CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST", "canary-managed"),
        ("CLAUDE_CONFIG_DIR", "/home/operator/.claude-canary"),
        ("CLAUDE_CODE_MAX_CONTEXT_TOKENS", "987654321"),
        ("GR782_UNRELATED_INHERITED", "kept")
    ];

    // ───────────────────────────── the scrub and the owned set ─────────────────────────────

    [Fact]
    public async Task InheritedAuthority_IsScrubbed_AndTheOnlyAnthropicNamesAreTheOwnedSix()
    {
        Plant(HostileInherited);
        string configDir = Path.Combine(_root, "run-claude-config");

        PromptResult result = await GatewayRunner(new ClaudeGatewayRunContext { ConfigDirectory = configDir })
            .RunAsync(Invocation(new PromptRunnerSettings { Model = Model }), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        FakeClaudeCall call = Assert.Single(_fake.Calls());

        // The exact ANTHROPIC_* set — whatever this machine's own shell exports, only the owned six survive.
        string[] anthropic = [.. call.Env.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL",
                "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL"
            ],
            anthropic);
        Assert.DoesNotContain(call.Env.Keys, k => k.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase));
        Assert.Null(call.Read("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.Null(call.Read("CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST"));

        // The owned values, each from the block — never from the inherited canaries.
        Assert.Equal(Gateway, call.Read(ClaudeGatewayEnvironment.BaseUrl));
        Assert.Equal(ClaudeGatewayEnvironment.PlaceholderToken, call.Read(ClaudeGatewayEnvironment.AuthToken));
        Assert.All(ClaudeGatewayEnvironment.ModelAliasNames, alias => Assert.Equal(Model, call.Read(alias)));
        Assert.Equal("1", call.Read(ClaudeGatewayEnvironment.DisableNonessentialTraffic));
        Assert.Equal(configDir, call.Read(ClaudeGatewayEnvironment.ConfigDir));

        // An owned variable the block does NOT set (no contextTokens here) is still the harness's: an inherited
        // value from the operator's shell never reaches the gateway child.
        Assert.Null(call.Read(ClaudeGatewayEnvironment.MaxContextTokens));

        // Positive control: the scrub is targeted, not a wipe — an unrelated inherited variable passes.
        Assert.Equal("kept", call.Read("GR782_UNRELATED_INHERITED"));
        Assert.DoesNotContain(call.Env.Values, v => v.Contains("canary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InheritedCaseVariants_AreScrubbed_ExactlyWhereTheOsFoldsCase()
    {
        (string Name, string Value)[] variants =
        [
            ("anthropic_api_key", "canary-lower-api-key"),
            ("Anthropic_Base_Url", "http://canary-mixed.invalid"),
            ("claude_code_use_bedrock", "canary-lower-bedrock"),
            ("Claude_Code_OAuth_Token", "canary-mixed-oauth"),
            ("claude_code_provider_managed_by_host", "canary-lower-managed"),
            ("claude_config_dir", "/home/operator/.claude-lower-canary")
        ];
        Plant(variants);

        await GatewayRunner().RunAsync(Invocation(new PromptRunnerSettings { Model = Model }), TestContext.Current.CancellationToken);

        FakeClaudeCall call = Assert.Single(_fake.Calls());
        foreach ((string name, string value) in variants)
        {
            bool reached = call.Env.Values.Contains(value, StringComparer.Ordinal);
            // Windows folds environment names, so Claude Code would read `anthropic_api_key` as ANTHROPIC_API_KEY:
            // it must be scrubbed. POSIX does not fold, so there it is a different variable Claude Code never reads.
            Assert.True(
                reached == !OperatingSystem.IsWindows(),
                $"inherited '{name}' {(reached ? "reached" : "did not reach")} the gateway child on this OS");
        }

        Assert.Equal(Gateway, call.Read(ClaudeGatewayEnvironment.BaseUrl));
        Assert.NotEqual("/home/operator/.claude-lower-canary", call.Read(ClaudeGatewayEnvironment.ConfigDir));
    }

    // ───────────────────────────── cross-block settings cannot re-introduce ─────────────────────────────

    public static TheoryData<bool> Casings => new() { false, true };

    [Theory]
    [MemberData(nameof(Casings))]
    public async Task ANonGatewayBlocksEnv_AndTheHarnessSet_CannotReintroduceAnOwnedOrScrubbedName(bool lowerCase)
    {
        string[] names = [.. ClaudeGatewayEnvironment.OwnedNames.Concat(ClaudeGatewayEnvironment.BlankedNames).Distinct(StringComparer.Ordinal)];
        var plantedEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            plantedEnv[lowerCase ? names[i].ToLowerInvariant() : names[i]] = $"planted-block-{i}";
        }

        RunConfig config = MixedConfig(plainEnv: plantedEnv);
        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(
            config with { GatewayRun = new ClaudeGatewayRunContext { ConfigDirectory = Path.Combine(_root, "cfg") } },
            new ProcessRunner());

        // runnerConfig A (the plain block's settings) routed to gateway runner B — the §1.1 shape.
        string harnessName = lowerCase ? "anthropic_base_url" : "ANTHROPIC_BASE_URL";
        PromptInvocation invocation = Invocation(registry.ResolveConfig("claude").Settings) with
        {
            Environment = new Dictionary<string, string>
            {
                ["GUARDRAILS_TASK_ID"] = "01-task",
                [harnessName] = "planted-harness"
            }
        };

        PromptResult result = await registry.Resolve("qwen").RunAsync(invocation, TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        FakeClaudeCall call = Assert.Single(_fake.Calls());
        Assert.DoesNotContain(call.Env, kv => kv.Value.StartsWith("planted-", StringComparison.Ordinal));
        Assert.Equal(Gateway, call.Read(ClaudeGatewayEnvironment.BaseUrl));
        Assert.All(ClaudeGatewayEnvironment.ModelAliasNames, alias => Assert.Equal(Model, call.Read(alias)));

        // The drops are recorded by NAME — every planted name — and no planted VALUE reaches the log.
        string preamble = File.ReadLines(invocation.StreamLogPath!).First();
        using JsonDocument doc = JsonDocument.Parse(preamble);
        string[] dropped = [.. doc.RootElement.GetProperty("dropped_env").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal([harnessName, .. plantedEnv.Keys], dropped);
        Assert.DoesNotContain("planted-", preamble, StringComparison.Ordinal);
    }

    // ───────────────────────────── the one composed --settings ─────────────────────────────

    [Fact]
    public async Task ExactlyOneSettings_ComposedFile_HasOnlyTheOwnedEnv_AndMergesEverySplicedHook_DroppingAnythingElse()
    {
        string logDir = Path.Combine(_root, "attempt-1");
        Directory.CreateDirectory(logDir);
        string worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        string containment = WorktreeContainmentHook.WriteHookFiles(logDir, worktree);

        // A second spliced settings file that ALSO tries to set routing — only its hooks may survive the merge.
        string smuggler = Path.Combine(_root, "smuggler.settings.json");
        File.WriteAllText(smuggler, """
            {
              "env": { "ANTHROPIC_BASE_URL": "http://smuggled.invalid", "ANTHROPIC_API_KEY": "smuggled-key" },
              "apiKeyHelper": "/bin/smuggled-helper",
              "model": "claude-opus-smuggled",
              "hooks": { "PostToolUse": [ { "matcher": "Write", "hooks": [ { "type": "command", "command": "echo gr782" } ] } ] }
            }
            """);

        PromptInvocation invocation = Invocation(
            new PromptRunnerSettings { Model = Model, ExtraArgs = ["--settings", containment, "--keep-me", "--settings", smuggler] },
            logDir);

        await GatewayRunner(contextTokens: 32768).RunAsync(invocation, TestContext.Current.CancellationToken);

        FakeClaudeCall call = Assert.Single(_fake.Calls());
        Assert.Single(call.Args, a => a.StartsWith("--settings", StringComparison.Ordinal));
        Assert.DoesNotContain(containment, call.Args);
        Assert.DoesNotContain(smuggler, call.Args);
        Assert.Contains("--keep-me", call.Args);
        string composedPath = call.Args[call.Args.ToList().IndexOf("--settings") + 1];
        Assert.Equal("--settings", call.Args[^2]);

        JsonObject composed = JsonNode.Parse(File.ReadAllText(composedPath))!.AsObject();
        Assert.Equal(["env", "fallbackModel", "hooks", "model"], composed.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(Model, composed["model"]!.GetValue<string>());
        Assert.Equal(Model, composed["fallbackModel"]!.GetValue<string>());

        // env = the owned values + every known scrubbed-but-not-owned name blanked — and nothing else.
        var expectedEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeGatewayEnvironment.BaseUrl] = Gateway,
            [ClaudeGatewayEnvironment.AuthToken] = ClaudeGatewayEnvironment.PlaceholderToken,
            [ClaudeGatewayEnvironment.MaxContextTokens] = "32768",
            [ClaudeGatewayEnvironment.DisableNonessentialTraffic] = "1",
            [ClaudeGatewayEnvironment.ConfigDir] = call.Read(ClaudeGatewayEnvironment.ConfigDir)!
        };
        foreach (string alias in ClaudeGatewayEnvironment.ModelAliasNames)
        {
            expectedEnv[alias] = Model;
        }

        foreach (string blanked in ClaudeGatewayEnvironment.BlankedNames)
        {
            expectedEnv[blanked] = string.Empty;
        }

        Dictionary<string, string> actualEnv = composed["env"]!.AsObject()
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal(expectedEnv.OrderBy(p => p.Key, StringComparer.Ordinal), actualEnv.OrderBy(p => p.Key, StringComparer.Ordinal));

        // hooks = both spliced files' hooks, in splice order, verbatim.
        JsonObject hooks = composed["hooks"]!.AsObject();
        JsonObject containmentHooks = JsonNode.Parse(File.ReadAllText(containment))!["hooks"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(containmentHooks["PreToolUse"], hooks["PreToolUse"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(smuggler))!["hooks"]!["PostToolUse"], hooks["PostToolUse"]));

        string text = File.ReadAllText(composedPath);
        Assert.DoesNotContain("smuggled", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableSplicedSettingsFile_RefusesBeforeLaunch_RatherThanDroppingTheHook()
    {
        string broken = Path.Combine(_root, "broken.settings.json");
        File.WriteAllText(broken, "{ this is not json");

        PromptResult result = await GatewayRunner().RunAsync(
            Invocation(new PromptRunnerSettings { Model = Model, ExtraArgs = ["--settings", broken] }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.EndsWith($"(via gateway {Gateway})", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_fake.Calls());
    }

    [Fact]
    public async Task AGatewayBlockWithNoModel_RefusesBeforeLaunch()
    {
        var runner = new ClaudePromptRunner("qwen", _fake.Command, new ProcessRunner(),
            new ClaudeGatewayConfig { BlockName = "qwen", BaseUrl = Gateway, Model = null },
            new ClaudeGatewayRunContext { ConfigDirectory = Path.Combine(_root, "cfg") });

        PromptResult result = await runner.RunAsync(Invocation(new PromptRunnerSettings()), TestContext.Current.CancellationToken);

        Assert.False(result.Completed);
        Assert.Equal(PromptFailureKind.RunnerConfiguration, result.FailureKind);
        Assert.Contains("GR2084", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_fake.Calls());
    }

    // ───────────────────────────── the token never touches disk ─────────────────────────────

    [Fact]
    public async Task ARealToken_ReachesOnlyTheChildEnvironment_NeverAnyFileOrArgument()
    {
        string variable = "GR782_REAL_TOKEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        const string secret = "sk-gr782-real-secret-token-value";
        Plant([(variable, secret)]);
        string logDir = Path.Combine(_root, "attempt-token");
        string configDir = Path.Combine(_root, "run-config-token");

        PromptResult result = await GatewayRunner(new ClaudeGatewayRunContext { ConfigDirectory = configDir }, authTokenEnv: variable)
            .RunAsync(Invocation(new PromptRunnerSettings { Model = Model }, logDir), TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        FakeClaudeCall call = Assert.Single(_fake.Calls());
        Assert.Equal(secret, call.Read(ClaudeGatewayEnvironment.AuthToken));
        Assert.DoesNotContain(call.Args, a => a.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, result.Summary, StringComparison.Ordinal);

        // Every byte the dispatch wrote — the composed settings, the stream log and its preamble, the transcript,
        // the config directory — is free of the secret. (The fake's own recording of its env is excluded: that
        // file IS the child's environment, the one place the token is meant to be.)
        foreach (string file in Directory.EnumerateFiles(logDir, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories)))
        {
            Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(logDir, "claude-stream" + ClaudeGatewayLaunch.SettingsFileSuffix)));
    }

    // ───────────────────────────── a plain block is untouched ─────────────────────────────

    [Fact]
    public async Task APlainClaudeBlock_InAMixedRun_LaunchesByteIdenticallyToThePre782Formula()
    {
        Plant(HostileInherited);
        string runConfigDir = Path.Combine(_root, "run-claude-config");
        Directory.CreateDirectory(runConfigDir);

        RunConfig config = MixedConfig(plainEnv: new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "http://operator-proxy.example" });
        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(
            config with { GatewayRun = new ClaudeGatewayRunContext { ConfigDirectory = runConfigDir } },
            new ProcessRunner());

        IPromptRunner plain = registry.Resolve("claude");
        Assert.Null(Assert.IsType<ClaudePromptRunner>(plain).Gateway);

        string logDir = Path.Combine(_root, "attempt-plain");
        PromptInvocation invocation = Invocation(registry.ResolveConfig("claude").Settings, logDir);
        PromptResult result = await plain.RunAsync(invocation, TestContext.Current.CancellationToken);

        // The pre-#782 launch, spelled out: the same executable, BuildArguments, BuildEnvironment, no scrub.
        await new ProcessRunner().RunAsync(
            new ResolvedCommand { Executable = _fake.Command, Arguments = ClaudePromptRunner.BuildArguments(invocation) },
            invocation.WorkingDirectory,
            ClaudePromptRunner.BuildEnvironment(invocation),
            TimeSpan.FromMinutes(2),
            standardInput: invocation.ComposedPrompt,
            stdoutLineSink: null,
            TestContext.Current.CancellationToken);

        IReadOnlyList<FakeClaudeCall> calls = _fake.Calls();
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[1].Args, calls[0].Args);
        Assert.Equal(
            calls[1].Env.OrderBy(p => p.Key, StringComparer.Ordinal),
            calls[0].Env.OrderBy(p => p.Key, StringComparer.Ordinal));

        // Positive controls: the hostile inherited set and the block's own ANTHROPIC_BASE_URL DID reach the plain
        // child — so the equality above is not two equally-scrubbed children agreeing.
        Assert.Equal("canary-inherited-api-key", calls[0].Read("ANTHROPIC_API_KEY"));
        Assert.Equal("/home/operator/.claude-canary", calls[0].Read("CLAUDE_CONFIG_DIR"));
        Assert.Equal("http://operator-proxy.example", calls[0].Read("ANTHROPIC_BASE_URL"));

        // And none of the gateway artifacts appeared: no composed settings, no config dir use, no preamble, no suffix.
        Assert.DoesNotContain("--settings", calls[0].Args);
        Assert.Empty(Directory.EnumerateFiles(logDir, "*" + ClaudeGatewayLaunch.SettingsFileSuffix));
        Assert.False(Directory.Exists(Path.Combine(logDir, "claude-config")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runConfigDir));
        Assert.DoesNotContain("guardrails_gateway", File.ReadLines(invocation.StreamLogPath!).First(), StringComparison.Ordinal);
        Assert.Equal(GatewayFakeClaude.ReportedCost, result.CostUsd);
        Assert.Null(result.Gateway);
        Assert.DoesNotContain("via gateway", result.Summary, StringComparison.Ordinal);
    }

    // ───────────────────────────── harness ─────────────────────────────

    /// <summary>Plant variables in the test PROCESS (the child inherits them), remembering the originals.</summary>
    private void Plant(IEnumerable<(string Name, string Value)> variables)
    {
        foreach ((string name, string value) in variables)
        {
            // Save both spellings: on Windows the lower-case name and the upper-case one are one variable.
            foreach (string spelling in new[] { name, name.ToUpperInvariant() })
            {
                if (!_saved.ContainsKey(spelling))
                {
                    _saved[spelling] = Environment.GetEnvironmentVariable(spelling);
                    Environment.SetEnvironmentVariable(spelling, null);
                }
            }
        }

        foreach ((string name, string value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
            _planted.Add(name);
        }
    }

    private ClaudePromptRunner GatewayRunner(
        ClaudeGatewayRunContext? context = null, int? contextTokens = null, string? authTokenEnv = null) =>
        new("qwen", _fake.Command, new ProcessRunner(),
            new ClaudeGatewayConfig
            {
                BlockName = "qwen", BaseUrl = Gateway, Model = Model, ContextTokens = contextTokens, AuthTokenEnv = authTokenEnv
            },
            context ?? new ClaudeGatewayRunContext { ConfigDirectory = Path.Combine(_root, "run-config") });

    /// <summary>A plan's worth of runner config: a plain claude block and a gateway block, both on the fake.</summary>
    private RunConfig MixedConfig(IReadOnlyDictionary<string, string> plainEnv)
    {
        var runners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
        {
            ["claude"] = new()
            {
                Name = "claude",
                Command = _fake.Command,
                Settings = new PromptRunnerSettings { Model = "claude-sonnet-4-5", Env = plainEnv }
            },
            ["qwen"] = new()
            {
                Name = "qwen",
                Command = _fake.Command,
                Settings = new PromptRunnerSettings { Model = Model },
                BaseUrl = Gateway
            }
        };

        return new RunConfig
        {
            Version = 1,
            DefaultPromptRunner = "claude",
            PromptRunners = runners,
            PromptRunnerNames = runners.Keys.ToHashSet(StringComparer.Ordinal)
        };
    }

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
}
