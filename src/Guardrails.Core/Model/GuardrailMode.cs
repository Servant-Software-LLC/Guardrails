namespace Guardrails.Core.Model;

/// <summary>
/// How guardrail failures within a single task attempt are handled. SSOT §2.
/// </summary>
public enum GuardrailMode
{
    /// <summary>Stop at the first failing guardrail (default).</summary>
    FailFast,

    /// <summary>Run every guardrail and aggregate all failures.</summary>
    RunAll
}
