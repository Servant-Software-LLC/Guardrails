using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardrails.Core.Loading;

/// <summary>Raw shape of <c>guardrails.json</c> for deserialization (SSOT §2).</summary>
internal sealed class RawRunConfig
{
    public int? Version { get; set; }
    public int? MaxParallelism { get; set; }
    public int? DefaultRetries { get; set; }
    public decimal? MaxCostUsd { get; set; }
    public int? DefaultTimeoutSeconds { get; set; }
    public int? TransientPauseBudgetSeconds { get; set; }
    public string? GuardrailMode { get; set; }
    public string? Workspace { get; set; }
    public string? WorktreeRoot { get; set; }
    public bool? RunOnCurrentBranch { get; set; }
    public bool? MergeOnSuccess { get; set; }
    public bool? TriageAutoFile { get; set; }
    public string? AutonomyPolicy { get; set; }
    public bool? AutoBreakdown { get; set; }
    public bool? PreserveAttemptsForSalvage { get; set; }

    // The optional criticality-dial block (issue #361, doc 12 §3.4). null ⇒ the block was ABSENT ⇒ the dial
    // is inert (RunConfig.Autonomy stays null). A present block (even "{}") binds a non-null instance; the
    // raw→model mapping in PlanLoader is stubbed until the implement task wires the real parse.
    public RawAutonomyConfig? Autonomy { get; set; }

    // The optional model-tiering block (SSOT §2/§3, issue #225). null ⇒ the block was ABSENT ⇒ no plan-wide
    // default tier exists and untagged tasks stay untagged (RunConfig.Tiering stays null).
    public RawTieringConfig? Tiering { get; set; }

    public Dictionary<string, List<string>>? Interpreters { get; set; }

    // promptRunners is a heterogeneous map: a "default" string pointer plus named
    // runner-config objects (RawPromptRunner). Bound as raw JSON and walked property by
    // property so the "default" pointer and the runner objects can be told apart.
    public JsonElement? PromptRunners { get; set; }
}

/// <summary>
/// Raw shape of the optional <c>autonomy</c> block (issue #361, doc 12 §3.4). Every field is optional; the
/// whole block absent ⇒ <see cref="RawRunConfig.Autonomy"/> is null ⇒ the dial is inert. The raw→model
/// mapping, its decided defaults (§10 I/N), and validation (GR2039/GR2040) are authored by the implement
/// task — this is only the deserialization target.
/// </summary>
internal sealed class RawAutonomyConfig
{
    public string? EscalationThreshold { get; set; }

    // gateThresholds keys are the three gate types: needs-human / wave-checkpoint (criticality levels) and
    // review-gate (the escalate/proceed-unreviewed acknowledgment). Bound raw as string values; the mapping
    // and its GR2039 value check are the implement task's job.
    public Dictionary<string, string>? GateThresholds { get; set; }

    public RawBlockerRetry? BlockerRetry { get; set; }
    public int? MaxJudgeWidenings { get; set; }
}

/// <summary>
/// Raw shape of the optional <c>tiering</c> block (SSOT §2/§3, issue #225). <c>defaultTier</c> is bound
/// VERBATIM — an unrecognized value reaches the validator's GR2043 check faithfully rather than being
/// normalized into validity, the same doctrine <c>action.model</c> follows for GR2030.
/// </summary>
internal sealed class RawTieringConfig
{
    public string? DefaultTier { get; set; }

    // The optional verifier sub-block (SSOT §2, DoR §6.5.1). null ⇒ the key was ABSENT ⇒ no floor.
    public RawTieringVerifier? Verifier { get; set; }
}

/// <summary>
/// Raw shape of the optional <c>tiering.verifier</c> sub-block (SSOT §2, DoR §6.5.1). <c>minTier</c> is
/// bound VERBATIM for the same reason <c>defaultTier</c> is: an unrecognized value must reach the
/// validator's GR2043 check as written rather than being normalized into validity.
/// </summary>
internal sealed class RawTieringVerifier
{
    public string? MinTier { get; set; }
}

/// <summary>Raw shape of the <c>autonomy.blockerRetry</c> sub-block (doc 12 §3.4/§4.2).</summary>
internal sealed class RawBlockerRetry
{
    public int? MaxAttempts { get; set; }
    public int? TotalWaitSeconds { get; set; }
}

/// <summary>Raw shape of one <c>promptRunners.&lt;name&gt;</c> config object (SSOT §2/§9).</summary>
internal sealed class RawPromptRunner
{
    public string? Command { get; set; }
    public string? PermissionMode { get; set; }
    public List<string>? AllowedTools { get; set; }
    public int? MaxTurns { get; set; }
    public string? Model { get; set; }
    public List<string>? ExtraArgs { get; set; }

    // Output-token cap (issue #114). null = harness default (PromptRunnerSettings.DefaultMaxOutputTokens).
    public int? MaxOutputTokens { get; set; }

    // General env passthrough (issue #114). null = none.
    public Dictionary<string, string>? Env { get; set; }

    // Which runner IMPLEMENTATION serves this block (SSOT §9, issue #224). null = ABSENT = claude, which is
    // what keeps the discriminator additive. An unrecognised token is reported by PlanLoader.ReadKind; a
    // RECOGNISED-but-unimplemented one by PlanValidator (both GR2044 — see that check for why the two
    // halves live where they do).
    public string? Kind { get; set; }

    // The opaque per-block thinking-effort knob (SSOT §9, issue #201). Typed as string? exactly like
    // Model, whose SHAPE it mirrors: both are opaque vendor tokens with no enumerable valid set, both are
    // shape-checked by the validator (GR2050 / GR2030), and neither is normalised on the way in.
    public string? Effort { get; set; }

    // Axes 1 and 2 of 3 (SSOT §9, charter Decision 7) are bound as RAW JSON, not as bool?/int?, precisely
    // because their malformed form is a TYPE error ("costly": "yes", "strength": "high"): a typed binding
    // would throw mid-deserialization and surface as a generic parse failure naming a CLR type instead of
    // the axis. Held raw, the loader can name the axis and keep loading so the rest of validate still
    // reports. null = the key was ABSENT.
    public JsonElement? Costly { get; set; }
    public JsonElement? Strength { get; set; }

    // Axis 3 of 3. A string, because its malformed form is a bad TOKEN, not a bad type.
    public string? Specialization { get; set; }

    // Per-model routing guidance (SSOT §9, issue #224). null = the key was absent.
    public RawPromptRunnerRouting? Routing { get; set; }

    public RawPromptRunnerOverrides? GuardrailOverrides { get; set; }
}

/// <summary>
/// Raw shape of the optional <c>promptRunners.&lt;name&gt;.routing</c> block (SSOT §9, issue #224). Its
/// presence opts the block into tier resolution; <c>tiers</c> is the machine-consumed half.
/// </summary>
internal sealed class RawPromptRunnerRouting
{
    // `tiers` is bound as RAW JSON, not List<string>?, for the same reason the costly/strength axes are:
    // its malformed forms include TYPE errors ("tiers": "medium", "tiers": [1, 2]), and a typed binding
    // would throw mid-deserialization and surface as a generic parse failure naming a CLR type instead of
    // the key. Held raw, the loader can name the key and keep loading so the rest of validate still
    // reports. null = the key was ABSENT — which is itself GR2047, since `tiers` is REQUIRED here.
    public JsonElement? Tiers { get; set; }

    public string? Notes { get; set; }
    public string? Guidance { get; set; }
    public List<string>? Tags { get; set; }

    // `rank` is a RETIRED key (settled OD-F) and is deliberately NOT a property here: declaring one would
    // model a value nothing may honour, which is the silent acceptance the retirement exists to prevent.
    // Unknown keys land here instead, where the loader can SEE a stale `rank` and warn without any code
    // path being able to read it as ordering.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Raw shape of a <c>guardrailOverrides</c> sub-block — every field optional (partial override).</summary>
internal sealed class RawPromptRunnerOverrides
{
    public string? PermissionMode { get; set; }
    public List<string>? AllowedTools { get; set; }
    public int? MaxTurns { get; set; }
    public string? Model { get; set; }
    public List<string>? ExtraArgs { get; set; }
    public int? MaxOutputTokens { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>Raw shape of <c>tasks/&lt;id&gt;/task.json</c> for deserialization (SSOT §3).</summary>
internal sealed class RawTask
{
    public string? Description { get; set; }

    // Optional stable identity that survives renumbering/slug edits across regenerations
    // (SSOT §11 / issue #5). Reserved for the regeneration merge; not yet consumed at runtime.
    public string? StableId { get; set; }

    public List<string>? DependsOn { get; set; }
    public int? Retries { get; set; }
    public int? TimeoutSeconds { get; set; }

    // Terminal integration gate marker (plan 08 M2, SSOT §3.3). Default false.
    public bool? IntegrationGate { get; set; }

    // Write-scope glob list (plan 08 §2/§3.4, SSOT §3.4). Null = absent = off-switch.
    public List<string>? WriteScope { get; set; }

    // Staging-output mappings for autonomous .claude/ delivery (SSOT §3.5, issue #130).
    // Null = absent = no staging. A present-but-malformed list is GR2024.
    public List<RawStagingOutput>? StagingOutputs { get; set; }

    public RawAction? Action { get; set; }
}

/// <summary>Raw shape of one <c>stagingOutputs[]</c> entry in <c>task.json</c> (SSOT §3.5).</summary>
internal sealed class RawStagingOutput
{
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>Raw shape of the optional <c>action</c> block in <c>task.json</c> (SSOT §3).</summary>
internal sealed class RawAction
{
    public string? Path { get; set; }
    public List<string>? Args { get; set; }
    public string? Runner { get; set; }
    public int? MaxTurns { get; set; }
    public string? Model { get; set; }

    // Difficulty tag (SSOT §3, issue #225): easy|medium|hard. Bound VERBATIM (no trim/case-fold) so an
    // unrecognized value reaches the validator's GR2043 check as written — the same "preserve the
    // malformed signal" doctrine Model follows for GR2030.
    public string? Tier { get; set; }

    // Per-task thinking-effort override (SSOT §3, issue #201). Mirrors Model's shape exactly — an opaque
    // vendor token, bound verbatim, shape-checked by the validator (GR2050).
    public string? Effort { get; set; }

    public int? TimeoutSeconds { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>Raw shape of a deterministic guardrail metadata sidecar (SSOT §4.1).</summary>
internal sealed class RawGuardrailSidecar
{
    public string? Description { get; set; }
    public List<string>? Args { get; set; }
    public int? TimeoutSeconds { get; set; }

    // Optional scope tag (plan 08 M2, SSOT §4.3). "integration" marks the guardrail as a
    // whole-repo soundness check at an integrationGate sink.
    public string? Scope { get; set; }

    // Optional author-set expected wall-clock duration in seconds (SSOT §4.1, issue #331). Surfaced
    // as an "expected ~Xm" hint in the running-guardrail heartbeat; must be > 0 when present (GR2036).
    public int? ExpectedDurationSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
