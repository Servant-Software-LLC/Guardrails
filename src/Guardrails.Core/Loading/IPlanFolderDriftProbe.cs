namespace Guardrails.Core.Loading;

/// <summary>
/// Answers how many commits touching the PLAN FOLDER exist on the operator's branch but not on the
/// run's plan branch (issue #576).
///
/// <para><b>The defect this exists to make visible.</b> The plan branch <c>guardrails/&lt;plan&gt;</c> is cut
/// ONCE, at the first run's start, and is never rebased across resumes. A fix the operator makes to the
/// plan folder BETWEEN resumes takes effect immediately — the harness reads the folder from the main
/// checkout — but never lands on the plan branch. So merging the plan branch alone, which is precisely
/// what the undelivered-work banner tells the operator to do, delivers the CODE while leaving the
/// committed PLAN FOLDER at run-start state. The repository then records a plan that could not have
/// produced the code beside it.</para>
///
/// <para>Measured on plan 32: three resumes each fixed a real plan-folder defect, the run used all three,
/// and after the merge master carried a 16-task plan (task 17 absent) whose task-01 guardrail contained
/// the filter that had HALTED the run. Caught by hand; nothing prompted it.</para>
///
/// <para><b>Silence is not proof</b>, on <see cref="IGitTrackedFileProbe"/>'s contract. When git is
/// unavailable, the plan branch does not exist, or the answer cannot be obtained, the probe reports
/// <c>null</c> — NOT KNOWN — and a not-known answer must never be rendered as "0 commits" or as
/// "your folder is in sync". The banner this feeds is read at the end of a run the operator is about to
/// act on, so a confident wrong reassurance is worse than saying nothing at all.</para>
///
/// <para>Injected rather than called directly, mirroring <see cref="IGitTrackedFileProbe"/>: the banner
/// stays a pure function over its inputs and is unit-testable without a git checkout on disk.</para>
/// </summary>
public interface IPlanFolderDriftProbe
{
    /// <summary>
    /// How many commits touching <paramref name="planFolderPath"/> are reachable from the operator's
    /// current HEAD but NOT from <paramref name="planBranch"/> — i.e. plan-folder edits the plan branch
    /// would not carry if it were merged on its own.
    ///
    /// <para>Returns <c>null</c> when NOT KNOWN, which includes every uninteresting case: no git, no such
    /// branch (a run that never used worktree mode has no plan branch), a detached or unreadable HEAD, or
    /// a failed invocation. <c>null</c> must never be read as zero.</para>
    /// </summary>
    /// <param name="planBranch">The plan branch, e.g. <c>guardrails/32-executed-definition-hash</c>.</param>
    /// <param name="planFolderPath">The plan folder, absolute or repo-relative; used as a git pathspec.</param>
    int? CommitsNotOnPlanBranch(string planBranch, string planFolderPath);
}

/// <summary>An <see cref="IPlanFolderDriftProbe"/> that knows nothing — the no-git default.</summary>
public sealed class NullPlanFolderDriftProbe : IPlanFolderDriftProbe
{
    /// <summary>The shared instance; the probe is stateless.</summary>
    public static readonly NullPlanFolderDriftProbe Instance = new();

    /// <inheritdoc />
    public int? CommitsNotOnPlanBranch(string planBranch, string planFolderPath) => null;
}
