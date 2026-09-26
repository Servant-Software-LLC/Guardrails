using System.Net;
using System.Net.Sockets;
using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests.ClaudeGateway;

/// <summary>
/// #782 stage 4 — the claude-gateway preflight driven through the REAL <see cref="PlanPreflightPhase.EvaluateAsync"/>
/// against loopback fakes of a LiteLLM gateway and a <c>llama-server</c> backend. Halts are read off
/// <c>state/run.json</c>; proceeds and "once per key" are counted at the listener, never from the code under test.
/// </summary>
public sealed class ClaudeGatewayPreflightPhaseTests : IDisposable
{
    private readonly string _planDir = Path.Combine(Path.GetTempPath(), "gr-gw-preflight-" + Guid.NewGuid().ToString("N"));

    public ClaudeGatewayPreflightPhaseTests() => Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task NoGatewayBlock_OpensZeroConnections()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        PlanDefinition plan = Load("""
            "claude": { "model": "claude-sonnet-4-5" }
            """);

        Assert.True(await RunAsync(plan));
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Fact]
    public async Task HealthyGateway_ResolvesTheBackend_ViaNormalizedApiBase_OneProbePerKey()
    {
        await using FakeGatewayServer backend = Backend(modelPath: "/models/Qwen3.6-35B-A3B-Q4_K_M.gguf", nCtx: 32768);
        await using FakeGatewayServer gateway = Gateway(backend, "Qwen");

        // Two blocks, two spellings of one gateway, one model: one listing, one messages probe, one identity.
        PlanDefinition plan = Load($$"""
            "default": "a",
            "a": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen", "backendModel": "qwen3.6-35b", "contextTokens": 32768 },
            "b": { "baseUrl": "http://localhost:{{gateway.Port}}/", "model": "Qwen" }
            """);

        var options = Options();
        var console = new StringWriter();
        Assert.True(await RunAsync(plan, options, console), console.ToString());

        Assert.Equal(1, gateway.Count("GET /v1/models"));
        Assert.Equal(1, gateway.Count("POST /v1/messages"));
        Assert.Equal(1, gateway.Count("GET /model/info"));
        Assert.Equal(1, backend.Count("GET /props"));
        Assert.Equal(0, backend.Count("GET /v1/props"));

        string identity = Assert.Single(options.ResolvedIdentities.Values);
        Assert.Equal($"{backend.BaseUrl} Qwen3.6-35B-A3B-Q4_K_M.gguf", identity);
        Assert.Contains("backendModel 'qwen3.6-35b' matched", console.ToString(), StringComparison.Ordinal);

        FakeRequest messages = gateway.Requests.First(r => r.Key == "POST /v1/messages");
        Assert.Equal($"Bearer {ClaudeGatewayEnvironment.PlaceholderToken}", messages.Headers["Authorization"]);
        Assert.False(messages.Headers.ContainsKey("x-api-key"));
        Assert.Contains("\"max_tokens\":256", messages.Body, StringComparison.Ordinal);
        Assert.Null(HaltOf(plan));
    }

    [Theory]
    [InlineData("qwen3.6-35b-a3b", "Qwen3.6-35B-A3B", null, true)]
    [InlineData("qwen3.6-35b", null, "/models/Qwen3.6-35B-A3B-Q4_K_M.gguf", true)]
    [InlineData("qwen3.8", null, "/models/Qwen3.6-35B-A3B-Q4_K_M.gguf", false)]
    [InlineData("qwen", null, "/models/qwen3.6-35b.gguf", true)]
    public async Task BackendModel_MatchRule_EveryDesignRow(string declared, string? alias, string? modelPath, bool proceeds)
    {
        await using FakeGatewayServer backend = Backend(modelPath, nCtx: 65536, alias: alias);
        await using FakeGatewayServer gateway = Gateway(backend, "Qwen");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen", "backendModel": "{{declared}}" }
            """);

        Assert.Equal(proceeds, await RunAsync(plan));
        if (!proceeds)
        {
            RunHalt halt = HaltOf(plan)!;
            Assert.Equal(RunHaltKind.PlanPreflightFailed, halt.Kind);
            Assert.StartsWith(ClaudeGatewayPreflight.HeadlinePrefix, halt.Headline, StringComparison.Ordinal);
            Assert.Contains(halt.FailedChecks!, c => c.Reason.Contains($"backendModel '{declared}'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TwoModelsResolvingToOneLoadedModel_Halts()
    {
        await using FakeGatewayServer backend = Backend("/models/Qwen3.6-35B-A3B.gguf", nCtx: 65536);
        await using FakeGatewayServer gateway = Gateway(backend, "Qwen", "Qwen3.8");
        PlanDefinition plan = Load($$"""
            "default": "q36",
            "q36": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" },
            "q38": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen3.8" }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains("resolve to ONE loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContextTokensAboveThePerSlotWindow_Halts()
    {
        await using FakeGatewayServer backend = Backend("/models/q.gguf", nCtx: 16384);
        await using FakeGatewayServer gateway = Gateway(backend, "Qwen");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen", "contextTokens": 32768 }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains("per-slot n_ctx of 16384", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnresolvableIdentity_IsDisclosedAsUnverified_NeverMatched()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (200, Content());
        // No /model/info route: not LiteLLM, so nothing is claimed.
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen", "backendModel": "qwen3.6" }
            """);

        var options = Options();
        var console = new StringWriter();
        Assert.True(await RunAsync(plan, options, console));

        Assert.Empty(options.ResolvedIdentities);
        Assert.Contains("backend identity unverified", console.ToString(), StringComparison.Ordinal);
        Assert.Contains("backendModel 'qwen3.6' declared, not verified", console.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("matched", console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouterModeBackend_WithSeveralModels_StaysUnverified()
    {
        await using FakeGatewayServer backend = FakeGatewayServer.Start();
        backend.Routes["GET /v1/models"] = (200, """{"data":[{"id":"a"},{"id":"b"}]}""");
        await using FakeGatewayServer gateway = Gateway(backend, "Qwen");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        var options = Options();
        Assert.True(await RunAsync(plan, options));
        Assert.Empty(options.ResolvedIdentities);
    }

    [Fact]
    public async Task UnreachableGateway_Halts()
    {
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "http://127.0.0.1:{{FreePort()}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains("could not be reached", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(401, "refused the credential")]
    [InlineData(403, "refused the credential")]
    [InlineData(500, "reporting itself broken")]
    public async Task ListingFailures_Halt(int status, string expected)
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (status, """{"error":"x"}""");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListingNotOffered_404_DowngradesToAWarning_AndStillProbesMessages()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["POST /v1/messages"] = (200, Content());
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        var console = new StringWriter();
        Assert.True(await RunAsync(plan, Options(), console));
        Assert.Contains("WARNING", console.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, gateway.Count("POST /v1/messages"));
    }

    [Fact]
    public async Task ModelNotListed_Halts_NamingIt()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"other"}]}""");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains("does not list the model 'Qwen'", StringComparison.Ordinal));
        Assert.Equal(0, gateway.Count("POST /v1/messages"));
    }

    [Theory]
    [InlineData(500, """{"error":{"message":"model not found"}}""")]
    [InlineData(200, """{"content":[]}""")]
    public async Task MessagesProbeWithoutAContentBlock_Halts_QuotingTheBody(int status, string body)
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (status, body);
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains(body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthTokenEnv_Unset_HaltsBeforeAnyConnection_Set_IsSentAsBearer()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (200, Content());
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen", "authTokenEnv": "GR_TEST_GATEWAY_KEY" }
            """);

        Assert.False(await RunAsync(plan, Options(env: _ => null)));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains("'GR_TEST_GATEWAY_KEY'", StringComparison.Ordinal));
        Assert.Equal(0, gateway.AcceptedConnections);

        File.Delete(RunJournal.PathFor(plan.PlanDirectory));
        Assert.True(await RunAsync(plan, Options(env: name => name == "GR_TEST_GATEWAY_KEY" ? "sk-local" : null)));
        Assert.All(gateway.Requests, r => Assert.Equal("Bearer sk-local", r.Headers["Authorization"]));
    }

    [Theory]
    [InlineData("""{ "env": { "ANTHROPIC_BASE_URL": "http://evil" } }""", "env.ANTHROPIC_BASE_URL")]
    [InlineData("""{ "env": { "ANTHROPIC_API_KEY": "sk" } }""", "env.ANTHROPIC_API_KEY")]
    [InlineData("""{ "env": { "CLAUDE_CODE_USE_BEDROCK": "1" } }""", "env.CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("""{ "apiKeyHelper": "/bin/key" }""", "'apiKeyHelper'")]
    [InlineData("""{ "fallbackModel": "claude-opus" }""", "'fallbackModel'")]
    [InlineData("""{ "model": "sonnet" }""", "'model'")]
    [InlineData("""{ "availableModels": ["sonnet"] }""", "'availableModels'")]
    [InlineData("""{ "forceLoginMethod": "gateway" }""", "forceLoginMethod")]
    [InlineData("""{ "forceLoginGatewayUrl": "https://gw" }""", "'forceLoginGatewayUrl'")]
    public async Task ManagedSettings_EachFinding_Halts_BeforeAnyConnection(string managed, string expected)
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        string managedPath = Path.Combine(_planDir, "managed-settings.json");
        File.WriteAllText(managedPath, managed);
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan, Options(managed: [managedPath])));
        Assert.Contains(HaltOf(plan)!.FailedChecks!, c => c.Reason.Contains(expected, StringComparison.Ordinal)
                                                         && c.Name.Contains(managedPath, StringComparison.Ordinal));
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Fact]
    public async Task ManagedSettings_ForceLoginMethodOtherThanGateway_IsFine()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, """{"data":[{"id":"Qwen"}]}""");
        gateway.Routes["POST /v1/messages"] = (200, Content());
        string managedPath = Path.Combine(_planDir, "managed-settings.json");
        File.WriteAllText(managedPath, """{ "forceLoginMethod": "claudeai", "env": { "BASH_MAX_TIMEOUT_MS": "1" } }""");
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.True(await RunAsync(plan, Options(managed: [managedPath])));
    }

    [Theory]
    [InlineData("settings.json", """{ "env": { "ANTHROPIC_API_KEY": "sk-canary" } }""", "env.ANTHROPIC_API_KEY")]
    [InlineData("settings.local.json", """{ "env": { "ANTHROPIC_API_KEY": "sk-canary" } }""", "env.ANTHROPIC_API_KEY")]
    [InlineData("settings.json", """{ "env": { "CLAUDE_CODE_USE_BEDROCK": "1" } }""", "env.CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("settings.local.json", """{ "env": { "CLAUDE_CODE_USE_BEDROCK": "1" } }""", "env.CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("settings.json", """{ "env": { "anthropic_base_url": "http://evil" } }""", "env.anthropic_base_url")]
    [InlineData("settings.json", """{ "apiKeyHelper": "/bin/key" }""", "'apiKeyHelper'")]
    [InlineData("settings.local.json", """{ "apiKeyHelper": "/bin/key" }""", "'apiKeyHelper'")]
    [InlineData("settings.json", """{ "forceLoginMethod": "console" }""", "'forceLoginMethod'")]
    [InlineData("settings.local.json", """{ "forceLoginMethod": "console" }""", "'forceLoginMethod'")]
    [InlineData("settings.json", """{ "forceLoginGatewayUrl": "https://gw" }""", "'forceLoginGatewayUrl'")]
    [InlineData("settings.local.json", """{ "forceLoginGatewayUrl": "https://gw" }""", "'forceLoginGatewayUrl'")]
    public async Task ProjectSettings_EachKeyInEitherFile_Halts_NamingTheFileAndKey(string file, string json, string expected)
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        Directory.CreateDirectory(Path.Combine(_planDir, ".claude"));
        File.WriteAllText(Path.Combine(_planDir, ".claude", file), json);
        PlanDefinition plan = Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        Assert.False(await RunAsync(plan));
        PlanPreflightCheck check = Assert.Single(JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)).PlanPreflights!.Checks);
        Assert.Contains(file, check.Reason!, StringComparison.Ordinal);
        Assert.Contains(expected, check.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-canary", check.Reason!, StringComparison.Ordinal);
        Assert.Equal(0, gateway.AcceptedConnections);
    }

    [Fact]
    public void MaxCostNote_DoesNotBind_OrBindsPartially()
    {
        RunConfig allGateway = Config(("q", "http://127.0.0.1:4000")) with { MaxCostUsd = 5m };
        Assert.Contains("does NOT bind", ClaudeGatewayPreflight.MaxCostNote(allGateway), StringComparison.Ordinal);

        RunConfig mixed = Config(("q", "http://127.0.0.1:4000"), ("claude", null)) with { MaxCostUsd = 5m };
        string note = ClaudeGatewayPreflight.MaxCostNote(mixed)!;
        Assert.Contains("PARTIALLY", note, StringComparison.Ordinal);
        Assert.Contains("'claude'", note, StringComparison.Ordinal);

        Assert.Null(ClaudeGatewayPreflight.MaxCostNote(allGateway with { MaxCostUsd = null }));
    }

    [Fact]
    public async Task ProvidersCheck_OnAGatewayBlock_RunsAToolUseRoundTrip()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["POST /v1/messages"] =
            (200, """{"content":[{"type":"tool_use","id":"toolu_1","name":"probe_tool","input":{}}]}""");
        Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        var io = new StringConsoleIo();
        int exit = await CommandFactory.BuildRootCommand(io).Parse(["providers", "check", _planDir, "q"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("tool_use (step 1): met", io.OutText, StringComparison.Ordinal);
        Assert.Contains("tool_result (step 2): met", io.OutText, StringComparison.Ordinal);
        FakeRequest[] posts = [.. gateway.Requests.Where(r => r.Key == "POST /v1/messages")];
        Assert.Equal(2, posts.Length);
        Assert.Contains("\"tools\"", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"tool_result\"", posts[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvidersCheck_OnAGatewayBlock_ReportsANoToolCallAsUnmet()
    {
        await using FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["POST /v1/messages"] = (200, Content());
        Load($$"""
            "q": { "baseUrl": "{{gateway.BaseUrl}}", "model": "Qwen" }
            """);

        var io = new StringConsoleIo();
        int exit = await CommandFactory.BuildRootCommand(io).Parse(["providers", "check", _planDir, "q"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("tool_use (step 1): UNMET", io.OutText, StringComparison.Ordinal);
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static string Content() => """{"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}]}""";

    /// <summary>A fake llama-server backend reporting one loaded model through <c>/props</c>.</summary>
    private static FakeGatewayServer Backend(string? modelPath, int nCtx, string? alias = null)
    {
        FakeGatewayServer backend = FakeGatewayServer.Start();
        string aliasPart = alias is null ? string.Empty : $"\"model_alias\":\"{alias}\",";
        string pathPart = modelPath is null ? string.Empty : $"\"model_path\":\"{modelPath}\",";
        backend.Routes["GET /props"] = (200, $$"""{ {{aliasPart}}{{pathPart}}"default_generation_settings":{"n_ctx":{{nCtx}}},"total_slots":1 }""");
        return backend;
    }

    /// <summary>A fake LiteLLM gateway serving <paramref name="models"/>, each mapped to <paramref name="backend"/> via an <c>api_base</c> ending /v1.</summary>
    private static FakeGatewayServer Gateway(FakeGatewayServer backend, params string[] models)
    {
        FakeGatewayServer gateway = FakeGatewayServer.Start();
        gateway.Routes["GET /v1/models"] = (200, "{\"data\":[" + string.Join(",", models.Select(m => $"{{\"id\":\"{m}\"}}")) + "]}");
        gateway.Routes["POST /v1/messages"] = (200, Content());
        gateway.Routes["GET /model/info"] = (200, "{\"data\":[" + string.Join(",", models.Select(m =>
            $"{{\"model_name\":\"{m}\",\"litellm_params\":{{\"model\":\"openai/{m}\",\"api_base\":\"{backend.BaseUrl}/v1/\"}}}}")) + "]}");
        return gateway;
    }

    private static ClaudeGatewayPreflightOptions Options(
        IReadOnlyList<string>? managed = null, Func<string, string?>? env = null) => new()
    {
        ManagedSettingsPaths = managed ?? [],
        ReadEnvironment = env ?? (_ => null)
    };

    private static async Task<bool> RunAsync(
        PlanDefinition plan, ClaudeGatewayPreflightOptions? options = null, TextWriter? console = null)
    {
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        return await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), console, CancellationToken.None, gatewayOptions: options ?? Options());
    }

    private static RunHalt? HaltOf(PlanDefinition plan) =>
        File.Exists(RunJournal.PathFor(plan.PlanDirectory)) ? JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)).Halt : null;

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static RunConfig Config(params (string Name, string? BaseUrl)[] blocks)
    {
        var runners = blocks.ToDictionary(
            b => b.Name,
            b => new PromptRunnerConfig { Name = b.Name, Command = "claude", Settings = new PromptRunnerSettings { Model = "Qwen" }, BaseUrl = b.BaseUrl },
            StringComparer.Ordinal);
        return new RunConfig { Version = 1, PromptRunners = runners, PromptRunnerNames = runners.Keys.ToHashSet() };
    }

    private PlanDefinition Load(string promptRunnersJson)
    {
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), $$"""
            {
              "version": 1, "workspace": ".", "defaultRetries": 0, "maxParallelism": 1,
              "promptRunners": { {{promptRunnersJson}} }
            }
            """);

        PlanLoadResult result = new PlanLoader().Load(_planDir);
        Assert.True(result.Plan is not null, string.Join("\n", result.Diagnostics));
        return result.Plan!;
    }
}
