namespace Guardrails.Core.Execution;

/// <summary>Terminal outcome of a single task in a serial run.</summary>
public enum TaskOutcome
{
    /// <summary>Action exited 0, every guardrail passed, and the fragment (if any) merged.</summary>
    Succeeded,

    /// <summary>The action exited non-zero (guardrails were skipped).</summary>
    ActionFailed,

    /// <summary>The action succeeded but one or more guardrails failed.</summary>
    GuardrailFailed,

    /// <summary>The action and guardrails passed but the action's fragment was not a valid JSON object (SSOT §6.2).</summary>
    InvalidFragment,

    /// <summary>A dependency did not succeed, so this task never ran.</summary>
    Blocked,

    /// <summary>Resume skipped this task because the journal already records it as succeeded.</summary>
    Skipped
}
