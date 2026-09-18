using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>One path the harness verified it MAY supply, and the checkout commit its bytes are read at.</summary>
public sealed record MissingResourceCandidate
{
    /// <summary>The workspace-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>The operator checkout's HEAD sha the blob was found at (design 41 §2.2).</summary>
    public required string SourceCommit { get; init; }
}

/// <summary>One path's verdict: a candidate (<see cref="Reason"/> null), or refused with its reason token.</summary>
public sealed record MissingResourcePathVerdict
{
    /// <summary>The workspace-relative path this verdict is about.</summary>
    public required string Path { get; init; }

    /// <summary>The §2.2 reason token, or null when the path is a candidate.</summary>
    public string? Reason { get; init; }
}

/// <summary>The whole consult's facts. <see cref="Available"/> false is the tri-state stop.</summary>
public sealed record MissingResourceFactsResult
{
    /// <summary>False when a git call ERRORED — never when a path was merely absent (design 41 §2.2).</summary>
    public required bool Available { get; init; }

    /// <summary>The stop token when <see cref="Available"/> is false; otherwise null.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The paths that passed every check, each carrying its source sha. Empty means no consult.</summary>
    public required IReadOnlyList<MissingResourceCandidate> Candidates { get; init; }

    /// <summary>One entry per path examined, candidate or refused — what the `observed` decision renders.</summary>
    public required IReadOnlyList<MissingResourcePathVerdict> Verdicts { get; init; }
}

/// <summary>
/// The harness-computed facts design 41 §2.2 requires BEFORE any model is consulted. Every git call is
/// tri-state: present, absent, or ERROR — and an error is never read as absent.
/// </summary>
public static class MissingResourceFacts
{
    /// <param name="plan">The plan. <c>plan.Workspace</c> IS the operator's checkout; <c>plan.PlanDirectory</c> bounds check 3.</param>
    /// <param name="haltedTask">The task that halted — excluded from check 4's "every OTHER task" sweep.</param>
    /// <param name="paths">The paths <see cref="MissingResourceSignal.PathsIn"/> found in the question.</param>
    /// <param name="integrationWorktreePath">The run's own base — the integration worktree, never the checkout.</param>
    /// <param name="originalBranch">`integ.OriginalBranch` — the branch the run started from.</param>
    /// <param name="originalHeadSha">`integ.OriginalHeadSha` — the commit the run started from.</param>
    public static MissingResourceFactsResult Compute(
        PlanDefinition plan,
        TaskNode haltedTask,
        IReadOnlyList<string> paths,
        string integrationWorktreePath,
        string originalBranch,
        string originalHeadSha) => throw new NotImplementedException();
}
