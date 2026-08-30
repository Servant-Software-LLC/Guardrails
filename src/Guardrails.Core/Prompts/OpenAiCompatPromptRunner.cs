namespace Guardrails.Core.Prompts;

/// <summary>
/// The openai-compat runner (plan 28, issue #223) — POSTs to an OpenAI-compatible
/// <c>/chat/completions</c> endpoint, the ONE kind covering Ollama, llama.cpp, LM Studio and vLLM
/// because they share the wire protocol (<see cref="Model.PromptRunnerKind.OpenAiCompat"/>).
///
/// <para><b>STUB.</b> Every behavioural member throws <see cref="NotImplementedException"/> — the
/// transport (task 11), tool loop (task 13) and verdict/role gate (task 15) fill this class in. It is
/// NOT registered anywhere yet: <see cref="PromptRunnerRegistry"/> still dispatches only <c>claude</c>,
/// and <see cref="Model.PromptRunnerKinds.Implemented"/> does not list
/// <see cref="Model.PromptRunnerKind.OpenAiCompat"/>, so no config can reach this class today (GR2044
/// blocks it at <c>validate</c>). It exists purely so later tasks' tests compile against a real type.</para>
/// </summary>
public sealed class OpenAiCompatPromptRunner : IPromptRunner
{
    public OpenAiCompatPromptRunner(string name)
    {
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "OpenAiCompatPromptRunner is a stub (plan 28 task 09) — transport lands in task 11.");
}
