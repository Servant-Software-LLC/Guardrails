namespace Guardrails.Core.Journal;

/// <summary>
/// The outcome of a single attempt, recorded in the journal (SSOT §7 attempt
/// <c>outcome</c> field). The full set is modelled now; M3 produces
/// <see cref="Succeeded"/>, <see cref="ActionFailed"/>, <see cref="GuardrailFailed"/>,
/// <see cref="Timeout"/>, and <see cref="InvalidFragment"/>. <see cref="Cancelled"/>
/// arrives with Ctrl+C handling in M4.
/// </summary>
public enum AttemptOutcome
{
    /// <summary>Action ran clean, every guardrail passed, and the fragment (if any) merged.</summary>
    Succeeded,

    /// <summary>The action exited non-zero; guardrails were skipped.</summary>
    ActionFailed,

    /// <summary>The action succeeded but one or more guardrails failed.</summary>
    GuardrailFailed,

    /// <summary>The action or a guardrail exceeded its timeout and was killed.</summary>
    Timeout,

    /// <summary>The run was cancelled (Ctrl+C) before the attempt completed. (M4.)</summary>
    Cancelled,

    /// <summary>The action wrote a fragment that was not a parseable JSON object (SSOT §6.2).</summary>
    InvalidFragment
}
