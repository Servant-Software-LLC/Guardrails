namespace Guardrails.Core.Execution;

/// <summary>
/// A record of ONE Part C safe-drift auto-resolution (issue #274, SSOT §7.2): the plan branch was
/// rewound past a provably-safe drifted suffix and its tasks journal-reset to re-run. The SAME shape is
/// surfaced live via <see cref="IRunObserver.DriftResolved"/>, threaded onto <see cref="RunReport"/> for
/// the end-of-run summary, and appended to the durable top-level <c>driftResolutions[]</c> journal
/// section — so every rewind, attended or not, leaves an audit trail of what was discarded and why. It is
/// NOT a terminal bucket: an auto-resolved run returns the NORMAL exit code (0 green / 2 needs-human).
/// </summary>
public sealed record DriftResolution
{
    /// <summary>How the rewind was authorized: <c>prompt</c> (a <c>y</c>), <c>reprocess</c> (pre-authorized), or <c>manual-reset</c> (scoped <c>guardrails reset</c>).</summary>
    public required string Trigger { get; init; }

    /// <summary>The commit the plan branch was rewound to (<c>git reset --hard</c> target); null for a journal-only reset (serial mode / no plan-branch commit to remove).</summary>
    public string? RewindTarget { get; init; }

    /// <summary>UTC time the drift was resolved.</summary>
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Per rebuilt task, its old→new short <c>definitionHash</c> — the same per-file payload the <c>DefinitionDrift</c> report carries.</summary>
    public required IReadOnlyList<DriftResolvedTask> Tasks { get; init; }
}

/// <summary>One task rebuilt by a Part C drift resolution: its id and its old→new definition hash.</summary>
public sealed record DriftResolvedTask
{
    /// <summary>The rebuilt task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>The <c>definitionHash</c> recorded at the task's last successful settle (or a sentinel when a descendant had none recorded).</summary>
    public required string OldHash { get; init; }

    /// <summary>The current on-disk <c>definitionHash</c> the task will be rebuilt against.</summary>
    public required string NewHash { get; init; }
}
