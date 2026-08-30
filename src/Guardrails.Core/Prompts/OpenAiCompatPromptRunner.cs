using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The openai-compat runner (plan 28, issue #223) — POSTs to an OpenAI-compatible
/// <c>/chat/completions</c> endpoint, the ONE kind covering Ollama, llama.cpp, LM Studio and vLLM
/// because they share the wire protocol (<see cref="Model.PromptRunnerKind.OpenAiCompat"/>).
///
/// <para><b>STUB.</b> <see cref="RunAsync"/> throws <see cref="NotImplementedException"/> — the
/// transport (task 11), tool loop (task 13) and verdict/role gate (task 15) fill it in. It is
/// NOT registered anywhere yet: <see cref="PromptRunnerRegistry"/> still dispatches only <c>claude</c>,
/// and <see cref="Model.PromptRunnerKinds.Implemented"/> does not list
/// <see cref="Model.PromptRunnerKind.OpenAiCompat"/>, so no config can reach this class today (GR2044
/// blocks it at <c>validate</c>). The constructor is real (plan 28 §4) — mirroring
/// <see cref="ClaudePromptRunner"/>'s <c>(name, command, processRunner)</c> shape — so tests can point a
/// real instance at a specific endpoint/model/context-window/wire config and a real transport
/// collaborator ahead of <see cref="RunAsync"/> being filled in.</para>
/// </summary>
public sealed class OpenAiCompatPromptRunner : IPromptRunner
{
    private readonly PromptRunnerConfig _config;
    private readonly HttpClient _httpClient;

    /// <param name="name">The runner's name (the <c>promptRunners</c> map key).</param>
    /// <param name="config">
    /// The block's config (plan 28 §4) — carries <see cref="PromptRunnerConfig.Endpoint"/>,
    /// <see cref="PromptRunnerConfig.ContextTokens"/>, <see cref="PromptRunnerConfig.ApiKeyEnv"/>,
    /// <see cref="PromptRunnerConfig.Wire"/> and <see cref="PromptRunnerConfig.Engine"/> — the five
    /// keys <see cref="PromptInvocation"/> never carries (they live only here).
    /// </param>
    /// <param name="httpClient">The transport collaborator this runner POSTs its wire requests through.</param>
    public OpenAiCompatPromptRunner(string name, PromptRunnerConfig config, HttpClient httpClient)
    {
        Name = name;
        _config = config;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "OpenAiCompatPromptRunner is a stub (plan 28 task 09) — transport lands in task 11. " +
            $"(runner '{Name}', endpoint '{_config.Endpoint}', model '{_config.Settings.Model}', " +
            $"transport client base address '{_httpClient.BaseAddress}')");
}
