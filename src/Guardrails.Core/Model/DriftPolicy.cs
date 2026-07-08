namespace Guardrails.Core.Model;

/// <summary>
/// How a resume handles a <b>provably-safe</b> definition drift on an already-<c>succeeded</c> task
/// (issue #274 Part C, SSOT §2/§7.2). An UNSAFE drift (a non-suffix / uncontained-fan-in rewind) ALWAYS
/// halts, exit 2, regardless of this policy — no value authorizes an unsound rewind.
/// </summary>
public enum DriftPolicy
{
    /// <summary>
    /// DEFAULT. In an interactive TTY, PROMPT the operator (<c>y</c> = rewind + re-run, <c>N</c> = halt);
    /// in a non-interactive context (redirected stdin / CI / an overwatcher) HALT (exit 2) — never
    /// prompts, never spends unbidden.
    /// </summary>
    Prompt,

    /// <summary>
    /// Pre-authorize the safe auto-resolve with NO prompt (interactive or not): rewind the plan branch
    /// past the safe drifted suffix and re-run it. Selected by the CLI <c>--reprocess-drift</c> override.
    /// Authorizes SPEND, never an unsound rewind.
    /// </summary>
    Reprocess,

    /// <summary>
    /// Explicit strict opt-out: ALWAYS halt on any drift, never prompt, never auto-resolve — preserves
    /// the Part A behavior for CI-strict users.
    /// </summary>
    Halt
}
