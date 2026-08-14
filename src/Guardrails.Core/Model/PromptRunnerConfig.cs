namespace Guardrails.Core.Model;

/// <summary>
/// One named prompt-runner configuration from <c>guardrails.json: promptRunners</c>
/// (SSOT §2/§9). Carries the data a runner needs; the runner CLASS
/// (<c>ClaudePromptRunner</c>) carries the flag-spelling and output-parsing code, so a
/// new CLI is a new class + a config block, never a schema change.
/// </summary>
public sealed record PromptRunnerConfig
{
    /// <summary>The runner's name (the <c>promptRunners</c> map key), e.g. "claude".</summary>
    public required string Name { get; init; }

    /// <summary>The executable to launch (e.g. "claude"). Defaults to the runner name.</summary>
    public required string Command { get; init; }

    /// <summary>The base settings used for action prompts.</summary>
    public required PromptRunnerSettings Settings { get; init; }

    /// <summary>
    /// Which runner IMPLEMENTATION serves this block (SSOT §9, issue #224). ABSENT ⇒
    /// <see cref="PromptRunnerKind.Claude"/>, which is what keeps the provider-registry change
    /// ADDITIVE: every config written before the discriminator existed is implicitly Claude and must
    /// validate and run unchanged.
    /// </summary>
    public PromptRunnerKind Kind { get; init; } = PromptRunnerKinds.Default;

    /// <summary>
    /// Axis 1 of 3 (charter Decision 7): does spending on this model warrant restraint? OPTIONAL and
    /// TRI-STATE — <c>null</c> means "not stated", deliberately distinct from an explicit <c>false</c>
    /// ("stated to be cheap"). Nothing READS it in Stage 1; the resolver (#226) is the first consumer.
    /// </summary>
    public bool? Costly { get; init; }

    /// <summary>
    /// Axis 2 of 3: relative capability, higher = stronger, minimum 1 (a non-positive value is a
    /// validation error). OPTIONAL; <c>null</c> = not stated. This is the ORDERING key — candidates for
    /// a tier are ordered by ASCENDING strength, so the weakest model that can serve the tier goes
    /// first. <c>routing.rank</c> is retired precisely because this axis subsumes it.
    /// </summary>
    public int? Strength { get; init; }

    /// <summary>
    /// Axis 3 of 3: what the model is FOR. OPTIONAL — an absent key resolves to
    /// <see cref="PromptRunnerSpecialization.Unspecified"/>, the enum member that exists so
    /// "not stated" is a first-class, writable value rather than a null.
    /// </summary>
    public PromptRunnerSpecialization Specialization { get; init; } = PromptRunnerSpecialization.Unspecified;

    /// <summary>
    /// Per-model ROUTING GUIDANCE (SSOT §9, issue #224): prose and/or tags describing the work this
    /// model should take on. Null = the block carried no <c>routing</c> key. Stage 1 requires only
    /// that it exists, validates, and round-trips — the static resolver (#226) is its first reader.
    /// </summary>
    public PromptRunnerRouting? Routing { get; init; }

    /// <summary>
    /// A partial override block applied for GUARDRAIL prompts only — the tighter,
    /// read-mostly verifier profile (SSOT §2 <c>guardrailOverrides</c>). Null fields
    /// inherit from <see cref="Settings"/>. Null = no overrides (guardrails use the base).
    /// </summary>
    public PromptRunnerOverrides? GuardrailOverrides { get; init; }

    /// <summary>The effective settings for a prompt of the given kind (base, or base + guardrail overrides).</summary>
    public PromptRunnerSettings EffectiveSettings(bool isGuardrail) =>
        isGuardrail && GuardrailOverrides is not null
            ? Settings.With(GuardrailOverrides)
            : Settings;
}

/// <summary>
/// The fully-resolved knobs that govern one prompt invocation (SSOT §2). All fields are
/// concrete (no nulls) so the runner does not re-apply defaults.
/// </summary>
public sealed record PromptRunnerSettings
{
    /// <summary>Permission mode passed to the runner (e.g. "acceptEdits", "default"). Default "acceptEdits".</summary>
    public string PermissionMode { get; init; } = "acceptEdits";

    /// <summary>Tools the runner is allowed to use. Empty = pass no <c>--allowedTools</c> (runner default).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Turn ceiling for the runner. Default 50 (SSOT §2 example).</summary>
    public int MaxTurns { get; init; } = 50;

    /// <summary>Model override; null = the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>Extra CLI arguments appended verbatim. Empty by default.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>
    /// The output-token cap handed to the runner (issue #114). Defaults to
    /// <see cref="DefaultMaxOutputTokens"/> — deliberately ABOVE Claude Code's 32 000 default so a
    /// well-formed single-response task is not blocked by the cap the harness never used to configure.
    /// The runner CLASS translates this into the CLI's env var
    /// (<c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c>) — the env-var NAME stays quarantined in
    /// <see cref="Prompts.ClaudePromptRunner"/>, never in this model. A non-positive value is a
    /// validation error (GR2023).
    /// </summary>
    public int MaxOutputTokens { get; init; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Extra environment variables passed verbatim to the runner process (SSOT §2/§9, issue #114) —
    /// a general passthrough for runner/provider knobs the harness does not model. These overlay (and
    /// may override) the harness <c>GUARDRAILS_*</c> env only for keys the user explicitly sets.
    /// Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The default output-token cap (issue #114): 64 000, double Claude Code's 32 000 default.</summary>
    public const int DefaultMaxOutputTokens = 64_000;

    /// <summary>Return a copy with any non-null fields of <paramref name="overrides"/> applied.</summary>
    public PromptRunnerSettings With(PromptRunnerOverrides overrides) => this with
    {
        PermissionMode = overrides.PermissionMode ?? PermissionMode,
        AllowedTools = overrides.AllowedTools ?? AllowedTools,
        MaxTurns = overrides.MaxTurns ?? MaxTurns,
        Model = overrides.Model ?? Model,
        ExtraArgs = overrides.ExtraArgs ?? ExtraArgs,
        MaxOutputTokens = overrides.MaxOutputTokens ?? MaxOutputTokens,
        Env = overrides.Env ?? Env
    };
}

/// <summary>
/// A partial settings block: every field is nullable so only the keys actually present in
/// <c>guardrailOverrides</c> override the base (SSOT §2). Used solely for the merge.
/// </summary>
public sealed record PromptRunnerOverrides
{
    public string? PermissionMode { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public int? MaxTurns { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string>? ExtraArgs { get; init; }
    public int? MaxOutputTokens { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Which runner IMPLEMENTATION a <c>promptRunners</c> block selects (SSOT §9, issue #224). Only
/// <see cref="Claude"/> has a concrete runner in Stage 1 — the others are the seam #223 fills, and
/// asking for one of them fails registry construction with an actionable message rather than
/// silently falling back to Claude.
/// </summary>
public enum PromptRunnerKind
{
    /// <summary>Claude Code (<c>ClaudePromptRunner</c>) — the DEFAULT for a block with no <c>kind</c>.</summary>
    Claude,

    /// <summary>OpenAI Codex CLI. Declarable in Stage 1; no concrete runner until #223.</summary>
    Codex,

    /// <summary>An OpenRouter-hosted model. Declarable in Stage 1; no concrete runner until #223.</summary>
    OpenRouter,

    /// <summary>A locally-hosted model. Declarable in Stage 1; no concrete runner until #223.</summary>
    Local
}

/// <summary>
/// The single source of truth for the <see cref="PromptRunnerKind"/> wire tokens
/// (<c>claude</c> / <c>codex</c> / <c>openrouter</c> / <c>local</c>), mirroring
/// <see cref="AutonomyPolicies"/>. Shared by the loader, validation, and the <c>providers init</c>
/// generator so the spelling never forks.
/// </summary>
public static class PromptRunnerKinds
{
    /// <summary>The kind an omitted <c>kind</c> key resolves to — the additive-compatibility default.</summary>
    public const PromptRunnerKind Default = PromptRunnerKind.Claude;

    /// <summary>The canonical wire token for <paramref name="kind"/> (e.g. <see cref="PromptRunnerKind.OpenRouter"/> ⇒ <c>openrouter</c>).</summary>
    public static string Token(PromptRunnerKind kind) =>
        throw new NotImplementedException($"PromptRunnerKinds.Token({kind}) is not implemented yet (#224).");
}

/// <summary>
/// What a model is FOR (charter Decision 7 / DoR §4.1) — the third per-model axis. Read by the
/// Stage 2 resolver (#226); Stage 1 only declares and validates it.
/// </summary>
public enum PromptRunnerSpecialization
{
    /// <summary>Not stated. What an absent <c>specialization</c> key resolves to, and legal to write explicitly.</summary>
    Unspecified,

    /// <summary>Writing and editing code.</summary>
    Coding,

    /// <summary>Planning, decomposition, and reasoning-heavy work (wire token <c>planning-reasoning</c>).</summary>
    PlanningReasoning,

    /// <summary>No particular strength — a generalist.</summary>
    General
}

/// <summary>
/// The single source of truth for the <see cref="PromptRunnerSpecialization"/> wire tokens
/// (<c>coding</c> / <c>planning-reasoning</c> / <c>general</c> / <c>unspecified</c>). The
/// hyphenated <c>planning-reasoning</c> spelling is why this mapping is explicit rather than
/// <c>Enum.ToString</c>.
/// </summary>
public static class PromptRunnerSpecializations
{
    /// <summary>The canonical wire token for <paramref name="specialization"/>.</summary>
    public static string Token(PromptRunnerSpecialization specialization) =>
        throw new NotImplementedException($"PromptRunnerSpecializations.Token({specialization}) is not implemented yet (#224).");
}

/// <summary>
/// The optional per-model <c>routing</c> block (SSOT §9, issue #224): guidance about the work a model
/// should take on. Nothing consumes it in Stage 1 — the static resolver (#226) is the first reader, so
/// this stage only proves it parses, validates, and survives a serialise/parse cycle.
///
/// <para><b><c>rank</c> is deliberately absent.</b> It is a RETIRED key (settled OD-F): ordering is
/// ascending <see cref="PromptRunnerConfig.Strength"/>, not a hand-written rank. A config still
/// carrying <c>routing.rank</c> gets a retired-field WARNING — modelling it here would be the silent
/// acceptance the warning exists to prevent.</para>
/// </summary>
public sealed record PromptRunnerRouting
{
    /// <summary>Free prose describing the work this model suits. Null = the key was absent.</summary>
    public string? Guidance { get; init; }

    /// <summary>Machine-comparable guidance tags. Empty = the key was absent.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
