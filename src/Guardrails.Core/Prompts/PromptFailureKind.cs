namespace Guardrails.Core.Prompts;

/// <summary>
/// A runner-agnostic classification of a non-successful prompt run (SSOT §9). Detection of
/// the concrete signal (HTTP status, vendor error text, the output-token-cap message) lives in
/// the CLI quarantine (<see cref="ClaudeStreamParser"/> / <see cref="ClaudePromptRunner"/>); the
/// harness (<c>ActionRunner</c> / <c>TaskExecutor</c>) routes on THIS enum only, never on any
/// Claude-specific string. Only <see cref="Transient"/> changes the retry control flow (it does
/// NOT consume the retry budget — the harness backs off and re-runs the same attempt, issue #115);
/// <see cref="OutputCap"/> and <see cref="Timeout"/> consume the budget like <see cref="Error"/>
/// but compose actionable, signal-specific retry feedback (issues #114 / #119).
/// </summary>
public enum PromptFailureKind
{
    /// <summary>The run succeeded (or its failure is not specially classified). Not a failure signal on its own.</summary>
    None,

    /// <summary>
    /// A transient, retryable infrastructure condition (issue #115): an HTTP 429/503/529, an
    /// "overloaded" response, or a usage/session/rate limit. A human cannot fix it and an immediate
    /// retry just re-fails, so the harness PAUSES (bounded backoff, honoring a parsed reset hint if
    /// any) and re-runs the SAME attempt WITHOUT consuming the retry budget.
    /// </summary>
    Transient,

    /// <summary>
    /// The single response exceeded the runner's output-token cap (issue #114,
    /// <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>). Distinct from a generic error so the retry carries
    /// actionable feedback ("write the file with incremental edits, keep reasoning brief") and a
    /// human sees a tool/config budget issue rather than a generic action failure.
    /// </summary>
    OutputCap,

    /// <summary>
    /// The runner process exceeded its timeout and was killed (issue #119). Distinct from a generic
    /// error so the retry carries timeout-specific feedback ("partial work is preserved — continue
    /// from it, don't re-explore") and the retry clock can be extended.
    /// </summary>
    Timeout,

    /// <summary>A genuine, non-special action failure (the agent reported <c>is_error</c> with no recognized signal).</summary>
    Error
}
