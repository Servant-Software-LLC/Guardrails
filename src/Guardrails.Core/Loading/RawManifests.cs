using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardrails.Core.Loading;

/// <summary>Raw shape of <c>guardrails.json</c> for deserialization (SSOT §2).</summary>
internal sealed class RawRunConfig
{
    public int? Version { get; set; }
    public int? MaxParallelism { get; set; }
    public int? DefaultRetries { get; set; }
    public int? DefaultTimeoutSeconds { get; set; }
    public string? GuardrailMode { get; set; }
    public string? Workspace { get; set; }
    public Dictionary<string, List<string>>? Interpreters { get; set; }

    // promptRunners is a heterogeneous map: a "default" string pointer plus named
    // runner-config objects. M2 only needs the declared names + the default pointer,
    // so it is bound as raw JSON and inspected, not fully typed (full config = M5).
    public JsonElement? PromptRunners { get; set; }
}

/// <summary>Raw shape of <c>tasks/&lt;id&gt;/task.json</c> for deserialization (SSOT §3).</summary>
internal sealed class RawTask
{
    public string? Description { get; set; }
    public List<string>? DependsOn { get; set; }
    public int? Retries { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool? Exclusive { get; set; }
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
