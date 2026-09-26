using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 stage 3 — GR2084 (ClaudeGatewayBlockInvalid, one test per clause), GR2085 (ClaudeModelNameToGateway, per
/// source and alias form) and GR2086 (ClaudeGatewayModelsShareEndpoint, with the loopback spellings folded).
/// </summary>
public sealed class ClaudeGatewayValidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-gw-val-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void TheCodesAreGr2084ToGr2086()
    {
        Assert.Equal("GR2084", DiagnosticCodes.ClaudeGatewayBlockInvalid);
        Assert.Equal("GR2085", DiagnosticCodes.ClaudeModelNameToGateway);
        Assert.Equal("GR2086", DiagnosticCodes.ClaudeGatewayModelsShareEndpoint);
        Assert.Equal("GR2087", DiagnosticCodes.GuardrailOverridesKeyIgnored);
    }

    [Fact]
    public void AWellFormedGatewayBlock_IsClean()
    {
        IReadOnlyList<Diagnostic> d = Validate(Block("""
            "baseUrl": "http://127.0.0.1:4000", "authTokenEnv": "LITELLM_KEY", "model": "Qwen",
            "contextTokens": 32768, "backendModel": "qwen3.6-35b-a3b"
            """));

        Assert.DoesNotContain(d, x => x.Code is "GR2084" or "GR2085" or "GR2086" or "GR2065");
        Assert.DoesNotContain(d, x => x.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("127.0.0.1:4000", "not an absolute http/https URL")]
    [InlineData("ftp://127.0.0.1:4000", "not an absolute http/https URL")]
    [InlineData("http://user:pw@127.0.0.1:4000", "userinfo")]
    [InlineData("http://127.0.0.1:4000?key=1", "query")]
    [InlineData("http://127.0.0.1:4000/v1", "'/v1'")]
    [InlineData("http://127.0.0.1:4000/v1/", "'/v1'")]
    [InlineData("http://127.0.0.1:4000/V1/messages", "/messages")]
    public void Gr2084_BaseUrlClauses(string baseUrl, string expected) =>
        AssertGr2084(Block($"\"baseUrl\": \"{baseUrl}\", \"model\": \"Qwen\""), expected);

    [Theory]
    [InlineData("sk-ant-api03-abc")]
    [InlineData("1TOKEN")]
    [InlineData("MY KEY")]
    public void Gr2084_AuthTokenEnvMustBeAVariableName(string value) =>
        AssertGr2084(Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", \"authTokenEnv\": \"{value}\""),
            "NAME of an environment variable");

    [Fact]
    public void Gr2084_BackendModelShorterThanFour() =>
        AssertGr2084(Block("\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", \"backendModel\": \"qwn\""),
            "shorter than 4");

    [Fact]
    public void Gr2084_GatewayContextTokensBelowOne() =>
        AssertGr2084(Block("\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", \"contextTokens\": 0"),
            "at least 1");

    [Fact]
    public void Gr2084_GatewayBlockWithNoModel() =>
        AssertGr2084(Block("\"baseUrl\": \"http://127.0.0.1:4000\""), "declares no 'model'");

    [Theory]
    [InlineData("baseUrl", "\"http://127.0.0.1:4000\"")]
    [InlineData("authTokenEnv", "\"LITELLM_KEY\"")]
    [InlineData("backendModel", "\"qwen3.6\"")]
    public void Gr2084_AGatewayKeyOnANonClaudeBlock(string key, string value)
    {
        IReadOnlyList<Diagnostic> d = Validate(
            $$"""{ "version": 1, "maxParallelism": 1, "promptRunners": { "c": { "kind": "cursor", "{{key}}": {{value}} } } }""");

        Diagnostic error = Assert.Single(d, x => x.Code == "GR2084");
        Assert.Contains($"'{key}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'cursor'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2084_AGatewayKeyOnAClaudeBlockWithNoBaseUrl() =>
        AssertGr2084(Block("\"model\": \"Qwen\", \"backendModel\": \"qwen3.6\""), "no 'baseUrl'");

    [Theory]
    [InlineData("baseUrl", "\"http://evil:1\"")]
    [InlineData("authTokenEnv", "\"X\"")]
    [InlineData("backendModel", "\"qwen3.6\"")]
    [InlineData("contextTokens", "8192")]
    public void Gr2084_AGatewayKeyUnderGuardrailOverrides(string key, string value) =>
        AssertGr2084(
            Block($$"""
                "baseUrl": "http://127.0.0.1:4000", "model": "Qwen", "guardrailOverrides": { "{{key}}": {{value}} }
                """),
            $"guardrailOverrides.{key} is not honoured");

    [Theory]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("anthropic_base_url")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_DEFAULT_SONNET_MODEL")]
    [InlineData("CLAUDE_CODE_SUBAGENT_MODEL")]
    [InlineData("CLAUDE_CODE_MAX_CONTEXT_TOKENS")]
    [InlineData("claude_code_disable_nonessential_traffic")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    public void Gr2084_AnOwnedVariableInEnv_CaseInsensitively_OnEveryOs(string name) =>
        AssertGr2084(
            Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", \"env\": {{ \"{name}\": \"0\" }}"),
            $"sets '{name}'");

    [Fact]
    public void Gr2084_AnOwnedVariableInGuardrailOverridesEnv() =>
        AssertGr2084(
            Block("""
                "baseUrl": "http://127.0.0.1:4000", "model": "Qwen", "guardrailOverrides": { "env": { "ANTHROPIC_BASE_URL": "x" } }
                """),
            "guardrailOverrides.env sets 'ANTHROPIC_BASE_URL'");

    [Fact]
    public void Gr2084_AnUnownedVariableInEnv_IsFine()
    {
        IReadOnlyList<Diagnostic> d = Validate(Block(
            "\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", \"env\": { \"BASH_DEFAULT_TIMEOUT_MS\": \"1\" }"));

        Assert.DoesNotContain(d, x => x.Code == "GR2084");
    }

    [Theory]
    [InlineData("\"extraArgs\": [\"--settings\", \"/tmp/s.json\"]", "extraArgs carries '--settings'")]
    [InlineData("\"extraArgs\": [\"--settings=/tmp/s.json\"]", "extraArgs carries '--settings=/tmp/s.json'")]
    [InlineData("\"guardrailOverrides\": { \"extraArgs\": [\"--settings\", \"x\"] }", "guardrailOverrides.extraArgs carries '--settings'")]
    public void Gr2084_SettingsFlagInExtraArgs(string fragment, string expected) =>
        AssertGr2084(Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", {fragment}"), expected);

    [Fact]
    public void Gr2065_StillRejectsContextTokensOnAPlainClaudeBlock()
    {
        IReadOnlyList<Diagnostic> d = Validate(Block("\"model\": \"claude-sonnet-4-5\", \"contextTokens\": 1000"));

        Assert.Contains(d, x => x.Code == "GR2065");
    }

    [Theory]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("us.anthropic.claude-sonnet-4-5-v1:0")]
    [InlineData("anthropic.claude-3-haiku")]
    [InlineData("Sonnet[1m]")]
    [InlineData("OPUS")]
    [InlineData("haiku")]
    [InlineData("fable")]
    [InlineData("opusplan")]
    [InlineData("default")]
    public void Gr2085_TheBlocksOwnModel_InEveryAliasForm(string model)
    {
        Diagnostic warning = Assert.Single(
            Validate(Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"{model}\"")),
            x => x.Code == "GR2085");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("promptRunners.qwen.model", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qwen")]
    [InlineData("qwen3.6-35b")]
    [InlineData("sonnet-alike-local")]
    public void Gr2085_ANonClaudeModel_IsSilent(string model) =>
        Assert.DoesNotContain(
            Validate(Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"{model}\"")),
            x => x.Code == "GR2085");

    [Fact]
    public void Gr2085_GuardrailOverridesModel()
    {
        Diagnostic warning = Assert.Single(
            Validate(Block("""
                "baseUrl": "http://127.0.0.1:4000", "model": "Qwen", "guardrailOverrides": { "model": "claude-haiku-4-5" }
                """)),
            x => x.Code == "GR2085");

        Assert.Contains("guardrailOverrides.model", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"extraArgs\": [\"--model\", \"claude-opus-4-1\"]", "extraArgs")]
    [InlineData("\"extraArgs\": [\"--fallback-model\", \"sonnet\"]", "extraArgs")]
    [InlineData("\"extraArgs\": [\"--fallback-model=haiku\"]", "extraArgs")]
    [InlineData("\"extraArgs\": [\"--model\", \"Qwen\"]", "extraArgs")]
    [InlineData("\"guardrailOverrides\": { \"extraArgs\": [\"--model=Qwen3.8\"] }", "guardrailOverrides.extraArgs")]
    public void Gr2084_AModelFlagInAGatewayBlocksExtraArgs_IsAnError_NotAWarning(string fragment, string key)
    {
        IReadOnlyList<Diagnostic> d = Validate(Block($"\"baseUrl\": \"http://127.0.0.1:4000\", \"model\": \"Qwen\", {fragment}"));

        Diagnostic error = Assert.Single(d, x => x.Code == "GR2084");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains($"promptRunners.qwen.{key} passes a model flag", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(d, x => x.Code == "GR2085");
    }

    [Fact]
    public void Gr2087_AGatewayKeyUnderAnOpenAiCompatBlocksOverrides_IsAWarning_NotAGatewayError()
    {
        IReadOnlyList<Diagnostic> d = Validate("""
            { "version": 1, "maxParallelism": 1, "promptRunners": { "judge": {
                "kind": "openai-compat", "endpoint": "http://127.0.0.1:11434/v1", "model": "m", "contextTokens": 8192,
                "guardrailOverrides": { "baseUrl": "http://x:1", "contextTokens": 4096 } } } }
            """);

        Assert.DoesNotContain(d, x => x.Code == "GR2084");
        Assert.Equal(2, d.Count(x => x.Code == "GR2087" && x.Severity == DiagnosticSeverity.Warning));
        Assert.Contains(d, x => x.Code == "GR2087"
                                && x.Message.Contains("has no effect on a 'openai-compat' block", StringComparison.Ordinal));
    }

    [Fact]
    public void Gr2065_ContextTokensOnAPlainClaudeBlock_SaysToAddBaseUrl()
    {
        Diagnostic error = Assert.Single(
            Validate(Block("\"model\": \"claude-sonnet-4-5\", \"contextTokens\": 1000")),
            x => x.Code == "GR2065");
        Assert.Contains("\"baseUrl\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2085_AnActionModelPinDispatchedToAGateway()
    {
        IReadOnlyList<Diagnostic> d = Validate(
            """{ "version": 1, "maxParallelism": 1, "promptRunners": { "default": "qwen", "qwen": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" } } }""",
            """{ "description": "t", "writeScope": [], "dependsOn": [], "action": { "model": "claude-sonnet-4-5" } }""");

        Diagnostic warning = Assert.Single(d, x => x.Code == "GR2085");
        Assert.Contains("action.model", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2085_AnActionModelPinDispatchedToAPlainClaudeBlock_IsSilent()
    {
        IReadOnlyList<Diagnostic> d = Validate(
            """{ "version": 1, "maxParallelism": 1, "promptRunners": { "default": "claude", "claude": { }, "qwen": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" } } }""",
            """{ "description": "t", "writeScope": [], "dependsOn": [], "action": { "model": "claude-sonnet-4-5" } }""");

        Assert.DoesNotContain(d, x => x.Code == "GR2085");
    }

    [Fact]
    public void Gr2086_TwoModelsOnLocalhostAndLoopback_UnderParallelism()
    {
        IReadOnlyList<Diagnostic> d = Validate("""
            {
              "version": 1, "maxParallelism": 2,
              "promptRunners": {
                "default": "qwen36",
                "qwen36": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" },
                "qwen38": { "baseUrl": "http://localhost:4000/", "model": "Qwen3.8" }
              }
            }
            """);

        Diagnostic warning = Assert.Single(d, x => x.Code == "GR2086");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("'Qwen'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("'Qwen3.8'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr2086_IsSilentSerially_AndForDistinctPorts()
    {
        const string Serial = """
            { "version": 1, "maxParallelism": 1, "promptRunners": { "default": "a",
              "a": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" }, "b": { "baseUrl": "http://localhost:4000", "model": "Qwen3.8" } } }
            """;
        const string Ports = """
            { "version": 1, "maxParallelism": 2, "promptRunners": { "default": "a",
              "a": { "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" }, "b": { "baseUrl": "http://127.0.0.1:4001", "model": "Qwen3.8" } } }
            """;

        Assert.DoesNotContain(Validate(Serial), x => x.Code == "GR2086");
        Assert.DoesNotContain(Validate(Ports), x => x.Code == "GR2086");
    }

    private void AssertGr2084(string guardrailsJson, string expected)
    {
        IReadOnlyList<Diagnostic> d = Validate(guardrailsJson);
        Assert.Contains(d, x => x.Code == "GR2084"
                                && x.Severity == DiagnosticSeverity.Error
                                && x.Message.Contains(expected, StringComparison.Ordinal));
    }

    private static string Block(string body) =>
        $$"""{ "version": 1, "maxParallelism": 1, "promptRunners": { "default": "qwen", "qwen": { {{body}} } } }""";

    private IReadOnlyList<Diagnostic> Validate(string guardrailsJson, string? taskJson = null)
    {
        string plan = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N")[..8]);
        string taskDir = Path.Combine(plan, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(plan, "guardrails.json"), guardrailsJson);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            taskJson ?? """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(plan);
        List<Diagnostic> diagnostics = [.. result.Diagnostics];
        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan));
        }

        return diagnostics;
    }
}
