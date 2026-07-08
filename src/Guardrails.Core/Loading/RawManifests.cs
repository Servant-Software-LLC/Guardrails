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
    public string? DriftPolicy { get; set; }
    public bool? PreserveAttemptsForSalvage { get; set; }
    public Dictionary<string, List<string>>? Interpreters { get; set; }

    // promptRunners is a heterogeneous map: a "default" string pointer plus named
    // runner-config objects (RawPromptRunner). Bound as raw JSON and walked property by
    // property so the "default" pointer and the runner objects can be told apart.
    public JsonElement? PromptRunners { get; set; }
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

    public RawPromptRunnerOverrides? GuardrailOverrides { get; set; }
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
