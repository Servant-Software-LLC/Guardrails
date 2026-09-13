using Guardrails.Core.Journal;
using Guardrails.Core.Model;

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

    /// <summary>
    /// For <see cref="OverwatchDecisionKind.AutoResolve"/>: the workspace-relative paths the overwatcher
    /// supplied via <see cref="OverwatchSupplyAutoResolve.Resolve"/> (design 40 §3/§4). Null for every
    /// other kind.
    /// </summary>
    public IReadOnlyList<string>? AutoResolvedPaths { get; init; }

    /// <summary>The advisory no-op: the deterministic policy stands unchanged (no runner, cost cap hit, or a malformed/errored/absent proposal).</summary>
    public static OverwatchDecision NoAction { get; } = new() { Kind = OverwatchDecisionKind.NoAction };
}

/// <summary>The overwatcher control-flow outcomes.</summary>
public enum OverwatchDecisionKind
{
    /// <summary>The overwatcher stayed out — the deterministic policy (short-circuit / retry / exhaustion) proceeds unchanged.</summary>
    NoAction,

    /// <summary>Halt honestly now, with the precise diagnosis. Never softer than the deterministic policy — only earlier + richer.</summary>
    Halt,

    /// <summary>Grant one more attempt BECAUSE a sanctioned change (guidance / budget) was applied that materially alters it.</summary>
    Grant,

    /// <summary>
    /// The overwatcher supplied an already-staged resource and re-armed the halted task itself — ONLY at
    /// <c>dial:critical</c> (design 40 §3, <see cref="OverwatchSupplyAutoResolve"/>). Distinct from
    /// <see cref="Grant"/>: a grant continues the SAME attempt with a sanctioned action-layer lever
    /// (guidance/budget); an auto-resolve drains a supplied file onto the run's base — naming the
    /// overwatcher as the <see cref="Journal.SuppliedRecord.By"/> supplier (design 40 §4) — and re-arms
    /// the task for a FRESH attempt whose own guardrails still run in full, exactly as if a human had run
    /// the three-command sequence themselves.
    /// </summary>
    AutoResolve
}

/// <summary>
/// Decides whether the overwatcher may AUTO-RESOLVE a needs-human halt caused by a missing supplied
/// resource (design 40 §3) instead of merely proposing the fix. Gated at <c>dial:critical</c> — the SAME
/// composition rule doc 12 §3.2 uses everywhere else the dial engages: <see cref="AutonomyPolicy.Auto"/>
/// AND an <c>autonomy</c> block present (<paramref name="autonomyBlockPresent"/> below — the
/// anti-Option-(c) guard also used by the auto-tier gate in <see cref="Overwatch"/>) AND
/// <see cref="EscalationThreshold.Critical"/>. Below that composition, the resolve does nothing but
/// propose the copy-pasteable three-command sequence (design 40 §3(b)) — it never drains the staging tree
/// and never touches the journal.
/// <para>
/// <b>The caution the decision survives (design 40 §3).</b> Applying the fix is mechanical — draining
/// <c>resourceSupply</c>'s already-staged file (<see cref="SuppliedDrain.Drain"/>) and recording it
/// (<see cref="RunJournal.RecordSupplied"/>) reuse the SAME shipped machinery a human-driven
/// <c>guardrails supply</c> uses. What is NOT mechanical, and what stays a human's call below
/// <c>dial:critical</c>, is deciding that <c>resourceSupply</c>'s staged file really is the file the task
/// needed — the caller is trusted to have made that match; this method only gates WHETHER to act on it.
/// </para>
/// <para>
/// STUB (task 19 — design 40 §3, review 2026-09-11): throws <see cref="NotImplementedException"/>
/// unconditionally. Task 20 wires the real decision, the drain, and the provenance write — naming
/// <c>"overwatcher"</c> as the <see cref="SuppliedRecord.By"/> supplier, never <c>"operator"</c>.
/// </para>
/// </summary>
public static class OverwatchSupplyAutoResolve
{
    /// <param name="policy">The run's <see cref="AutonomyPolicy"/>. Anything other than <see cref="AutonomyPolicy.Auto"/> keeps the dial inert (doc 12 §3.2).</param>
    /// <param name="autonomyBlockPresent">Whether the run's config carries an explicit <c>autonomy</c> block — the anti-Option-(c) guard; a bare <c>auto</c> with no block keeps the dial inert.</param>
    /// <param name="escalationThreshold">The run-wide dial (<c>plan.Config.Autonomy?.EscalationThreshold</c>). Only <see cref="EscalationThreshold.Critical"/> sanctions an auto-resolve.</param>
    /// <param name="task">The halted task the resource is needed for.</param>
    /// <param name="plan">The plan — supplies <c>Workspace</c>/<c>PlanDirectory</c> for the drain.</param>
    /// <param name="journal">The run's journal — receives the §4 provenance record on an auto-resolve.</param>
    /// <param name="resourceSupply">
    /// The <see cref="OverwatchFixKind.ResourceSupply"/> fix naming the already-staged, already-matched
    /// resource. The caller has already made the "is this the right file" judgement (design 40 §3); this
    /// method only gates whether to act on it.
    /// </param>
    public static OverwatchDecision Resolve(
        AutonomyPolicy policy,
        bool autonomyBlockPresent,
        EscalationThreshold? escalationThreshold,
        TaskNode task,
        PlanDefinition plan,
        RunJournal journal,
        OverwatchFixOp resourceSupply) =>
        throw new NotImplementedException();
}
