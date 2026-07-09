namespace Guardrails.Core.Execution;

/// <summary>
/// The struggle-boundary transitions at which the harness DETERMINISTICALLY engages the overwatcher
/// (SSOT §9.2, doc 11 §4, #305 Decision C — EAGER). The judge never decides WHEN it fires; the harness
/// does, from typed outcomes plus the eager <c>attempt ≥ 2</c> trigger. It fires AT MOST ONCE per attempt
/// and the whole thing is bounded by <c>maxCostUsd</c>.
/// </summary>
public enum OverwatchTrigger
{
    /// <summary>#305 Decision C: a task reached <c>attempt ≥ 2</c> (the eager trigger) with a retryable failure and budget remaining.</summary>
    EagerAttempt,

    /// <summary>The #174/#182 no-op-deadlock short-circuit is about to fire (a genuine no-op + byte-identical guardrail failure).</summary>
    NoOpDeadlock,

    /// <summary>The #264 deterministic-<c>script</c> reproduction short-circuit is about to fire (byte-identical action output + guardrail failure).</summary>
    DeterministicScript,

    /// <summary>A permission-wall early halt (#86/#104) — may fire even on attempt 1 (a structural <c>.claude/</c> wall).</summary>
    PermissionWall,

    /// <summary>The task exhausted its retry budget and is settling <c>needs-human</c> — today's §9.2 triage, now one overwatcher case (§9.2.1).</summary>
    TerminalExhaustion
}

/// <summary>Canonical wire tokens for <see cref="OverwatchTrigger"/> — used in <c>decisions[]</c> / <c>overwatch.jsonl</c> so the spelling never forks.</summary>
public static class OverwatchTriggers
{
    /// <summary>The canonical token for <paramref name="trigger"/>.</summary>
    public static string Token(OverwatchTrigger trigger) => trigger switch
    {
        OverwatchTrigger.EagerAttempt => "eager-attempt",
        OverwatchTrigger.NoOpDeadlock => "no-op-deadlock",
        OverwatchTrigger.DeterministicScript => "deterministic-script",
        OverwatchTrigger.PermissionWall => "permission-wall",
        OverwatchTrigger.TerminalExhaustion => "terminal-exhaustion",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unhandled overwatch trigger.")
    };
}
