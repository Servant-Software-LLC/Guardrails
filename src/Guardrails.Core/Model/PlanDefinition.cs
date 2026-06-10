namespace Guardrails.Core.Model;

/// <summary>
/// A fully loaded plan folder: its run config plus every task. SSOT §1.
/// </summary>
public sealed record PlanDefinition
{
    /// <summary>Absolute path to the plan folder root (contains <c>guardrails.json</c>).</summary>
    public required string PlanDirectory { get; init; }

    /// <summary>The deserialized run configuration with defaults applied.</summary>
    public required RunConfig Config { get; init; }

    /// <summary>Tasks in folder-name (ordinal) order. Keyed lookups use <see cref="TaskNode.Id"/>.</summary>
    public required IReadOnlyList<TaskNode> Tasks { get; init; }

    /// <summary>The absolute resolved workspace (cwd for child processes), from <see cref="RunConfig.Workspace"/>.</summary>
    public required string Workspace { get; init; }
}
