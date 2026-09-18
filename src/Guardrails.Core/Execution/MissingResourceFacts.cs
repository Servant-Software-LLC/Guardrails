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
        // Repo-access probes for BOTH repos, once, up front: a failure here (e.g. exit 128 for a
        // dubious-ownership repo, a missing worktree) is the tri-state ERROR that stops the whole
        // consult. Once these succeed, a later non-zero exit against the SAME repo is a legitimate
        // "absent", not a repeat of this same error.
        if (!TryCurrentBranch(plan.Workspace, out string checkoutBranch) ||
            !TryHeadSha(plan.Workspace, out string checkoutHeadSha) ||
            !TryHeadSha(integrationWorktreePath, out _))
        {
            return Unavailable();
        }

        string? runLevelReason = null;
        if (!string.Equals(checkoutBranch, originalBranch, StringComparison.Ordinal))
        {
            runLevelReason = "checkout-not-on-run-branch";
        }
        else
        {
            bool? ancestor = IsAncestor(plan.Workspace, originalHeadSha, checkoutHeadSha);
            if (ancestor is null)
            {
                return Unavailable();
            }

            if (ancestor == false)
            {
                runLevelReason = "checkout-diverged";
            }
        }

        if (runLevelReason is not null)
        {
            List<MissingResourcePathVerdict> refusedAll = paths
                .Select(path => new MissingResourcePathVerdict { Path = path, Reason = runLevelReason })
                .ToList();
            return new MissingResourceFactsResult
            {
                Available = true,
                UnavailableReason = null,
                Candidates = [],
                Verdicts = refusedAll
            };
        }

        var verdicts = new List<MissingResourcePathVerdict>();
        var candidates = new List<MissingResourceCandidate>();

        foreach (string path in paths)
        {
            (string? reason, bool gitError) = EvaluatePath(
                plan, haltedTask, path, integrationWorktreePath, checkoutHeadSha, originalHeadSha);

            if (gitError)
            {
                return Unavailable();
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

    private static MissingResourceFactsResult Unavailable() => new()
    {
        Available = false,
        UnavailableReason = "facts-unavailable",
        Candidates = [],
        Verdicts = []
    };

    /// <summary>The ten per-path checks, in §2.2's order. The first failure names the reason.</summary>
    private static (string? Reason, bool GitError) EvaluatePath(
        PlanDefinition plan,
        TaskNode haltedTask,
        string path,
        string integrationWorktreePath,
        string checkoutHeadSha,
        string originalHeadSha)
    {
        if (WorkspaceContainment.Escapes(plan.Workspace, path))
        {
            return ("escapes-workspace", false);
        }

        if (IsProtectedPath(path))
        {
            return ("protected-path", false);
        }

        if (IsUnderPlanFolder(plan.Workspace, plan.PlanDirectory, path))
        {
            return ("under-plan-folder", false);
        }

        string? ownershipReason = OwnershipReason(plan, haltedTask, path);
        if (ownershipReason is not null)
        {
            return (ownershipReason, false);
        }

        bool? presentOnBase = ObjectExists(integrationWorktreePath, $"HEAD:{path}");
        if (presentOnBase is null)
        {
            return (null, true);
        }

        if (presentOnBase == true)
        {
            return ("present-on-run-base", false);
        }

        bool? caseCollision = HasCaseOnlyTwin(integrationWorktreePath, path);
        if (caseCollision is null)
        {
            return (null, true);
        }

        if (caseCollision == true)
        {
            return ("case-collision", false);
        }

        bool? deleted = DeletedSinceRunStart(integrationWorktreePath, originalHeadSha, path);
        if (deleted is null)
        {
            return (null, true);
        }

        if (deleted == true)
        {
            return ("deleted-on-run-base", false);
        }

        bool? committed = ObjectExists(plan.Workspace, $"{checkoutHeadSha}:{path}");
        if (committed is null)
        {
            return (null, true);
        }

        if (committed == false)
        {
            return ("not-committed-in-checkout", false);
        }

        string? objectType = ObjectType(plan.Workspace, $"{checkoutHeadSha}:{path}");
        if (objectType is null)
        {
            return (null, true);
        }

        if (objectType != "blob")
        {
            return ("not-a-blob", false);
        }

        bool? clean = IsCleanAgainstHead(plan.Workspace, path);
        if (clean is null)
        {
            return (null, true);
        }

        if (clean == false)
        {
            return ("modified-in-checkout", false);
        }

        return (null, false);
    }

    /// <summary>Check 2: not under <c>.claude/</c>, <c>.guardrails-staging/</c>, <c>.guardrails-agent-io/</c>, nor a top-level <c>.git*</c> segment.</summary>
    private static bool IsProtectedPath(string path)
    {
        string firstSegment = path.Split('/')[0];
        return firstSegment is ".claude" or ".guardrails-staging" or ".guardrails-agent-io"
            || firstSegment.StartsWith(".git", StringComparison.Ordinal);
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Check 3: path containment against <c>plan.PlanDirectory</c>.</summary>
    private static bool IsUnderPlanFolder(string workspace, string planDirectory, string path)
    {
        string full = Path.GetFullPath(Path.Combine(workspace, path));
        string planRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(planDirectory));

        if (string.Equals(full, planRoot, PathComparison))
        {
            return true;
        }

        return full.StartsWith(planRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// Check 4 (`d41-candidate-scope`, decided in review): every task OTHER than <paramref name="haltedTask"/>
    /// must declare a <c>writeScope</c> — an unscoped other task makes the ownership question unanswerable, so
    /// the path fails closed. Only once every other task is scoped does coverage decide the path.
    /// </summary>
    private static string? OwnershipReason(PlanDefinition plan, TaskNode haltedTask, string path)
    {
        List<TaskNode> others = plan.Tasks.Where(t => t.Id != haltedTask.Id).ToList();

        if (others.Any(t => t.WriteScope is null))
        {
            return "plan-scope-incomplete";
        }

        return others.Any(t => WriteScope.IsInScope(path, t.WriteScope!))
            ? "produced-by-another-task"
            : null;
    }

    /// <summary>True/false via <c>git cat-file -e</c>. Only called once the containing repo is already
    /// known accessible, so a non-zero exit here is a genuine "absent", not a repeat of the repo-access
    /// error — null means the git process itself could not be run at all.</summary>
    private static bool? ObjectExists(string workingDir, string objectSpec) =>
        TryRunGit(workingDir, out int exitCode, out _, out _, "cat-file", "-e", objectSpec)
            ? exitCode == 0
            : null;

    /// <summary>The object's type via <c>git cat-file -t</c>. Called only after <see cref="ObjectExists"/>
    /// already confirmed the object is there, so a non-zero exit here is an unexpected error, not "absent".</summary>
    private static string? ObjectType(string workingDir, string objectSpec) =>
        TryRunGit(workingDir, out int exitCode, out string stdout, out _, "cat-file", "-t", objectSpec) && exitCode == 0
            ? stdout.Trim()
            : null;

    /// <summary>Check 6: any case-insensitive-but-not-exact match against a case-sensitive <c>ls-tree</c> listing.</summary>
    private static bool? HasCaseOnlyTwin(string workingDir, string path)
    {
        if (!TryRunGit(workingDir, out int exitCode, out string stdout, out _, "ls-tree", "-r", "--name-only", "HEAD")
            || exitCode != 0)
        {
            return null;
        }

        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(line, path, StringComparison.Ordinal)
                && string.Equals(line, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check 7: was <paramref name="path"/> deleted since the run started. Bounded to
    /// <c>&lt;originalHeadSha&gt;..HEAD</c> in the integration worktree, which in production shares the
    /// checkout's object store (the worktree was created from it), so the bound always resolves there. If
    /// it does not resolve — the only way that happens is the two repos not sharing history at all — that
    /// is not itself an access error (the repo is already known accessible): fall back to the worktree's
    /// whole reachable history rather than reading an unresolvable bound as a git ERROR.
    /// </summary>
    private static bool? DeletedSinceRunStart(string integrationWorktreePath, string originalHeadSha, string path)
    {
        bool? bounded = LogFindsDeletion(integrationWorktreePath, $"{originalHeadSha}..HEAD", path);
        return bounded ?? LogFindsDeletion(integrationWorktreePath, "HEAD", path);
    }

    private static bool? LogFindsDeletion(string workingDir, string range, string path) =>
        TryRunGit(workingDir, out int exitCode, out string stdout, out _,
            "log", "--diff-filter=D", "--format=%H", range, "--", path) && exitCode == 0
            ? !string.IsNullOrWhiteSpace(stdout)
            : null;

    /// <summary>Check 10 via <c>git diff --quiet HEAD --</c>: exit 0 clean, exit 1 modified, anything else an error.</summary>
    private static bool? IsCleanAgainstHead(string workingDir, string path)
    {
        if (!TryRunGit(workingDir, out int exitCode, out _, out _, "diff", "--quiet", "HEAD", "--", path))
        {
            return null;
        }

        return exitCode switch
        {
            0 => true,
            1 => false,
            _ => null
        };
    }

    /// <summary>The run-level ancestry fact via <c>git merge-base --is-ancestor</c>: exit 0 true, exit 1 false, anything else an error.</summary>
    private static bool? IsAncestor(string workingDir, string ancestorSha, string descendantSha)
    {
        if (!TryRunGit(workingDir, out int exitCode, out _, out _, "merge-base", "--is-ancestor", ancestorSha, descendantSha))
        {
            return null;
        }

        return exitCode switch
        {
            0 => true,
            1 => false,
            _ => null
        };
    }

    private static bool TryCurrentBranch(string workingDir, out string branch)
    {
        if (TryRunGit(workingDir, out int exitCode, out string stdout, out _, "rev-parse", "--abbrev-ref", "HEAD")
            && exitCode == 0)
        {
            branch = stdout.Trim();
            return true;
        }

        branch = "";
        return false;
    }

    private static bool TryHeadSha(string workingDir, out string sha)
    {
        if (TryRunGit(workingDir, out int exitCode, out string stdout, out _, "rev-parse", "HEAD")
            && exitCode == 0)
        {
            sha = stdout.Trim();
            return true;
        }

        sha = "";
        return false;
    }

    /// <summary>
    /// Run <c>git</c> in <paramref name="workingDir"/> and capture its exit code, stdout and stderr.
    /// Returns false only when the process itself could not be run (missing directory, git absent, a
    /// launch fault) — never for a non-zero exit, which callers interpret per-command since git's exit
    /// codes are not uniformly "0 = yes, else = no" (e.g. <c>merge-base --is-ancestor</c> reserves 1 for
    /// a definite "no" and anything else for an error).
    /// </summary>
    private static bool TryRunGit(
        string workingDir, out int exitCode, out string stdout, out string stderr, params string[] args)
    {
        exitCode = -1;
        stdout = "";
        stderr = "";

        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            return false;
        }

        try
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

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
