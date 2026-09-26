using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 stage 1 — the claude-gateway keys load, a claude block with a <c>baseUrl</c> is a gateway block, and the
/// registry hands that block's gateway configuration to its <see cref="ClaudePromptRunner"/> instance (D3).
/// </summary>
public sealed class ClaudeGatewayConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-gw-cfg-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void GatewayKeys_RoundTrip_AndMakeTheBlockAGateway()
    {
        PlanDefinition plan = Load("""
            {
              "version": 1, "maxParallelism": 1,
              "promptRunners": {
                "default": "qwen36",
                "qwen36": {
                  "kind": "claude", "baseUrl": "http://127.0.0.1:4000/", "authTokenEnv": "LITELLM_KEY",
                  "model": "Qwen", "contextTokens": 32768, "backendModel": "qwen3.6-35b-a3b"
                },
                "claude": { }
              }
            }
            """);

        PromptRunnerConfig gateway = plan.Config.PromptRunners["qwen36"];
        Assert.Equal("http://127.0.0.1:4000/", gateway.BaseUrl);
        Assert.Equal("LITELLM_KEY", gateway.AuthTokenEnv);
        Assert.Equal("qwen3.6-35b-a3b", gateway.BackendModel);
        Assert.Equal(32768, gateway.ContextTokens);
        Assert.True(gateway.IsClaudeGateway);

        Assert.False(plan.Config.PromptRunners["claude"].IsClaudeGateway);
        Assert.Null(plan.Config.PromptRunners["claude"].BaseUrl);
    }

    [Fact]
    public void GatewayKeysUnderGuardrailOverrides_AreRecorded_NotHonoured()
    {
        PlanDefinition plan = Load("""
            {
              "version": 1, "maxParallelism": 1,
              "promptRunners": {
                "qwen": {
                  "baseUrl": "http://127.0.0.1:4000", "model": "Qwen",
                  "guardrailOverrides": { "baseUrl": "http://evil:1", "authTokenEnv": "X", "backendModel": "abcd", "contextTokens": 5 }
                }
              }
            }
            """);

        PromptRunnerConfig block = plan.Config.PromptRunners["qwen"];
        Assert.Equal(["baseUrl", "authTokenEnv", "backendModel", "contextTokens"], block.GatewayKeysInOverrides);
        Assert.Equal("http://127.0.0.1:4000", block.BaseUrl);
    }

    [Fact]
    public void BaseUrlOnANonClaudeBlock_IsNotAGateway()
    {
        var block = new PromptRunnerConfig
        {
            Name = "c", Command = "agent", Kind = PromptRunnerKind.Cursor, Settings = new PromptRunnerSettings(),
            BaseUrl = "http://127.0.0.1:4000"
        };

        Assert.False(block.IsClaudeGateway);
        Assert.Null(ClaudeGatewayConfig.From(block));
    }

    [Fact]
    public void From_NormalizesTheBaseUrl_AndCarriesTheBlockFacts()
    {
        var block = new PromptRunnerConfig
        {
            Name = "qwen", Command = "claude", Settings = new PromptRunnerSettings { Model = "Qwen" },
            BaseUrl = "  http://127.0.0.1:4000// ", AuthTokenEnv = "LITELLM_KEY", ContextTokens = 65536,
            BackendModel = "qwen3.6-35b-a3b"
        };

        ClaudeGatewayConfig gateway = ClaudeGatewayConfig.From(block)!;
        Assert.Equal("qwen", gateway.BlockName);
        Assert.Equal("http://127.0.0.1:4000", gateway.BaseUrl);
        Assert.Equal("LITELLM_KEY", gateway.AuthTokenEnv);
        Assert.Equal("Qwen", gateway.Model);
        Assert.Equal(65536, gateway.ContextTokens);
        Assert.Equal("qwen3.6-35b-a3b", gateway.BackendModel);
    }

    [Fact]
    public void RedactUserInfo_NeverLetsACredentialThrough()
    {
        Assert.Equal("http://gw.example:4000", ClaudeGatewayConfig.RedactUserInfo("http://user:secret@gw.example:4000/"));
        Assert.Equal("http://gw.example:4000", ClaudeGatewayConfig.RedactUserInfo("http://gw.example:4000"));
    }

    [Theory]
    [InlineData("http://localhost:4000", "http://127.0.0.1:4000/")]
    [InlineData("http://[::1]:4000", "http://127.0.0.1:4000")]
    [InlineData("HTTP://LOCALHOST:4000/", "http://127.0.0.1:4000")]
    public void EndpointKey_FoldsTheLoopbackSpellings(string a, string b) =>
        Assert.Equal(ClaudeGatewayConfig.EndpointKey(a), ClaudeGatewayConfig.EndpointKey(b));

    [Fact]
    public void EndpointKey_KeepsDistinctPortsApart() =>
        Assert.NotEqual(ClaudeGatewayConfig.EndpointKey("http://127.0.0.1:4000"), ClaudeGatewayConfig.EndpointKey("http://127.0.0.1:4001"));

    [Fact]
    public void Registry_HandsTheGatewayToThatBlocksInstance_AndNoneToAPlainClaudeBlock()
    {
        PlanDefinition plan = Load("""
            {
              "version": 1, "maxParallelism": 1,
              "promptRunners": {
                "default": "claude",
                "qwen": { "baseUrl": "http://127.0.0.1:4000/", "model": "Qwen" },
                "claude": { }
              }
            }
            """);

        var context = new ClaudeGatewayRunContext { ConfigDirectory = Path.Combine(_root, "cfg") };
        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(
            plan.Config with { GatewayRun = context }, new ProcessRunner());

        var gateway = Assert.IsType<ClaudePromptRunner>(registry.Resolve("qwen"));
        Assert.NotNull(gateway.Gateway);
        Assert.Equal("http://127.0.0.1:4000", gateway.Gateway!.BaseUrl);
        Assert.Equal("Qwen", gateway.Gateway.Model);

        var plain = Assert.IsType<ClaudePromptRunner>(registry.Resolve("claude"));
        Assert.Null(plain.Gateway);
    }

    [Fact]
    public void RunContext_ResolvesAnIdentityAcrossLoopbackSpellings()
    {
        var context = new ClaudeGatewayRunContext
        {
            ConfigDirectory = _root,
            BackendIdentities = new Dictionary<string, string>
            {
                [ClaudeGatewayRunContext.IdentityKey("http://127.0.0.1:4000", "Qwen")] = "http://127.0.0.1:8080 qwen3.6"
            }
        };

        Assert.Equal("http://127.0.0.1:8080 qwen3.6", context.IdentityFor("http://localhost:4000/", "Qwen"));
        Assert.Null(context.IdentityFor("http://127.0.0.1:4000", "Qwen3.8"));
    }

    private PlanDefinition Load(string guardrailsJson)
    {
        string plan = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N")[..8]);
        string taskDir = Path.Combine(plan, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(plan, "guardrails.json"), guardrailsJson);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(plan);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }
}
