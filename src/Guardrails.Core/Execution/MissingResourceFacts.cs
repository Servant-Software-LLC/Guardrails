using System.Diagnostics;
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
        string originalHeadSha)
    {
        // -- Run-level lineage facts, checked once. Either failing refuses every path with that reason. --

        GitResult branch = RunGit(plan.Workspace, "rev-parse", "--abbrev-ref", "HEAD");
        if (branch.ExitCode != 0)
        {
            return Unavailable();
        }

        if (!string.Equals(branch.Stdout.Trim(), originalBranch, StringComparison.Ordinal))
        {
            return RefuseAll(paths, "checkout-not-on-run-branch");
        }

        GitResult checkoutHead = RunGit(plan.Workspace, "rev-parse", "HEAD");
        if (checkoutHead.ExitCode != 0)
        {
            return Unavailable();
        }

        string checkoutHeadSha = checkoutHead.Stdout.Trim();

        // In every real run the checkout and the integration worktree are worktrees of the SAME
        // repository, so originalHeadSha (the run's own starting commit) is always resolvable here.
        // It is only ever unresolvable when the caller is a test double built from two independent
        // repositories — there is no lineage evidence to report in that case, so the fact is not
        // refused on that basis alone; it is treated as "nothing to compare against".
        GitResult originalExists = RunGit(plan.Workspace, "cat-file", "-e", originalHeadSha);
        if (originalExists.ExitCode == 0)
        {
            GitResult ancestor =
                RunGit(plan.Workspace, "merge-base", "--is-ancestor", originalHeadSha, checkoutHeadSha);
            if (ancestor.ExitCode == 1)
            {
                return RefuseAll(paths, "checkout-diverged");
            }

            if (ancestor.ExitCode != 0)
            {
                return Unavailable();
            }
        }
        else if (originalExists.ExitCode != 1)
        {
            return Unavailable();
        }

        // -- The boundary for check 7 ("did THIS run delete it"): the merge-base of the integration
        // worktree's HEAD and the checkout's HEAD, i.e. the commit the plan branch was cut from. When
        // checkoutHeadSha is not resolvable in the integration worktree (again, only the disjoint-repo
        // test double — production shares one object database), fall back to the whole reachable
        // history rather than erroring, since that is a strictly WIDER search for a deletion, never a
        // narrower one that could hide one. --
        GitResult checkoutHeadInInteg = RunGit(integrationWorktreePath, "cat-file", "-e", checkoutHeadSha);
        string deletionRange;
        if (checkoutHeadInInteg.ExitCode == 0)
        {
            GitResult mergeBase = RunGit(integrationWorktreePath, "merge-base", checkoutHeadSha, "HEAD");
            if (mergeBase.ExitCode != 0)
            {
                return Unavailable();
            }

            deletionRange = mergeBase.Stdout.Trim() + "..HEAD";
        }
        else if (checkoutHeadInInteg.ExitCode == 1)
        {
            deletionRange = "HEAD";
        }
        else
        {
            return Unavailable();
        }

        // -- The full set of paths tracked at the integration worktree's HEAD, used for checks 5 and 6. --
        GitResult integrationTree = RunGit(integrationWorktreePath, "ls-tree", "-r", "--name-only", "HEAD");
        if (integrationTree.ExitCode != 0)
        {
            return Unavailable();
        }

        var integrationPaths = new HashSet<string>(SplitLines(integrationTree.Stdout), StringComparer.Ordinal);
        var integrationPathsIgnoreCase =
            new HashSet<string>(SplitLines(integrationTree.Stdout), StringComparer.OrdinalIgnoreCase);

        string planDirectoryRelative = ToForwardSlash(Path.GetRelativePath(plan.Workspace, plan.PlanDirectory));

        var verdicts = new List<MissingResourcePathVerdict>();
        var candidates = new List<MissingResourceCandidate>();

        foreach (string path in paths)
        {
            string? reason;

            // 1 — stays inside the workspace.
            if (WorkspaceContainment.Escapes(plan.Workspace, path))
            {
                reason = "escapes-workspace";
            }
            // 2 — not a protected path.
            else if (IsProtectedPath(path))
            {
                reason = "protected-path";
            }
            // 3 — not under the plan folder.
            else if (IsUnderPlanFolder(path, planDirectoryRelative))
            {
                reason = "under-plan-folder";
            }
            // 4 — no OTHER task may produce it. Fails closed on an unscoped sibling.
            else if (AnySiblingDeclaresNoWriteScope(plan, haltedTask))
            {
                reason = "plan-scope-incomplete";
            }
            else if (AnySiblingProduces(plan, haltedTask, path))
            {
                reason = "produced-by-another-task";
            }
            // 5 — absent from the run's base.
            else if (integrationPaths.Contains(path))
            {
                reason = "present-on-run-base";
            }
            // 6 — no case-only twin on the run's base.
            else if (integrationPathsIgnoreCase.Contains(path))
            {
                reason = "case-collision";
            }
            else
            {
                reason = ClassifyAgainstCheckout(
                    plan.Workspace, integrationWorktreePath, checkoutHeadSha, deletionRange, path, out bool errored);
                if (errored)
                {
                    return Unavailable();
                }
            }

            verdicts.Add(new MissingResourcePathVerdict { Path = path, Reason = reason });
            if (reason is null)
            {
                candidates.Add(new MissingResourceCandidate { Path = path, SourceCommit = checkoutHeadSha });
            }
        }

        return new MissingResourceFactsResult
        {
            Available = true,
            UnavailableReason = null,
            Candidates = candidates,
            Verdicts = verdicts
        };
    }

    /// <summary>Checks 7–10: the run's own deletion history, then the operator's checkout.</summary>
    private static string? ClassifyAgainstCheckout(
        string workspace,
        string integrationWorktreePath,
        string checkoutHeadSha,
        string deletionRange,
        string path,
        out bool errored)
    {
        errored = false;

        // 7 — the run did not delete it.
        GitResult deletions = RunGit(
            integrationWorktreePath, "log", "--diff-filter=D", "--format=%H", deletionRange, "--", path);
        if (deletions.ExitCode != 0)
        {
            errored = true;
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deletions.Stdout))
        {
            return "deleted-on-run-base";
        }

        // 8 — committed in the operator's checkout, and 9 — that object is a file. A single `ls-tree`
        // call answers both: empty stdout is a clean "not found" (exit 0), and the printed entry's
        // type field (blob/tree/commit) is check 9 without a second process launch.
        GitResult checkoutEntry = RunGit(workspace, "ls-tree", checkoutHeadSha, "--", path);
        if (checkoutEntry.ExitCode != 0)
        {
            errored = true;
            return null;
        }

        if (string.IsNullOrWhiteSpace(checkoutEntry.Stdout))
        {
            return "not-committed-in-checkout";
        }

        if (!string.Equals(LsTreeType(checkoutEntry.Stdout), "blob", StringComparison.Ordinal))
        {
            return "not-a-blob";
        }

        // 10 — unmodified in the checkout's working tree.
        GitResult diff = RunGit(workspace, "diff", "--quiet", "HEAD", "--", path);
        if (diff.ExitCode == 1)
        {
            return "modified-in-checkout";
        }

        if (diff.ExitCode != 0)
        {
            errored = true;
            return null;
        }

        return null;
    }

    private static MissingResourceFactsResult Unavailable() => new()
    {
        Available = false,
        UnavailableReason = "facts-unavailable",
        Candidates = [],
        Verdicts = []
    };

    private static MissingResourceFactsResult RefuseAll(IReadOnlyList<string> paths, string reason) => new()
    {
        Available = true,
        UnavailableReason = null,
        Candidates = [],
        Verdicts = paths.Select(p => new MissingResourcePathVerdict { Path = p, Reason = reason }).ToList()
    };

    // .claude/, .guardrails-staging/, .guardrails-agent-io/, and any top-level segment starting ".git"
    // (.git itself, .gitignore, .gitattributes, ...).
    private static bool IsProtectedPath(string path)
    {
        string first = path.Split('/')[0];
        return first is ".claude" or ".guardrails-staging" or ".guardrails-agent-io"
            || first.StartsWith(".git", StringComparison.Ordinal);
    }

    private static bool IsUnderPlanFolder(string path, string planDirectoryRelative)
    {
        if (string.IsNullOrEmpty(planDirectoryRelative) || planDirectoryRelative == ".")
        {
            return false;
        }

        return string.Equals(path, planDirectoryRelative, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(planDirectoryRelative + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AnySiblingDeclaresNoWriteScope(PlanDefinition plan, TaskNode haltedTask) =>
        plan.Tasks.Any(t => t.Id != haltedTask.Id && t.WriteScope is null);

    private static bool AnySiblingProduces(PlanDefinition plan, TaskNode haltedTask, string path) =>
        plan.Tasks.Any(t => t.Id != haltedTask.Id && t.WriteScope is not null && WriteScope.IsInScope(path, t.WriteScope));

    private static string ToForwardSlash(string path) => path.Replace('\\', '/');

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // `git ls-tree <tree-ish> -- <path>` prints "<mode> <type> <sha>\t<path>"; the type is the second field.
    private static string LsTreeType(string lsTreeLine) => lsTreeLine.TrimStart().Split(' ')[1];

    private readonly record struct GitResult(string Stdout, int ExitCode);

    /// <summary>
    /// Run <c>git</c> with <paramref name="workingDir"/> as its cwd and return stdout and the exit code.
    /// Never throws. Callers classify the exit code themselves: for a plumbing command whose exit codes
    /// are documented as tri-state (0/1/other), 1 is a clean, expected "false" answer — never treated as
    /// a git failure.
    /// </summary>
    private static GitResult RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
            StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new GitResult(stdout, proc.ExitCode);
    }
}
