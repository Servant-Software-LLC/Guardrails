namespace Guardrails.Core.Execution;

/// <summary>
/// Identifies the plan's shared integration worktree — the long-lived worktree that receives
/// fast-forward merges from each task segment (SSOT §1). Created once per run by
/// <see cref="IWorktreeProvider.CreateIntegration"/> and shared across all tasks in the run.
/// </summary>
public sealed class IntegrationHandle
{
    /// <summary>Absolute path to the integration worktree directory.</summary>
    public string IntegrationWorktreePath { get; init; } = "";

    /// <summary>Name of the plan branch (e.g. <c>guardrails/my-plan</c>) that the integration worktree tracks.</summary>
    public string PlanBranchName { get; init; } = "";

    /// <summary>The user's original branch at the time the run started (restored on failure or completion).</summary>
    public string OriginalBranch { get; init; } = "";

    /// <summary>The HEAD sha of the user's original branch at run start, used to detect upstream drift.</summary>
    public string OriginalHeadSha { get; init; } = "";

    /// <summary>The harness-generated run identifier for this execution (scopes segment branch names).</summary>
    public string RunId { get; init; } = "";
}
