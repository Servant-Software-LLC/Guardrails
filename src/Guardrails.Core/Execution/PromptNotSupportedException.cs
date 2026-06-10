namespace Guardrails.Core.Execution;

/// <summary>
/// Thrown when a run is attempted on a plan that contains prompt actions or prompt
/// guardrails. Prompt execution arrives in M5; until then a plan with prompts validates
/// fine but cannot be run. The run fails fast — before any task executes.
/// </summary>
public sealed class PromptNotSupportedException : Exception
{
    public PromptNotSupportedException(string message) : base(message) { }
}
