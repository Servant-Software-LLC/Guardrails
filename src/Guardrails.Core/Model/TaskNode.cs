namespace Guardrails.Core.Model;

/// <summary>
/// A single task in the plan DAG. SSOT §3. The task id is its folder name.
/// </summary>
public sealed record TaskNode
{
    /// <summary>Task id = the task folder name (kebab-case, e.g. "01-write-greeting-script").</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Optional stable identity that survives renumbering/slug edits across regenerations
    /// (SSOT §11 / issue #5). Null when the plan does not declare one. Reserved for the
    /// regeneration merge; not yet consumed at runtime.
    /// </summary>
    public string? StableId { get; init; }

    /// <summary>Absolute path to the task folder.</summary>
    public required string Directory { get; init; }

    /// <summary>Required one-line human description (used in feedback and reporting).</summary>
    public required string Description { get; init; }

    /// <summary>Task ids this task depends on. May be empty; never null.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Retry override; null = use <c>defaultRetries</c>. (Retries are M4; carried for fidelity.)</summary>
    public int? Retries { get; init; }

    /// <summary>Whole-attempt timeout ceiling in seconds; null = use <c>defaultTimeoutSeconds</c>.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The resolved action for this task.</summary>
    public required ActionDefinition Action { get; init; }

    /// <summary>The resolved guardrails, in filename sort order. At least one (validated).</summary>
    public required IReadOnlyList<GuardrailDefinition> Guardrails { get; init; }

    /// <summary>
    /// When true, this task is the terminal integration gate for the plan (plan 08 M2, SSOT §3.3).
    /// A multi-leaf or fan-in plan must have exactly one such sink, which must carry at least one
    /// guardrail with <c>scope:"integration"</c>. Default false (no gate role).
    /// </summary>
    public bool IntegrationGate { get; init; }

    /// <summary>
    /// The declared write-scope for this task (plan 08 §2/§3.4, SSOT §3.4). Each entry is a
    /// glob pattern matched against <c>git diff --name-status</c> paths after the action runs.
    /// Null (absent in <c>task.json</c>) is the off-switch — no write-scope check runs.
    /// </summary>
    public IReadOnlyList<string>? WriteScope { get; init; }
}
