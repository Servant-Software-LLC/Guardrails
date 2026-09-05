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
    ///
    /// <para>On the pinned and legacy paths this is the name that was ASKED for — <c>action.runner</c>
    /// ?? the <c>promptRunners.default</c> pointer, the same expression today's executor resolves a
    /// runner by. It is therefore reported even when the registry declares no such block, a residual
    /// validation already rejects: <see cref="Runner"/> is the field that says whether the name found
    /// anything, and a diagnostic able to print the name nobody declared beats one that can only say
    /// "null".</para>
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

    /// <summary>
    /// The rung a PREVIOUS attempt of this task served, when a guardrail failure moved this attempt up
    /// the escalation ladder (<see cref="EscalationLadder.Apply"/>, issue #228); <c>null</c> means no
    /// escalation happened.
    ///
    /// <para><b><see cref="EscalatedFrom"/> is NOT <see cref="Climbed"/>.</b> <see cref="Climbed"/> is
    /// TRUE when <c>Candidates(RequestedTier)</c> was EMPTY and the resolver walked to a stronger rung
    /// inside ONE attempt — a CAPABILITY fact ("no configured runner serves the requested rung").
    /// <see cref="EscalatedFrom"/> is a different reason to be on a higher rung: a previous attempt of
    /// this task FAILED ITS GUARDRAILS. The two are independent — an escalated attempt may also climb —
    /// and they must stay separately readable in the journal and the report.</para>
    /// </summary>
    public string? EscalatedFrom { get; init; }
}

/// <summary>
/// The outcome of one VERIFIER resolution (DoR <c>docs/plans/17-model-tiering.md</c> §6.5/§6.5.1,
/// issue #201): the route the prompt-judge that grades an attempt actually runs on, and enough
/// provenance to answer "was this judge strong enough to vouch?".
///
/// <para><b>It carries §12.4's <c>judge {...}</c> object, plus what the advisory needs.</b> The
/// journal shape is <c>{ runner, kind, model, effort, tier, strength, bumped }</c> — every one of
/// those is a member here, so the wiring task MAPS this record rather than re-deriving any of it.
/// <see cref="Weak"/>, <see cref="Bumped"/> and <see cref="Degraded"/> are the three facts that exist
/// ONLY inside this resolution: the #229 advisory (and its preflight / JIT surfaces) reads them
/// instead of re-testing <c>strength</c>, <c>kind</c> and <c>costly</c> for itself, which would be the
/// D22a duplication one level down.</para>
///
/// <para><b>There is deliberately no <c>NoRoute</c> here, and the omission is the design.</b> An
/// unsatisfiable ACTOR rung HALTS (§6.2, GR2048); an unsatisfiable VERIFIER preference DEGRADES —
/// <see cref="Degraded"/> — and the run proceeds. §12.6 states that no verifier condition may ever
/// fail a build, so a judge that could not be improved must arrive as a route plus a warning, never as
/// an outcome the scheduler can halt on.</para>
/// </summary>
public sealed record JudgeResolution
{
    /// <summary>
    /// The <c>promptRunners</c> block the judge resolved to — <b>the object rule 7 turns on</b>.
    /// <c>guardrailOverrides</c> compose with the resolved JUDGE's block, not the actor's, and the
    /// mechanical form of that rule is calling <see cref="PromptRunnerConfig.EffectiveSettings"/> on
    /// THIS block. Applying the actor block's overrides to a judge running somewhere else silently
    /// mis-profiles every bumped judge — right model, wrong permissions, wrong tools, wrong turn
    /// budget — and nothing else in the system notices. Null only when nothing resolved at all (no
    /// rung, no floor, and no <c>default</c> pointer).
    /// </summary>
    public PromptRunnerConfig? Runner { get; init; }

    /// <summary>
    /// The resolved block's name (its <c>promptRunners</c> map key) — §12.4's <c>judge.runner</c>, and
    /// the spelling every log line and diagnostic uses.
    /// </summary>
    public string? RunnerName { get; init; }

    /// <summary>
    /// The resolved block's provider kind — §12.4's <c>judge.kind</c>. Recorded because it is half of
    /// §4.1's weakness fallback: once more than one provider is registered, "which model graded this"
    /// is not answerable from the model string alone. Null when no block resolved.
    /// </summary>
    public PromptRunnerKind? Kind { get; init; }

    /// <summary>
    /// The EFFECTIVE model string the judge runs on — §12.4's <c>judge.model</c>. Null = the runner
    /// CLI's own default, the same meaning it carries on the actor side.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The EFFECTIVE thinking-effort for the judge — §12.4's <c>judge.effort</c>, i.e. the resolved
    /// block's <c>effort</c>. Null = nothing stated one.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// The rung the judge resolved AT — §12.4's <c>judge.tier</c>. Per rule 2 this is the ACTOR's rung
    /// (or, for a rung-less actor, the plan-wide <c>tiering.defaultTier</c>), moved only by the §6.5.1
    /// floor. It is NOT moved by rule 3: <see cref="Bumped"/> moves STRENGTH at a FIXED rung (D24a).
    /// Null on a <c>runner</c> pin — no rung was resolved — and when no rung exists anywhere.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// The resolved block's declared <c>strength</c> — §12.4's <c>judge.strength</c>. Null = the
    /// operator ranked nothing, which is exactly why <see cref="Weak"/> is a separate datum rather
    /// than a comparison a reader could do on this number.
    /// </summary>
    public int? Strength { get; init; }

    /// <summary>
    /// §12.4's <c>judge.bumped</c>: TRUE when the rule-3 weak-actor STRENGTH bump fired — the judge is
    /// a strictly stronger block than the actor, AT the actor's own rung. FALSE (never absent) when it
    /// did not, because #230-lite reads this datum to answer "is a bumped judge worth what it costs",
    /// and an absent field cannot be counted.
    ///
    /// <para>It says the bump FIRED, not that one was wanted: when a bump was wanted and the only
    /// stronger block was <c>costly</c>, this stays false and <see cref="Degraded"/> is set instead.
    /// The pair is what tells "no bump was needed" apart from "a bump was refused".</para>
    /// </summary>
    public bool Bumped { get; init; }

    /// <summary>
    /// <b>The advisory's input datum</b>, and the reason it is recorded rather than re-derived: TRUE
    /// when the RESOLVED JUDGE block is weak by <see cref="TierResolver.IsWeakVerifier"/>, the one
    /// shared §6.5-rule-4 predicate. A judge weaker than its actor, or equal-and-weak, is a #229
    /// review finding, a preflight line and a per-attempt JIT re-check; all three read this (against
    /// the actor's own block) instead of each re-testing <c>strength</c> and <c>kind</c> for
    /// themselves.
    /// </summary>
    public bool Weak { get; init; }

    /// <summary>
    /// TRUE when §6.5 rule 1 decided this resolution — an explicit frontmatter <c>tier</c> or
    /// <c>runner</c> pin, which wins outright and stops every rule below it. A <c>runner</c> pin
    /// additionally leaves <see cref="Tier"/> null and bypasses the §6.5.1 floor: it named a block, so
    /// there is no rung for a floor to raise. The human who pinned it said what they wanted — and the
    /// advisory still measures what they got, because the floor governs resolution while the advisory
    /// governs reality.
    /// </summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// TRUE when the §6.5.1 floor decided <see cref="Tier"/> — either by RAISING a rung that came out
    /// below <c>tiering.verifier.minTier</c>, or by SUPPLYING one where resolution had none. It is
    /// what §6.5.1's journal note records as <c>judge.tierSource: "floor"</c>, so the cost of the
    /// policy stays attributable to the policy. FALSE when the floor is absent, or was satisfied
    /// without moving anything — a result at or above the floor is UNTOUCHED, which is the whole
    /// difference between a floor and a default.
    /// </summary>
    public bool FloorRaised { get; init; }

    /// <summary>
    /// TRUE when the §6.5 rule-5 DEGRADE fired: a strength bump was wanted, every block that would
    /// have satisfied it is <c>costly: true</c>, so the judge stayed at the ACTOR's route and the
    /// advisory fires. <b>The run PROCEEDS.</b> This is not a halt and it is not
    /// <see cref="TierResolution.NoRoute"/>'s counterpart — the actor HALTS in the same situation
    /// precisely because an actor route is load-bearing and a verifier opinion is not. Knowable only
    /// inside this resolution, and the datum that lets the advisory say "your judge is no stronger
    /// than the work BECAUSE the stronger block is reserved" instead of leaving the operator to guess.
    /// </summary>
    public bool Degraded { get; init; }
}
