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
    public string? GuardrailMode { get; set; }
    public string? Workspace { get; set; }
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
    public bool? Exclusive { get; set; }

    // Workspace-relative files whose SHA-256 the harness records into state after a successful
    // action (issue #46) — the agent never computes the hash itself.
    public List<string>? CaptureHashes { get; set; }

    public RawAction? Action { get; set; }
}

/// <summary>Raw shape of the optional <c>action</c> block in <c>task.json</c> (SSOT §3).</summary>
internal sealed class RawAction
{
    public string? Path { get; set; }
    public List<string>? Args { get; set; }
    public string? Runner { get; set; }
    public int? MaxTurns { get; set; }
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
