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
    /// The runner refused to send, or the server truncated, a request that overflowed the model's
    /// context window (plan 28 §6.1) — the exact mirror of <see cref="OutputCap"/> on the other side of
    /// the same window. Before sending, the runner refuses when a pessimistic estimate
    /// (<c>ceil(chars / 3) + maxOutputTokens</c>) exceeds the block's <c>contextTokens</c>; after a
    /// response, it fails when the server's reported <c>usage.prompt_tokens</c> is below an optimistic
    /// floor (<c>floor(chars / 4)</c>), which is the vendor silently truncating rather than reporting an
    /// error. Distinct from a generic <see cref="Error"/> so the retry carries actionable feedback (raise
    /// <c>contextTokens</c> or shrink the task's inputs) — there is nothing the harness can auto-escalate,
    /// unlike <see cref="MaxTurns"/>.
    /// </summary>
    ContextOverflow,

    /// <summary>
    /// The runner process exceeded its timeout and was killed (issue #119). Distinct from a generic
    /// error so the retry carries timeout-specific feedback (serial mode: "partial work is preserved —
    /// continue from it, don't re-explore"; worktree mode: "your writes were rolled back — re-author,
    /// don't re-explore", issue #167) and the retry clock can be extended.
    /// </summary>
    Timeout,

    /// <summary>
    /// The agent ran out of TURN budget mid-progress (issue #129 / #94): the runner reported the
    /// max-turns terminal subtype (Claude: <c>error_max_turns</c>, "Reached maximum number of turns
    /// (N)"). Categorically different from a logic failure — the agent was making real progress and
    /// simply hit the turn cap. Distinct from a generic <see cref="Error"/> so the retry carries
    /// actionable feedback ("you ran out of turns mid-task; work more directly") AND the harness
    /// AUTO-ESCALATES the next attempt's turn budget (mirroring the timeout clock, issue #119) instead
    /// of retrying into the same wall.
    /// </summary>
    MaxTurns,

    /// <summary>
    /// The session produced NO OUTPUT for longer than its stall bound and was killed (issue #504).
    /// Categorically different from <see cref="Timeout"/>, and the distinction is the whole point: a
    /// timeout bounds how long a session may RUN, a stall bounds how long it may be SILENT. A wall clock
    /// kills a session that is progressing steadily and lets a wedged one sit idle until the ceiling; a
    /// stall bound does neither. Measured twice on `model-tiering-stage-3`: both waves were emitting
    /// output continuously when the 30-minute clock killed them mid-sign-off.
    ///
    /// <para>The bound must clear the longest LEGITIMATE quiet tool call — a breakdown agent runs suites,
    /// and one <c>dotnet test</c> measured 10m44s with the stream silent throughout — so it is minutes,
    /// not seconds, and is deliberately NOT the 60s freshness threshold design 23 uses for DISPLAY.</para>
    /// </summary>
    Stalled,

    /// <summary>A genuine, non-special action failure (the agent reported <c>is_error</c> with no recognized signal).</summary>
    Error
}
