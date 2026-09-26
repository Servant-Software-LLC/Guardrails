using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>#782 §3.2 (D2) — the pure backend-identity rules: api_base normalization and the backendModel match rule.</summary>
public sealed class ClaudeGatewayBackendIdentityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1/", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://host/api/V1", "http://host/api")]
    public void NormalizeApiBase_StripsTheTrailingSlashThenV1(string apiBase, string expected) =>
        Assert.Equal(expected, ClaudeGatewayBackendIdentity.NormalizeApiBase(apiBase));

    [Theory]
    [InlineData("qwen3.6-35b-a3b", "Qwen3.6-35B-A3B", null, true)]                                 // (a)
    [InlineData("qwen3.6-35b", null, "/models/Qwen3.6-35B-A3B-Q4_K_M.gguf", true)]                // (b)
    [InlineData("qwen3.8", null, "/models/Qwen3.6-35B-A3B-Q4_K_M.gguf", false)]                   // halt
    [InlineData("qwen", null, "/models/qwen3.6-35b.gguf", true)]                                   // (b), weak
    [InlineData("qwen3.6", "Qwen3.6-35B-A3B", "/models/qwen3.6.gguf", false)]                      // an alias wins: (a) only
    [InlineData("models", null, "/models/qwen3.6.gguf", false)]                                    // the DIRECTORY never matches
    [InlineData("qwen3.6", null, null, false)]                                                     // no evidence, no claim
    [InlineData("qwen3.6", null, @"C:\models\Qwen3.6.gguf", true)]
    public void Matches_TheDesignRules(string declared, string? alias, string? modelPath, bool expected) =>
        Assert.Equal(expected, ClaudeGatewayBackendIdentity.Matches(declared, alias, modelPath));

    [Theory]
    [InlineData("/models/q.gguf", true)]
    [InlineData("q.gguf", true)]
    [InlineData(@"C:\m\q", true)]
    [InlineData("Qwen3.6-35B-A3B", false)]
    public void LooksLikePath(string value, bool expected) =>
        Assert.Equal(expected, ClaudeGatewayBackendIdentity.LooksLikePath(value));

    [Fact]
    public void Describe_PrefersTheAlias_ElseTheBasename()
    {
        Assert.Equal("http://b Qwen", ClaudeGatewayBackendIdentity.Describe("http://b", "Qwen", "/m/x.gguf"));
        Assert.Equal("http://b x.gguf", ClaudeGatewayBackendIdentity.Describe("http://b", null, "/m/x.gguf"));
    }
}
