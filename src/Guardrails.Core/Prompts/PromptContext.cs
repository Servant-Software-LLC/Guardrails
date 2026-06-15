namespace Guardrails.Core.Prompts;

/// <summary>
/// A pointer to a completed dependency task's artifacts, injected into a task's prompt so the
/// agent can see what its ancestors produced — files, classes, patterns — instead of
/// rediscovering the project from scratch (issue #26 Gap 4). Scoped to the transitive
/// <c>dependsOn</c> closure and supplied on EVERY attempt (not just retries). Paths are
/// absolute; the harness omits artifacts that do not exist on disk at compose time.
/// </summary>
public sealed record DependencyContextRef
{
    /// <summary>The dependency task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>The dependency task's one-line description.</summary>
    public required string Description { get; init; }

    /// <summary>Absolute path to the dependency's succeeded-attempt log dir.</summary>
    public required string LogDir { get; init; }

    /// <summary>
    /// Absolute path to the dependency's <c>transcript.md</c> (the CLI-equivalent view, #27),
    /// or null when none exists (e.g. a script-action ancestor that produced no Claude stream).
    /// </summary>
    public string? TranscriptPath { get; init; }

    /// <summary>Absolute path to the state fragment the dependency contributed, or null if it wrote none.</summary>
    public string? FragmentPath { get; init; }
}

/// <summary>
/// A pointer to a PRIOR attempt of the SAME task, injected into a retry prompt so the agent
/// sees the full arc of what was already tried — not just the immediately preceding failure
/// (issue #26 Gaps 2 &amp; 3). The harness points the agent at the clean <c>transcript.md</c>
/// (what was done) and <c>feedback.md</c> (why it failed) rather than the raw stream. Paths
/// are absolute.
/// </summary>
public sealed record PriorAttemptRef
{
    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>A short, human-readable outcome label (e.g. <c>guardrail-failed</c>).</summary>
    public required string Outcome { get; init; }

    /// <summary>Absolute path to that attempt's log dir.</summary>
    public required string LogDir { get; init; }

    /// <summary>Absolute path to that attempt's <c>transcript.md</c> (what the agent did), if it exists.</summary>
    public string? TranscriptPath { get; init; }

    /// <summary>Absolute path to that attempt's <c>feedback.md</c> (why it failed), if it exists.</summary>
    public string? FeedbackPath { get; init; }
}
