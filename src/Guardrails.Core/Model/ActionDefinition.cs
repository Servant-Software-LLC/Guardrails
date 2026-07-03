namespace Guardrails.Core.Model;

/// <summary>
/// A resolved action for a task — the single action file plus the settings that
/// govern how it runs. SSOT §3 ("action" block). Discovered by convention
/// (one <c>action.*</c> file) or pointed at explicitly via <c>task.json action.path</c>.
/// </summary>
public sealed record ActionDefinition
{
    /// <summary>Absolute path to the action file on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Whether this action runs as a script/executable or an LLM prompt.</summary>
    public required ActionKind Kind { get; init; }

    /// <summary>Arguments for deterministic (script/executable) actions. Empty for prompt actions.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Prompt-runner name for prompt actions; null = use <c>promptRunners.default</c>. M5.</summary>
    public string? Runner { get; init; }

    /// <summary>Turn ceiling override for prompt actions; null = inherit from the runner config / frontmatter.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Model override for prompt actions; null = inherit from the runner config default.</summary>
    public string? Model { get; init; }

    /// <summary>Per-action timeout ceiling in seconds; null = inherit from task/config.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Overrides the config workspace as the process cwd. Relative to the plan dir; null = config workspace.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables injected into this action's process.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>();
}
