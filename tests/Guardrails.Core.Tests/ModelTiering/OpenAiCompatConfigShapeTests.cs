using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The openai-compat block config surface (plan 28 §4, issue #223): <c>endpoint</c> /
/// <c>contextTokens</c> / <c>apiKeyEnv</c> / <c>wire</c> / <c>engine</c> LOAD off <c>guardrails.json</c>
/// and land on the parsed <see cref="PromptRunnerConfig"/> — proving <c>PlanLoader.BuildRunnerConfig</c>
/// actually BINDS the five new <c>RawPromptRunner</c> properties onto the model, not merely that they
/// deserialize onto the raw shape (the false green a declared-but-unbound property would otherwise
/// produce). Goes straight through <see cref="PlanLoader"/>, never through record initializers, for the
/// same reason <c>PromptRunnerSchemaTests</c> does: "loads" is a claim about the loader.
///
/// <para><b>No <see cref="PlanValidator"/> pass here.</b> Shape/range validation of these five keys
/// (absolute-URL endpoint, contextTokens &gt;= 1, wire's harness-owned-field check) is GR2065, a
/// separate task's job — and running the validator over a <c>kind: "openai-compat"</c> block today
/// would also trip GR2044 (recognised-but-not-yet-implemented), which is a fact about the RUNNER, not
/// about this config surface. This class's job is narrower and prior to both: prove the loader binds
/// the five keys, full stop.</para>
/// </summary>
public sealed class OpenAiCompatConfigShapeTests : IDisposable
{
    private readonly string _root;

    public OpenAiCompatConfigShapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-openai-shape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// A block carrying all five new keys — the plan's own §4 worked example — loads clean and every
    /// value lands on the parsed <see cref="PromptRunnerConfig"/>, including a nested <c>wire</c> knob
    /// (<c>options.num_ctx</c>), which only survives if <c>Wire</c> is bound as a real map rather than
    /// dropped or flattened.
    /// </summary>
    [Fact]
    public void NewKeys_LoadAndLandOnTheBlock()
    {
        PromptRunnerConfig runner = LoadRunner("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "apiKeyEnv": "LOCAL_INFERENCE_KEY",
                  "engine": "ollama",
                  "wire": { "keep_alive": "30m", "options": { "num_ctx": 32768 } }
                }
              }
            }
            """, "local-qwen");

        Assert.Equal(PromptRunnerKind.OpenAiCompat, runner.Kind);
        Assert.Equal("http://127.0.0.1:11434/v1", runner.Endpoint);
        Assert.Equal(32768, runner.ContextTokens);
        Assert.Equal("LOCAL_INFERENCE_KEY", runner.ApiKeyEnv);
        Assert.Equal("ollama", runner.Engine);

        Assert.NotNull(runner.Wire);
        Assert.Equal("30m", runner.Wire!["keep_alive"].GetString());
        Assert.Equal(32768, runner.Wire["options"].GetProperty("num_ctx").GetInt32());
    }

    /// <summary>
    /// The back-compat pin: a block that never mentions any of the five keys loads with every one of
    /// them absent as <c>null</c> — not defaulted to anything, and no existing <c>guardrails.json</c>
    /// changes behaviour by a byte.
    /// </summary>
    [Fact]
    public void NewKeys_AreAbsentAsNull_OnAConfigThatOmitsThem()
    {
        PromptRunnerConfig runner = LoadRunner("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "permissionMode": "acceptEdits", "maxTurns": 50 }
              }
            }
            """, "claude");

        Assert.Equal(PromptRunnerKind.Claude, runner.Kind);
        Assert.Null(runner.Endpoint);
        Assert.Null(runner.ContextTokens);
        Assert.Null(runner.ApiKeyEnv);
        Assert.Null(runner.Wire);
        Assert.Null(runner.Engine);
    }

    // --- harness ------------------------------------------------------------------------

    /// <summary>Load <paramref name="guardrailsJson"/> through the real <see cref="PlanLoader"/> and return the parsed block named <paramref name="runnerName"/>.</summary>
    private PromptRunnerConfig LoadRunner(string guardrailsJson, string runnerName)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);

        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        PlanLoadResult result = new PlanLoader().Load(_root);

        Assert.True(result.Plan is not null, $"plan failed to load:\n{string.Join("\n", result.Diagnostics)}");

        return result.Plan!.Config.PromptRunners[runnerName];
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
