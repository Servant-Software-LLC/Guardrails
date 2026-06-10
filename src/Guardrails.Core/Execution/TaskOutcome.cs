namespace Guardrails.Core.Execution;

/// <summary>Terminal outcome of a single task in an M2 serial run.</summary>
public enum TaskOutcome
{
    /// <summary>Action exited 0 and every guardrail passed.</summary>
    Succeeded,

    /// <summary>The action exited non-zero (guardrails were skipped).</summary>
    ActionFailed,

    /// <summary>The action succeeded but one or more guardrails failed.</summary>
    GuardrailFailed,

    /// <summary>A dependency did not succeed, so this task never ran.</summary>
    Blocked
}
