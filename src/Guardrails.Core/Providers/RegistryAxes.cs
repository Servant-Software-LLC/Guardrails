using Guardrails.Core.Model;

namespace Guardrails.Core.Providers;

/// <summary>
/// The four registry keys <c>guardrails providers init</c> solicits (SSOT §9.7, DoR §4.3), and the
/// <c>//</c> comment each one is introduced by.
///
/// <para><b>Every legal-value list here is COMPUTED from the enum that validation enforces</b> —
/// <see cref="PromptRunnerSpecializations.TokenList"/>, <see cref="ActionTiers.TokenList"/> — never
/// retyped. The whole point of the verb is that the enums become discoverable in the file being edited;
/// a hand-copied list that drifts from the validator would make the generator actively misleading, which
/// is worse than the docs-only status quo it replaces.</para>
///
/// <para><b>Why <c>null</c> is the value written for an absent axis.</b> The loader treats a missing key
/// and an explicit JSON <c>null</c> identically as "not stated" (<c>PlanLoader.AbsentAxis</c>), so
/// writing <c>null</c> changes NOTHING semantically while turning a remembered schema into a filled-in
/// form. It also keeps the tri-state payoff alive across re-runs: the block is still UNSTATED after
/// annotation, so the verb keeps naming it and asking until a human writes a real answer.</para>
/// </summary>
public static class RegistryAxes
{
    /// <summary>Axis 1 of 3 — whether spending on this model warrants restraint. Tri-state.</summary>
    public const string Costly = "costly";

    /// <summary>Axis 2 of 3 — relative capability, and the only total order.</summary>
    public const string Strength = "strength";

    /// <summary>Axis 3 of 3 — what the model is for.</summary>
    public const string Specialization = "specialization";

    /// <summary>The tier-eligibility block. Not an axis, but solicited alongside them for the same reason.</summary>
    public const string Routing = "routing";

    /// <summary>
    /// The value written for an absent key: <c>null</c>, i.e. "not stated". Deliberately NOT a guessed
    /// <c>false</c>/<c>1</c>/<c>"general"</c> — answering on the user's behalf is the failure this verb
    /// exists to prevent, one layer up from "never fabricate a model id".
    /// </summary>
    public const string UnstatedValue = "null";

    /// <summary>
    /// The four solicited keys in canonical emission order, each with the comment lines that introduce
    /// it. Order is fixed so a second run against a partially-answered block appends the remainder in
    /// the same sequence a first run would have.
    /// </summary>
    public static IReadOnlyList<RegistryAxisSpec> All { get; } =
    [
        new(Costly,
        [
            "// costly: true | false | null — null means NOT STATED, which is NOT the same as false.",
            "//   Only `true` reserves this model so that ONLY A HUMAN may assign it (an explicit",
            "//   action.runner / action.model pin, or the `default` pointer): the harness never chooses",
            "//   a costly block, at any tier, with no override and no --force."
        ]),
        new(Strength,
        [
            "// strength: an integer >= 1 | null — higher = stronger, and the only total order.",
            "//   Candidates for a tier are taken in ASCENDING strength (the weakest model that can serve",
            "//   the tier goes first); null means NOT STATED, and sorts last."
        ]),
        new(Specialization,
        [
            "// specialization: what the model is FOR. Legal values:",
            $"//   {PromptRunnerSpecializations.TokenList} — or null.",
            "//   A preference used to break ties among candidates, never an ordering. Writing",
            "//   'unspecified' explicitly is an answer; null is not."
        ]),
        new(Routing,
        [
            "// routing: null | { \"tiers\": [ ... ] } — null means this block is NEVER a tier target",
            "//   (reachable only by an explicit action.runner / action.model pin, or as the `default`",
            $"//   pointer). Opt in with a non-empty subset of {ActionTiers.TokenList}. 'tiers' is the",
            "//   only key tier resolution reads; 'notes' is prose for humans and is never parsed."
        ])
    ];

    /// <summary>
    /// The stable opening clause of the "could not enumerate" note for <paramref name="kindToken"/> — the
    /// string both the emitter and the idempotency check use, so a second run recognises the note a first
    /// run wrote instead of stacking another copy beside it.
    /// </summary>
    public static string CouldNotEnumerateMarker(string kindToken) =>
        $"could not enumerate models for kind '{kindToken}'";

    /// <summary>
    /// The full "could not enumerate" note (DoR §4.3 ruling 2): what happened, what the user must do, and
    /// — the part worth carrying in the file rather than only in the docs — WHY the generator refuses to
    /// help by guessing.
    /// </summary>
    public static IReadOnlyList<string> CouldNotEnumerateNote(string kindToken) =>
    [
        $"// {CouldNotEnumerateMarker(kindToken)} — this build has no model-list surface",
        "//   for it, so `guardrails providers init` added NO blocks. Add them by hand; the legal axis",
        "//   values are commented above. A registry entry is a ROUTING TARGET, not documentation: a",
        "//   fabricated or stale model id would be spent against at a model that may not exist, so the",
        "//   generator never invents one."
    ];
}

/// <summary>One solicited key and the comment that introduces it. See <see cref="RegistryAxes"/>.</summary>
/// <param name="Name">The JSON key, matched case-insensitively (as the loader binds it).</param>
/// <param name="CommentLines">The <c>//</c> lines emitted above the key, already prefixed.</param>
public sealed record RegistryAxisSpec(string Name, IReadOnlyList<string> CommentLines);
