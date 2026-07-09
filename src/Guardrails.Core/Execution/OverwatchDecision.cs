namespace Guardrails.Core.Execution;

/// <summary>
/// What the overwatcher decided at a struggle boundary (doc 11 §5) — the control-flow signal the
/// <see cref="TaskExecutor"/> loop consults. It is NEVER a verdict: the overwatcher can grant an adjusted
/// attempt (coupled to a sanctioned change) or halt honestly, but it can never mark a task succeeded or
/// merge a fragment. "No sanctioned change ⇒ no grant": a <see cref="OverwatchDecisionKind.Grant"/> ALWAYS
/// carries a materially-different next attempt (guidance and/or a budget bump).
/// </summary>
public sealed record OverwatchDecision
{
    /// <summary>The decision kind.</summary>
    public required OverwatchDecisionKind Kind { get; init; }

    /// <summary>
    /// For <see cref="OverwatchDecisionKind.Grant"/>: guidance to inject into the NEXT attempt's composed
    /// prompt (the ephemeral, allowlist lever). Non-empty when a guidance op was sanctioned.
    /// </summary>
    public string? GuidanceInjection { get; init; }

    /// <summary>
    /// For <see cref="OverwatchDecisionKind.Grant"/>: extra retry attempts to add to the budget (a
    /// sanctioned budget lever), already clamped to the hard cap. Zero when only guidance was sanctioned.
    /// </summary>
    public int ExtraRetries { get; init; }

    /// <summary>
    /// A one-line enrichment appended to the task's <c>needs-human</c> summary when the overwatcher halts
    /// with a precise diagnosis (makes the halt earlier + richer, never softer). Null for a grant/no-action.
    /// </summary>
    public string? RichHaltSummary { get; init; }

    /// <summary>The advisory no-op: the deterministic policy stands unchanged (no runner, cost cap hit, or a malformed/errored/absent proposal).</summary>
    public static OverwatchDecision NoAction { get; } = new() { Kind = OverwatchDecisionKind.NoAction };
}

/// <summary>The three overwatcher control-flow outcomes.</summary>
public enum OverwatchDecisionKind
{
    /// <summary>The overwatcher stayed out — the deterministic policy (short-circuit / retry / exhaustion) proceeds unchanged.</summary>
    NoAction,

    /// <summary>Halt honestly now, with the precise diagnosis. Never softer than the deterministic policy — only earlier + richer.</summary>
    Halt,

    /// <summary>Grant one more attempt BECAUSE a sanctioned change (guidance / budget) was applied that materially alters it.</summary>
    Grant
}
