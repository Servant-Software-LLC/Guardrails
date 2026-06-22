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

    /// <summary>
    /// A prompt action's response exceeded the runner's output-token cap (issue #114,
    /// <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>). A budget-exhaustion failure distinct from a generic
    /// <see cref="ActionFailed"/>, so a human (and §9 triage) sees the agent ran out of OUTPUT budget —
    /// a tool/config issue — and the retry carries actionable "write incrementally / split" feedback.
    /// </summary>
    OutputCap,

    /// <summary>
    /// A transient infrastructure limit (HTTP 429/503/529, "overloaded", a usage/session/rate limit)
    /// did not clear within the task's cumulative pause budget (<c>transientPauseBudgetSeconds</c>,
    /// issue #115). The harness paused+re-ran WITHOUT consuming the retry budget; only when the pause
    /// budget was exhausted did the task settle <c>needs-human</c> with this DISTINCT outcome — so the
    /// journal shows a rate-limit halt ("re-run later"), never a generic action failure. A transient
    /// pause that DOES clear is never journaled (observe-only via <see cref="Execution.IRunObserver.PromptPaused"/>).
    /// </summary>
    RateLimited,

    /// <summary>The run was cancelled (Ctrl+C) before the attempt completed. (M4.)</summary>
    Cancelled,

    /// <summary>The action wrote a fragment that was not a parseable JSON object (SSOT §6.2).</summary>
    InvalidFragment,

    /// <summary>
    /// A prompt action signalled an unresolvable decision by writing a root <c>needsHuman</c>
    /// key to its fragment (SSOT §9). The attempt is recorded with this distinct outcome and
    /// the task goes straight to <c>needs-human</c> — no retry burn, no guardrails. (M5.)
    /// </summary>
    NeedsHuman
}
