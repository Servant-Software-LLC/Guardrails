namespace Guardrails.Core.Model;

/// <summary>
/// The criticality dial — the OPTIONAL <c>autonomy</c> block of <c>guardrails.json</c> (issue #361,
/// doc 12 §3.3–§3.5; decided values §10 F/G/I/N). A NEW ORTHOGONAL axis that COMPOSES with — and never
/// redefines — the shipped <see cref="AutonomyPolicy"/> (SSOT §2.1): <c>autonomyPolicy</c> answers "how do I
/// treat a decision with a known-safe resolution, and may I prompt?"; the dial answers "how bold am I about
/// best-guessing a decision that has NO known-safe resolution, unattended?".
///
/// The whole block being ABSENT ⇒ <see cref="RunConfig.Autonomy"/> is <c>null</c> ⇒ the dial is INERT and the
/// run behaves byte-for-byte as today (doc 12 §3.2 — the load-bearing back-compat guarantee).
///
/// DATA ONLY. The raw→model MAPPING (the decided defaults, the GR2039/GR2040 validation) is authored by the
/// implement task (<c>03-implement-autonomy-config</c> / <c>05-implement-autonomy-validation</c>); this record
/// is only the shape those tests target. The C# property initializers below carry the decided defaults (§10
/// I/N) as DATA so a present-block-with-omitted-fields mapping resolves them, but no parse is wired here.
/// </summary>
public sealed record AutonomyConfig
{
    /// <summary>
    /// The run-wide dial (doc 12 §3.3): the LOWEST criticality that still escalates — the rule is literally
    /// <c>escalate ⟺ assessedCriticality ≥ EscalationThreshold</c>. Default
    /// <see cref="Model.EscalationThreshold.High"/> when the block is present (decided §10 N: <c>--autonomous</c>
    /// best-guesses only low/moderate; fully-autonomous requires an explicit <c>--dial critical</c>).
    /// </summary>
    public EscalationThreshold EscalationThreshold { get; init; } = EscalationThreshold.High;

    /// <summary>
    /// OPTIONAL per-gate overrides (doc 12 §3.5). A gate whose value is absent ⇒ the run-wide
    /// <see cref="EscalationThreshold"/> applies. Null (the default) ⇒ no overrides at all.
    /// </summary>
    public GateThresholds? GateThresholds { get; init; }

    /// <summary>
    /// The bounded wait/backoff ceiling for a retryable hard blocker (doc 12 §4.2). Its own decided defaults
    /// (<c>{ maxAttempts: 5, totalWaitSeconds: 900 }</c>, §10 I) apply when the sub-block is omitted.
    /// </summary>
    public BlockerRetry BlockerRetry { get; init; } = new();

    /// <summary>
    /// Run-level cap on how many times a judge may reclassify an UNKNOWN failure as retryable across the whole
    /// run (doc 12 §4.3). Once spent, every subsequent unknown failure escalates deterministically. Default
    /// <c>3</c> (decided §10 I).
    /// </summary>
    public int MaxJudgeWidenings { get; init; } = 3;
}

/// <summary>
/// The OPTIONAL per-gate dial overrides (doc 12 §3.5). <see cref="NeedsHuman"/> and
/// <see cref="WaveCheckpoint"/> are criticality levels; <see cref="ReviewGate"/> is NOT a criticality level
/// but the escalate/<c>proceed-unreviewed</c> acknowledgment (a FLOOR, doc 12 §5) — kept in the same map so
/// the operator sees all three gates in one place, precisely so that turning the run-wide dial to
/// <c>critical</c> can never accidentally clear the review gate. A null member ⇒ that gate falls back to the
/// run-wide dial.
/// </summary>
public sealed record GateThresholds
{
    /// <summary>Override for an agent-emitted <c>{"needsHuman": "…"}</c> gate (doc 12 §3.5).</summary>
    public EscalationThreshold? NeedsHuman { get; init; }

    /// <summary>Override for the JIT between-wave checkpoint gate (doc 12 §3.5, #360).</summary>
    public EscalationThreshold? WaveCheckpoint { get; init; }

    /// <summary>
    /// The <c>review-gate</c> acknowledgment (doc 12 §3.5/§5.2) — escalate (default) or the explicit named
    /// opt-in <c>proceed-unreviewed</c>. NOT a criticality level.
    /// </summary>
    public ReviewGateDecision? ReviewGate { get; init; }
}

/// <summary>
/// The bounded wait/backoff ceiling for a retryable hard blocker (doc 12 §4.2), floored by
/// <c>transientPauseBudgetSeconds</c>. Decided defaults (§10 I).
/// </summary>
public sealed record BlockerRetry
{
    /// <summary>Ceiling on retries before escalating a retryable blocker. Default <c>5</c> (§10 I).</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Ceiling on cumulative wait (seconds) before escalating. Default <c>900</c> (§10 I).</summary>
    public int TotalWaitSeconds { get; init; } = 900;
}

/// <summary>
/// The four coarse criticality levels, ASCENDING (doc 12 §3.3; decided §10 F). Declared low→critical so the
/// enum's NATURAL order IS the criticality order — <c>Low &lt; Moderate &lt; High &lt; Critical</c> — because
/// the dial value is "the lowest criticality that still escalates," a threshold compared with <c>≥</c>. A
/// coarse ordered enum, deliberately not a float (false precision, §DA-2). There is deliberately no "never"
/// level (§3.3): <c>Critical</c> already means "fully autonomous, floors only".
/// </summary>
public enum EscalationThreshold
{
    /// <summary>Most cautious dial value: escalate ~everything (≡ the interactive halt-at-every-gate flow).</summary>
    Low,

    /// <summary>Conservative: escalate moderate/high/critical; best-guess only low.</summary>
    Moderate,

    /// <summary>Balanced — the recommended <c>--autonomous</c> default (§10 N): escalate high/critical.</summary>
    High,

    /// <summary>Fully autonomous: best-guess low/moderate/high judgment calls; escalate only critical (floors always escalate).</summary>
    Critical
}

/// <summary>
/// The <c>review-gate</c> resolution (doc 12 §5.2) — a FLOOR, not a criticality level.
/// <see cref="Escalate"/> is the default (halt the wave; a human must run <c>/guardrails-review</c>);
/// <see cref="ProceedUnreviewed"/> is the explicit named opt-in (Option P) that runs an unreviewed wave with
/// its recorded teeth (distinct non-zero exit, <c>mergeOnSuccess</c> off, GR2040 on the forbidden compound).
/// The harness NEVER self-attests a review, at any dial setting.
/// </summary>
public enum ReviewGateDecision
{
    /// <summary>Default: halt the wave and escalate for a human review pass.</summary>
    Escalate,

    /// <summary>Explicit named opt-in: run the wave unreviewed, recorded indelibly (Option P, §5.2).</summary>
    ProceedUnreviewed
}
