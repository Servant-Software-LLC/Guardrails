using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// The write-scope CHECK built-in (plan 08 §2/§3.4): verifies that every path touched by
/// the task's action falls within the declared <c>writeScope</c> globs, performs a scoped
/// revert of out-of-scope paths on violation, and is entirely read-only in the pass path.
/// Keyed on <c>task.json writeScope</c> PRESENCE — a null scope is the off-switch.
/// </summary>
public static class WriteScopeCheck
{
    /// <summary>
    /// Compare <c>git diff --name-status --no-renames &lt;taskBase&gt;..HEAD</c> against
    /// <paramref name="scope"/>. Returns a passing result immediately when
    /// <paramref name="scope"/> is null (the off-switch). All changed paths that are NOT
    /// claimed by the scope are collected into <see cref="WriteScopeCheckResult.OffendingPaths"/>.
    /// This method is READ-ONLY: it does not modify any file in the worktree.
    /// </summary>
    public static WriteScopeCheckResult Check(string repoPath, string taskBase, IReadOnlyList<string>? scope)
    {
        if (scope is null)
            return new WriteScopeCheckResult { Passed = true, OffendingPaths = [] };

        string diffOutput;
        try
        {
            diffOutput = RunGit(repoPath, "diff", "--name-status", "--no-renames", $"{taskBase}..HEAD");
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
    /// Restore <paramref name="offendingPaths"/> to their <paramref name="taskBase"/> state
    /// via <c>git checkout &lt;taskBase&gt; -- &lt;paths&gt;</c>, leaving all in-scope WIP
    /// untouched. No-op when <paramref name="offendingPaths"/> is empty.
    /// </summary>
    public static void ScopedRevert(string repoPath, string taskBase, IReadOnlyList<string> offendingPaths)
    {
        if (offendingPaths.Count == 0) return;

        var args = new List<string> { "checkout", taskBase, "--" };
        args.AddRange(offendingPaths);
        RunGit(repoPath, [.. args]);
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
