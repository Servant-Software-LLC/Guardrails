using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// The write-scope CHECK built-in (plan 08 §2/§3.4): verifies that every path touched by
/// the task's action falls within the declared <c>writeScope</c> globs, performs a scoped
/// revert of out-of-scope paths on violation, and never rewrites in-scope WIP.
/// Keyed on <c>task.json writeScope</c> PRESENCE — a null scope is the off-switch.
/// </summary>
public static class WriteScopeCheck
{
    /// <summary>
    /// Diff the task's post-action working tree against <paramref name="taskBase"/> and compare
    /// every changed path to <paramref name="scope"/>. Returns a passing result immediately when
    /// <paramref name="scope"/> is null (the off-switch). All changed paths that are NOT claimed by
    /// the scope are collected into <see cref="WriteScopeCheckResult.OffendingPaths"/>.
    /// </summary>
    /// <remarks>
    /// The check runs AFTER the action but BEFORE the segment commit (SSOT §3.4): at that point the
    /// action's writes are UNCOMMITTED in the segment worktree, so a <c>taskBase..HEAD</c> commit diff
    /// would be empty (HEAD == taskBase) and the check would pass vacuously — it would never catch a
    /// same-attempt out-of-scope write. To inspect the action's ACTUAL writes, this stages the
    /// worktree (<c>git add -A</c>, capturing modified, deleted, AND new/untracked files) and diffs the
    /// index against <paramref name="taskBase"/> (<c>git diff --cached --name-status --no-renames</c>).
    /// Staging the index is not a content rewrite — no tracked file's bytes change — and the Scheduler's
    /// integration step stages + commits the same tree on the pass path anyway.
    /// </remarks>
    public static WriteScopeCheckResult Check(string repoPath, string taskBase, IReadOnlyList<string>? scope)
    {
        if (scope is null)
            return new WriteScopeCheckResult { Passed = true, OffendingPaths = [] };

        string diffOutput;
        try
        {
            // Stage the action's writes so new/untracked files surface in the staged diff, then diff
            // the index against taskBase — this captures uncommitted writes (the live pre-commit path)
            // AND already-committed segment work (the committed-segment test path) with one command.
            RunGit(repoPath, "add", "-A");
            diffOutput = RunGit(repoPath, "diff", "--cached", "--name-status", "--no-renames", taskBase);
        }
        catch (InvalidOperationException ex)
        {
            // WS_2: a git failure (bad repo, bad/unknown sha) must FAIL CLOSED — never read an
            // empty stdout as "no changes". An empty diff would otherwise find zero offending
            // paths and silently pass the check, a green standing over an undiffable tree.
            return new WriteScopeCheckResult
            {
                Passed = false,
                OffendingPaths = [$"<git-error: {ex.Message}>"]
            };
        }

        var offending = new List<string>();
        foreach (string rawLine in diffOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;

            string path = line[(tabIdx + 1)..].Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path)) continue;

            if (!WriteScope.IsInScope(path, scope))
                offending.Add(path);
        }

        return new WriteScopeCheckResult
        {
            Passed = offending.Count == 0,
            OffendingPaths = offending
        };
    }

    /// <summary>
    /// Restore each path in <paramref name="offendingPaths"/> to its <paramref name="taskBase"/>
    /// state, leaving all in-scope WIP (staged or unstaged) untouched. No-op when
    /// <paramref name="offendingPaths"/> is empty.
    /// </summary>
    /// <remarks>
    /// Two out-of-scope cases must both be undone (SSOT §3.4):
    /// <list type="bullet">
    /// <item>A path that existed at <paramref name="taskBase"/> (an out-of-scope MODIFY or DELETE) is
    /// restored with <c>git checkout &lt;taskBase&gt; -- &lt;path&gt;</c>, which rewrites both the index
    /// and the working tree back to the base blob.</item>
    /// <item>A path that did NOT exist at <paramref name="taskBase"/> (a newly-ADDED out-of-scope file)
    /// has no base blob to check out — it is removed with <c>git rm -f -- &lt;path&gt;</c>, deleting it
    /// from the index and the working tree.</item>
    /// </list>
    /// Membership at base is probed per-path with <c>git cat-file -e &lt;taskBase&gt;:&lt;path&gt;</c>;
    /// only the offending paths are touched, so a same-attempt in-scope edit survives the revert.
    /// </remarks>
    public static void ScopedRevert(string repoPath, string taskBase, IReadOnlyList<string> offendingPaths)
    {
        if (offendingPaths.Count == 0) return;

        var existedAtBase = new List<string>();
        var addedSinceBase = new List<string>();
        foreach (string path in offendingPaths)
        {
            if (ExistsAtBase(repoPath, taskBase, path))
                existedAtBase.Add(path);
            else
                addedSinceBase.Add(path);
        }

        // Modified/deleted tracked files: restore the base blob into the index AND the working tree.
        if (existedAtBase.Count > 0)
        {
            var args = new List<string> { "checkout", taskBase, "--" };
            args.AddRange(existedAtBase);
            RunGit(repoPath, [.. args]);
        }

        // Newly-added files: no base blob exists, so git checkout would fail ("did not match any file");
        // remove them from the index and the working tree instead.
        if (addedSinceBase.Count > 0)
        {
            var args = new List<string> { "rm", "-f", "--" };
            args.AddRange(addedSinceBase);
            RunGit(repoPath, [.. args]);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> exists in <paramref name="taskBase"/>'s tree (so it can be
    /// restored from there). Uses <c>git cat-file -e &lt;taskBase&gt;:&lt;path&gt;</c>, which exits 0
    /// when the blob exists and non-zero otherwise; a non-zero exit therefore means "added since base".
    /// </summary>
    private static bool ExistsAtBase(string repoPath, string taskBase, string path)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"{taskBase}:{path}");
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }

    // Runs git and FAILS CLOSED on a non-zero exit (WS_2): the caller must never mistake an
    // empty stdout from a failed git invocation for "no changes". Throws so Check can convert
    // the failure into Passed=false and ScopedRevert surfaces a bad revert loudly.
    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}

/// <summary>
/// The outcome of a <see cref="WriteScopeCheck.Check"/> call.
/// </summary>
public sealed record WriteScopeCheckResult
{
    /// <summary>True when every changed path is within the declared write-scope.</summary>
    public bool Passed { get; init; }

    /// <summary>Changed paths that fall outside the declared write-scope. Empty when <see cref="Passed"/>.</summary>
    public IReadOnlyList<string> OffendingPaths { get; init; } = [];
}
