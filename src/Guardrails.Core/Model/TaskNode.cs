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

    /// <summary>Exclusive-workspace flag; null = default by action kind (prompt → true). (Honored from M4.)</summary>
    public bool? Exclusive { get; init; }

    /// <summary>
    /// Workspace-relative file paths whose SHA-256 (over raw bytes) the harness records into this
    /// task's state fragment after a successful action, under
    /// <c>{ "&lt;taskId&gt;": { "fileHashes": { "&lt;path&gt;": "&lt;hex&gt;" } } }</c> (SSOT §3 /
    /// issue #46). The hash is computed in code — the action agent never shells out — so a
    /// downstream <c>tests-untouched</c> guardrail can recompute (e.g. <c>Get-FileHash</c>) and
    /// detect modification. Empty when the task declares none; never null.
    /// </summary>
    public IReadOnlyList<string> CaptureHashes { get; init; } = [];

    /// <summary>The resolved action for this task.</summary>
    public required ActionDefinition Action { get; init; }

    /// <summary>The resolved guardrails, in filename sort order. At least one (validated).</summary>
    public required IReadOnlyList<GuardrailDefinition> Guardrails { get; init; }
}
