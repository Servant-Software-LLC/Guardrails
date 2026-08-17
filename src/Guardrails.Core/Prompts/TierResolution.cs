using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The outcome of one attempt-launch resolution (DoR <c>docs/plans/17-model-tiering.md</c> §6.1/§6.2,
/// issue #226): WHICH route was selected, and enough provenance to answer "how did I get here".
///
/// <para><b>It is a RESULT, never an exception.</b> Every §6.1/§6.2 outcome — a pin, a tier-resolved
/// route, a climb, legacy fallback, and the <c>no-route</c> defensive residual — arrives as one of
/// these, so the caller can tell them apart and settle each honestly. A resolver that threw for
/// "nothing serves this rung" would hand the caller a failure it cannot distinguish from a bug, which
/// is exactly what §6.2's <c>no-route</c> outcome exists to avoid.</para>
///
/// <para><b>Two of these fields are knowable ONLY inside the resolver.</b> <see cref="Climbed"/> and
/// the D28 pair (<see cref="CostlyCeilingBound"/> / <see cref="CostlyCeilingBlocks"/>) fall out of the
/// candidacy sweep and cannot be re-derived downstream without a second copy of the candidacy
/// predicate — which D22a forbids. They ride here so wave 2 READS them rather than re-testing
/// <see cref="PromptRunnerConfig.Costly"/> for itself.</para>
///
/// <para><b>There is deliberately no <c>tierSource</c> field.</b> §12.4's journal enum
/// (<c>tier</c>/<c>override</c>/<c>escalated</c>) is assembled in wave 2, and its missing input —
/// whether the rung came from <c>action.tier</c> or <c>tiering.defaultTier</c>, which
/// <c>PlanLoader</c> collapses at load — is restored separately as <c>ActionDefinition.TierOrigin</c>.
/// The resolver READS that origin; it does not compute it, so nothing about it belongs in this
/// record. <see cref="Pinned"/> and <see cref="Legacy"/> below are NOT that field: they are the
/// observable §6.1 PRECEDENCE branch, which this resolver does decide.</para>
/// </summary>
public sealed record TierResolution
{
    /// <summary>
    /// The <c>promptRunners</c> block this resolution selected, or <c>null</c> when nothing was
    /// selected (<see cref="NoRoute"/>).
    /// </summary>
    public PromptRunnerConfig? Runner { get; init; }

    /// <summary>
    /// The selected block's name (its <c>promptRunners</c> map key) — the spelling every log line,
    /// journal record and diagnostic uses. Null when <see cref="NoRoute"/>.
    /// </summary>
    public string? RunnerName { get; init; }

    /// <summary>
    /// The EFFECTIVE model string for this attempt: an <c>action.model</c> pin when one is present,
    /// otherwise the selected block's <c>model</c>, otherwise (on the legacy path) the runner's own
    /// model else the CLI default. Null means "the runner CLI's own default", which is what today's
    /// two-level fallback already means when nothing names a model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The EFFECTIVE thinking-effort for this attempt: the selected block's <c>effort</c>, overridden
    /// by <c>action.effort</c> when the action carries one. Per §6.1 item 2, <c>action.effort</c>
    /// ALONE is not a pin — it adjusts the RESOLVED route's effort rather than bypassing resolution.
    /// Null = nothing stated an effort, so the runner's own default applies.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// The rung resolution was ASKED for — the effective tier (<c>action.tier</c> ??
    /// <c>tiering.defaultTier</c>). Null on the pinned and legacy paths, where no rung was resolved.
    /// Kept alongside <see cref="Tier"/> so a climb is legible: the pair says "asked for easy, served
    /// at medium".
    /// </summary>
    public string? RequestedTier { get; init; }

    /// <summary>
    /// The rung actually SERVED — equal to <see cref="RequestedTier"/> unless the resolver
    /// <see cref="Climbed"/>, in which case it is the nearest STRONGER rung with a non-empty candidate
    /// set. Never weaker than <see cref="RequestedTier"/>: there is no downward lever in v1. Null when
    /// no rung was resolved (a pin, legacy, or <see cref="NoRoute"/>).
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// True when <c>Candidates(RequestedTier)</c> was empty and the resolver climbed to a stronger
    /// rung (§6.2). Knowable only inside the candidacy sweep; wave 2 logs it loudly and records it in
    /// per-attempt provenance rather than absorbing it silently.
    /// </summary>
    public bool Climbed { get; init; }

    /// <summary>
    /// The §6.2 <c>no-route</c> defensive outcome: NO candidate block exists at the requested rung or
    /// any stronger one, so nothing was selected. A config gap GR2048 should have caught at validate
    /// time; at run time it settles needs-human with an actionable "register a provider serving tier
    /// ≥ R" message. It is deliberately a distinguishable RESULT, not a silent fallback to the
    /// runner's model (D30) and not an exception.
    /// </summary>
    public bool NoRoute { get; init; }

    /// <summary>
    /// The <b>D28 binding-ceiling datum</b>: true when a block was excluded from candidacy ONLY
    /// because it is <c>costly: true</c> — i.e. it DECLARES the requested rung or a stronger one
    /// (<see cref="PromptRunnerConfig.DeclaresTier"/>) but is not a candidate
    /// (<see cref="PromptRunnerConfig.ServesTier"/>). Definitionally
    /// <c>DeclaresTier ∧ ¬ServesTier</c>, which is exactly the pair GR2048 already computes at
    /// validate time.
    ///
    /// <para><b>It changes what is LOGGED, never what is SELECTED.</b> The costly floor is untouched:
    /// no override, no dial, no new path to a costly model. What this datum buys is that wave 2 can
    /// emit a loud warning on re-attempt NAMING the block the harness was not permitted to pick —
    /// without it, a failure caused by the weaker model running out of reasoning is indistinguishable
    /// from an ordinary failure, and the operator tunes prompts against a constraint they cannot
    /// see.</para>
    ///
    /// <para>True independently of whether a route was found: it is equally the reason a rung
    /// <see cref="NoRoute"/>s (GR2048's second cause — "the only blocks serving this rung are marked
    /// costly") and the reason a weaker block was served when a stronger one was sitting right
    /// there.</para>
    /// </summary>
    public bool CostlyCeilingBound { get; init; }

    /// <summary>
    /// The names of the blocks <see cref="CostlyCeilingBound"/> is about — the ones the harness may
    /// never auto-select — so the D28 warning can name them instead of describing them. Ordered so the
    /// message is stable. Empty whenever <see cref="CostlyCeilingBound"/> is false.
    /// </summary>
    public IReadOnlyList<string> CostlyCeilingBlocks { get; init; } = [];

    /// <summary>
    /// True when §6.1 item 1 decided this: a full <c>action.runner</c>/<c>action.model</c> pin, which
    /// bypasses tier resolution entirely. <see cref="RequestedTier"/> and <see cref="Tier"/> are null
    /// on a pinned resolution — no rung was resolved — which is why this flag exists at all: without
    /// it "no rung" cannot be told from the legacy path.
    ///
    /// <para>A pin is the sanctioned route to a <c>costly</c> block: the floor constrains what the
    /// HARNESS may choose, never what a human may assign.</para>
    /// </summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// True when §6.1 item 3 decided this: there was no effective tier at all (no <c>action.tier</c>,
    /// no judge frontmatter <c>tier</c>, no <c>tiering.defaultTier</c>), so resolution fell back to
    /// <c>promptRunners.&lt;name&gt;.model</c> else the CLI default — exactly today's behaviour, and
    /// the runtime half of Invariant 7.
    ///
    /// <para><b>D30: legacy is the no-RUNG path, <see cref="NoRoute"/> is the no-CANDIDATE path, and
    /// nothing is both.</b> Once an effective tier exists, resolution owns the outcome: an empty
    /// candidate set climbs, and a genuinely empty registry at-or-above the rung settles
    /// <see cref="NoRoute"/>. It never silently drops back to the runner's model.</para>
    /// </summary>
    public bool Legacy { get; init; }
}
