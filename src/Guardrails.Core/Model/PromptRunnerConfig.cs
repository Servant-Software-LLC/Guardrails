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
