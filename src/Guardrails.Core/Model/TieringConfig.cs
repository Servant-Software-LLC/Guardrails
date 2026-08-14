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
