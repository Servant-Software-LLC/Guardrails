namespace Guardrails.Core.Execution;

/// <summary>
/// Identifies the plan's shared integration worktree — the long-lived worktree that receives
/// fast-forward merges from each task segment (SSOT §1). Created once per run by
/// <see cref="IWorktreeProvider.CreateIntegration"/> and shared across all tasks in the run.
/// </summary>
public sealed record IntegrationHandle
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

    /// <summary>
    /// The branch a delivery on THIS PLAN has already landed on (SSOT §7 <c>deliveryTarget</c>, issue #726),
    /// read from the journal at run start; null until any delivery has landed. Stamped by the Scheduler, not
    /// by <see cref="IWorktreeProvider.CreateIntegration"/>, which reads git and knows nothing of the journal.
    /// </summary>
    public string? RecordedDeliveryTarget { get; init; }

    /// <summary>
    /// The branch every delivery in this run must land on (issue #726): the branch an earlier delivery
    /// ALREADY landed on when this plan has one, else <see cref="OriginalBranch"/> — this process's run-start
    /// pin, which is the right answer only while nothing has landed yet.
    /// <para>
    /// <b>Why the two differ, and why it matters.</b> The pin is re-read from <c>HEAD</c> by every process,
    /// so a resume from another branch pins THAT branch. Before design 39 all delivery happened at run end,
    /// so a resume elsewhere at least kept a plan's work together; with per-wave delivery it silently split
    /// the work across two branches, and the #588 branch-moved check refused nothing because it compared the
    /// new pin against itself. Every delivery gate reads THIS property, never <see cref="OriginalBranch"/>.
    /// </para>
    /// </summary>
    public string DeliveryTarget =>
        RecordedDeliveryTarget is { Length: > 0 } recorded ? recorded : OriginalBranch;
}
