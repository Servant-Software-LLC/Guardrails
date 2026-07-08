namespace Guardrails.Core.Model;

/// <summary>
/// The unified autonomy knob (SSOT §2.1, #254/#269/#274): ONE enum governing EVERY prompt/halt/auto
/// decision boundary in the harness, replacing the per-feature knobs that would otherwise multiply (the
/// #274 Part C <c>driftPolicy</c> it folds in, the #269 overwatcher, the #254 inter-wave adjustment). In
/// M1 the only wired boundary is the resume definition-drift gate (§7.2); the wave / task boundaries
/// (M2/M3) reuse this same field. An UNSAFE / UNSOUND action (e.g. a non-suffix / uncontained-fan-in
/// drift rewind) ALWAYS halts, exit 2, regardless of this policy — no value authorizes an unsound action.
/// </summary>
public enum AutonomyPolicy
{
    /// <summary>
    /// DEFAULT. At a decision boundary, in an interactive TTY PROMPT the operator (apply on approval,
    /// halt on decline); in a non-interactive context (redirected stdin / CI / an overwatcher) HALT
    /// (exit 2) — never prompts, never spends unbidden. (The <c>ResetCommand.Confirm</c> /
    /// <c>Console.IsInputRedirected</c> discipline.)
    /// </summary>
    Prompt,

    /// <summary>
    /// Explicit strict opt-out: never prompt, never auto — ALWAYS halt (exit 2) for out-of-band human
    /// action. Most conservative (preserves the #274 Part A behavior for CI-strict users).
    /// </summary>
    Halt,

    /// <summary>
    /// "Just handle everything": apply the decision without prompting WHEREVER it is SAFE / SANCTIONED —
    /// e.g. rewind the plan branch past a provably-safe drifted suffix and re-run it. Authorizes SPEND /
    /// APPLICATION of a SAFE action, never an UNSOUND one. Selected by the CLI <c>--autonomy auto</c> or
    /// the legacy alias <c>--reprocess-drift</c>.
    /// </summary>
    Auto
}

/// <summary>
/// The single source of truth for the <see cref="AutonomyPolicy"/> string tokens (SSOT §2.1:
/// <c>prompt</c> / <c>halt</c> / <c>auto</c>). Shared by the loader (<c>guardrails.json</c> parsing), the
/// CLI <c>--autonomy</c> flag, and the <c>decisions[]</c> reporting surface so the spelling never forks.
/// </summary>
public static class AutonomyPolicies
{
    /// <summary>The canonical wire token for <paramref name="policy"/> (e.g. <see cref="AutonomyPolicy.Auto"/> ⇒ <c>auto</c>).</summary>
    public static string Token(AutonomyPolicy policy) => policy switch
    {
        AutonomyPolicy.Prompt => "prompt",
        AutonomyPolicy.Halt => "halt",
        AutonomyPolicy.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unhandled autonomy policy.")
    };

    /// <summary>
    /// Parse an <c>autonomyPolicy</c> string (trim + case-insensitive): <c>prompt</c>, <c>halt</c>, or
    /// <c>auto</c>. Any other value returns <c>false</c> (the caller emits GR2031 / a CLI usage error).
    /// </summary>
    public static bool TryParse(string value, out AutonomyPolicy policy)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "prompt":
                policy = AutonomyPolicy.Prompt;
                return true;
            case "halt":
                policy = AutonomyPolicy.Halt;
                return true;
            case "auto":
                policy = AutonomyPolicy.Auto;
                return true;
            default:
                policy = AutonomyPolicy.Prompt;
                return false;
        }
    }
}
