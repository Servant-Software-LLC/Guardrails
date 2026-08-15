namespace Guardrails.Core.Model;

/// <summary>
/// The OPTIONAL <c>tiering</c> block of <c>guardrails.json</c> (SSOT §2/§3, issue #225): the plan-wide
/// difficulty default that covers every task left untagged — including one hand-added to the folder
/// after breakdown, which no <c>/plan-breakdown</c> run ever saw.
///
/// <para>The whole block being ABSENT ⇒ <see cref="RunConfig.Tiering"/> is <c>null</c> ⇒ NO default is
/// fabricated and every untagged task resolves to a <c>null</c> tier. That is the load-bearing additive
/// guarantee for a single-model user (charter §C: "no <c>action.tier</c>, no <c>tiering</c> block, no
/// report lines"): a hard-coded fallback here would silently tier plans that never asked to be
/// tiered.</para>
///
/// <para>Nothing ROUTES on a tier in Stage 1 — this stage only lets a plan SAY what it has and holds it
/// to that. The resolver is Stage 2 (#226).</para>
/// </summary>
public sealed record TieringConfig
{
    /// <summary>
    /// The tier applied to any task that declares no <c>action.tier</c> of its own (<c>easy</c>|
    /// <c>medium</c>|<c>hard</c>). Bound VERBATIM from JSON — an unrecognized value is NOT normalized
    /// away here but reported by the validator (GR2043), naming the bad value. Null ⇒ the block was
    /// present but declared no default ⇒ untagged tasks stay untagged.
    /// </summary>
    public string? DefaultTier { get; init; }

    /// <summary>
    /// The OPTIONAL <c>tiering.verifier</c> sub-block (SSOT §2/§9.6, DoR §6.5.1). Null ⇒ the key was
    /// absent ⇒ no verifier floor, and the judge's rung is chosen entirely by the §6.5 rule.
    /// </summary>
    public TieringVerifierConfig? Verifier { get; init; }
}

/// <summary>
/// The OPTIONAL <c>tiering.verifier</c> block (SSOT §2, DoR §6.5.1 — settled OD-H). It carries exactly
/// one v1 key, and the distinction that key encodes is the whole reason the block exists.
///
/// <para><b><see cref="MinTier"/> is a FLOOR, not a default.</b> It NEVER selects the judge's rung — the
/// rule ("the judge's rung = the actor's effective rung, bumped one STRENGTH rank when the actor is
/// weak") still chooses. The floor only REFUSES a result that came out below it, and it can only ever
/// raise, never lower. A plan-wide <c>easy</c> default would drag every judge down; a plan-wide
/// <c>easy</c> floor does nothing at all. That asymmetry is the setting.</para>
///
/// <para>Nothing RESOLVES against it yet — there is no resolver — so this stage only proves it parses,
/// validates (GR2043 on an unrecognised token) and round-trips.</para>
/// </summary>
public sealed record TieringVerifierConfig
{
    /// <summary>
    /// The rung the resolved judge may never end up BELOW (<c>easy</c>|<c>medium</c>|<c>hard</c>). Bound
    /// VERBATIM from JSON — an unrecognised value is reported by the validator (GR2043) naming the bad
    /// value, not normalised away here. Null ⇒ the key was absent ⇒ no floor.
    ///
    /// <para>When the floor cannot be met without a <c>costly: true</c> block it DEGRADES — the judge
    /// stays at the best non-costly result and an advisory fires. It is deliberately NOT an error: an
    /// unsatisfiable ACTOR tier halts (GR2048) because an actor route is load-bearing, while an
    /// unsatisfiable VERIFIER floor is advisory, and a GR code is a thing that can fail a build.</para>
    /// </summary>
    public string? MinTier { get; init; }
}

/// <summary>
/// The single source of truth for the difficulty-tier tokens (SSOT §3, issue #225): <c>easy</c>,
/// <c>medium</c>, <c>hard</c>. Shared by the loader (which only propagates a RECOGNIZED plan-wide
/// default) and the validator (which reports an unrecognized one as GR2043), so the spelling never forks.
/// </summary>
public static class ActionTiers
{
    /// <summary>The cheapest tier — mechanical work a small model can do.</summary>
    public const string Easy = "easy";

    /// <summary>The middle tier — the default weight of ordinary implementation work.</summary>
    public const string Medium = "medium";

    /// <summary>The most demanding tier — design-sensitive or cross-cutting work.</summary>
    public const string Hard = "hard";

    /// <summary>The recognized tokens, in ascending difficulty — the exact set a tier may spell.</summary>
    public static IReadOnlyList<string> All { get; } = [Easy, Medium, Hard];

    /// <summary>The recognized tokens as a comma-separated list, for diagnostic messages.</summary>
    public static string TokenList => string.Join(", ", All.Select(t => $"'{t}'"));

    /// <summary>
    /// True when <paramref name="tier"/> is EXACTLY one of the three tokens — ordinal, no trimming and
    /// no case-folding. A tier is bound verbatim from the manifest (the GR2030 <c>action.model</c>
    /// doctrine — preserve the malformed signal, let the validator judge it), so <c>"hard "</c> with a
    /// stray trailing space is a typo the operator gets told about rather than one the loader silently
    /// swallows.
    /// </summary>
    public static bool IsRecognized(string? tier) =>
        tier is not null && All.Contains(tier, StringComparer.Ordinal);
}
