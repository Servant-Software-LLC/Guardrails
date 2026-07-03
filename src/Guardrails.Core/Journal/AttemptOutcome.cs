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
    /// A prompt action exhausted its TURN budget mid-progress (issue #129 / #94): the runner reported
    /// max-turns (Claude: <c>error_max_turns</c>). A budget-exhaustion failure distinct from a generic
    /// <see cref="ActionFailed"/>, so a human (and §9 triage) sees the agent ran out of TURNS — not a
    /// logic failure — and the harness AUTO-ESCALATES the next attempt's turn budget (mirroring the
    /// timeout clock) instead of retrying into the same wall.
    /// </summary>
    MaxTurns,

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
    NeedsHuman,

    /// <summary>
    /// The runtime REFUSED a write/edit because the path is not on the granted permission allow-list,
    /// and the wall is unrecoverable by retrying (issues #86 / #104): a <c>.claude/</c> path (the
    /// Claude Code sub-agent runtime blocks automated <c>.claude/</c> writes even under
    /// <c>acceptEdits</c> — structural, halted on the FIRST hit) or the SAME path refused across
    /// repeated attempts. The harness settles the task <c>needs-human</c> EARLY with an actionable
    /// reason instead of burning the rest of the retry budget on the identical wall. Distinct from a
    /// generic <see cref="ActionFailed"/> so a human sees a permission/config issue, not "the agent
    /// failed".
    /// </summary>
    PermissionDenied,

    /// <summary>
    /// A per-task <c>tasks/&lt;id&gt;/preflights/</c> slot failed (the two-scope preflights F9 split): the
    /// task-scoped preflight gate did not pass, so the harness settles the task <c>needs-human</c> and its
    /// transitive cone <c>blocked</c> (exit 2) instead of running the action. Journaled inside <c>tasks{}</c>
    /// as the attempt <c>outcome</c>, alongside <see cref="GuardrailFailed"/> / <see cref="ActionFailed"/> /
    /// <see cref="OutputCap"/> / <see cref="MaxTurns"/> / <see cref="RateLimited"/> — a DISTINCT outcome so a
    /// human (and §9 triage) sees a preflight gate failure, not a generic action failure. Distinct from the
    /// two whole-plan phase halts (<c>plan-preflight-failed</c> / <c>plan-guardrail-failed</c>), which live
    /// OUTSIDE <c>tasks{}</c> in the top-level journal sections (SSOT §7).
    /// </summary>
    TaskPreflightFailed
}
