using System;
using System.Diagnostics;
using System.Linq;

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
            // Staging EXCLUDES the reconstructable dep/build set (SSOT §5.3(D), issue #280): a
            // guardrail's `npm ci` node_modules is uniformly invisible to harness git, so it can never
            // surface here as a spurious out-of-scope violation (e.g. a leftover in a reused
            // linear-chain worktree), and phase-2 scope-clean (§3.4) never deletes it from disk (#255).
            SegmentStaging.StageAll(repoPath);
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
                OffendingPaths = [new WriteScopeOffense { Path = $"<git-error: {ex.Message}>", Status = '?' }]
            };
        }

        var offending = new List<WriteScopeOffense>();
        foreach (string rawLine in diffOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int tabIdx = line.IndexOf('\t');
            if (tabIdx < 0) continue;

            // git diff --name-status prints a single status letter (A/M/D — renames are disabled via
            // --no-renames, so no multi-char "R100"-style scores appear here) before the tab.
            string statusField = line[..tabIdx].Trim();
            char status = statusField.Length > 0 ? statusField[0] : '?';

            string path = line[(tabIdx + 1)..].Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path)) continue;

            if (!WriteScope.IsInScope(path, scope))
            {
                offending.Add(new WriteScopeOffense
                {
                    Path = path,
                    Status = status,
                    Preview = CapturePreviewIfNewFile(repoPath, path, status)
                });
            }
        }

        return new WriteScopeCheckResult
        {
            Passed = offending.Count == 0,
            OffendingPaths = offending
        };
    }

    /// <summary>
    /// Phase-2 scope-clean (SSOT §3.4, issue #280): the "verifiers don't produce committed artifacts"
    /// guarantee. Called AFTER a writeScope task's guardrails PASS and before the segment settle, it
    /// re-computes the out-of-scope changed paths and REVERTS them — reusing the exact same
    /// <see cref="Check"/> + <see cref="ScopedRevert"/> as the phase-1 action check. The CRUCIAL
    /// semantic difference from phase 1: this STRIPS SILENTLY and NEVER fails the attempt. A passing
    /// guardrail legitimately runs an <c>npm ci</c> / a build as a side effect; those out-of-scope
    /// artifacts are expected and are cleaned so the commit carries exactly the in-scope diff — never
    /// punished. Returns the offenses it stripped (empty when nothing was out of scope) so the caller
    /// can echo them to a log / observer note (the #253 "don't silently vanish files" posture).
    /// <para>
    /// The reconstructable dep/build dirs (<see cref="SegmentStaging.ReconstructableExclusions"/>) are
    /// invisible to <see cref="Check"/>'s staging, so they are NEVER seen here and thus NEVER deleted
    /// from the worktree — they stay on disk (warm-cache #255) and are kept out of the commit by the
    /// <see cref="SegmentStaging"/> exclusion at the <see cref="GitWorktreeProvider.Integrate"/> staging
    /// site instead. Returns <c>[]</c> for a null scope (the off-switch) and for the WS_2 git-error
    /// sentinel (status <c>?</c>) — phase 2 is best-effort and must not fail the attempt, so a git error
    /// during the re-check degrades to "nothing stripped" rather than attempting to <c>git rm</c> a
    /// synthetic marker path.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WriteScopeOffense> StripOutOfScope(
        string repoPath, string taskBase, IReadOnlyList<string>? scope)
    {
        WriteScopeCheckResult result = Check(repoPath, taskBase, scope);

        // Only revert REAL offending paths — the WS_2 git-error sentinel (status '?') is a synthetic
        // marker, not a path. Phase 2 does not fail the attempt, so a git error simply strips nothing.
        var realOffenses = result.OffendingPaths.Where(o => o.Status != '?').ToList();
        if (realOffenses.Count == 0)
        {
            return [];
        }

        ScopedRevert(repoPath, taskBase, realOffenses);
        return realOffenses;
    }

    /// <summary>
    /// Issue #253's forensic-trace gap: <see cref="ScopedRevert"/> deletes a newly-added out-of-scope
    /// file outright, leaving nothing for a human to inspect afterward — no way to tell whether it was
    /// a genuine (if misguided) agent write or unattributable environmental cruft swept up by
    /// <c>git add -A</c> (e.g. a leaked test fixture — the suspected mechanism in #253). Captures a
    /// best-effort snapshot — size plus a short text preview — for an 'A' (new/untracked) offense
    /// ONLY, while the file still exists in the worktree (i.e. called from <see cref="Check"/>,
    /// strictly before the caller invokes <see cref="ScopedRevert"/>). 'M'/'D' offenses return null:
    /// their taskBase blob is always separately recoverable via <c>git show taskBase:&lt;path&gt;</c>,
    /// so no snapshot is needed. Returns null (never throws) when the file is missing, unreadable, or
    /// binary-looking — this is a diagnostic nicety, not something that may fail the check itself.
    /// </summary>
    private static WriteScopeOffensePreview? CapturePreviewIfNewFile(string repoPath, string relativePath, char status)
    {
        if (status != 'A') return null;

        try
        {
            string fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;

            const int maxPreviewBytes = 500;
            byte[] buffer = new byte[(int)Math.Min(maxPreviewBytes, info.Length)];
            using (FileStream stream = info.OpenRead())
            {
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (read < buffer.Length) Array.Resize(ref buffer, read);
            }

            // A NUL byte anywhere in the sampled prefix is the same cheap binary sniff git itself uses.
            string textPreview = Array.IndexOf(buffer, (byte)0) < 0
                ? System.Text.Encoding.UTF8.GetString(buffer)
                : "<binary content>";

            return new WriteScopeOffensePreview { SizeBytes = info.Length, TextPreview = textPreview };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the post-action working tree has ANY change versus <paramref name="taskBase"/> —
    /// a modified, deleted, or new/untracked file (issue #174). Uses the same stage-then-diff
    /// primitive as <see cref="Check"/> (so new/untracked files surface), independent of any
    /// declared <c>writeScope</c>. Drives the no-op short-circuit: a task whose action made NO file
    /// changes this attempt (and wrote no state fragment) cannot fix a guardrail failure by
    /// retrying. FAILS OPEN — a git error returns <c>true</c> (assume the action DID change
    /// something) so an undiffable tree NEVER triggers the short-circuit; the inverse of the
    /// write-scope check's fail-closed posture, because here "true" is the safe, retry-preserving
    /// answer.
    /// </summary>
    public static bool HasFileChanges(string repoPath, string taskBase)
    {
        try
        {
            // Same reconstructable-set exclusion as Check (SSOT §5.3(D), issue #280): a guardrail's
            // node_modules must not be read as an "observable change" that keeps the no-op
            // short-circuit (#174) from firing.
            SegmentStaging.StageAll(repoPath);
            string diff = RunGit(repoPath, "diff", "--cached", "--name-status", "--no-renames", taskBase);
            return diff.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(l => l.Trim().Length > 0);
        }
        catch (InvalidOperationException)
        {
            // Fail OPEN: an undiffable tree must not be read as "no changes" — that would let the
            // no-op short-circuit fire on a task that may genuinely have written files. Preserve the
            // full retry budget instead.
            return true;
        }
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
    public static void ScopedRevert(string repoPath, string taskBase, IReadOnlyList<WriteScopeOffense> offendingPaths)
    {
        if (offendingPaths.Count == 0) return;

        var existedAtBase = new List<string>();
        var addedSinceBase = new List<string>();
        foreach (WriteScopeOffense offense in offendingPaths)
        {
            if (ExistsAtBase(repoPath, taskBase, offense.Path))
                existedAtBase.Add(offense.Path);
            else
                addedSinceBase.Add(offense.Path);
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
    public IReadOnlyList<WriteScopeOffense> OffendingPaths { get; init; } = [];
}

/// <summary>
/// One offending path from a write-scope violation (issue #253), paired with its raw
/// <c>git diff --name-status</c> change-status letter — <c>A</c> (new/untracked addition),
/// <c>M</c> (modification of a file that existed at <c>taskBase</c>), <c>D</c> (deletion of a file
/// that existed at <c>taskBase</c>), or <c>?</c> for the WS_2 git-error sentinel path, which never
/// had a real diff line to read a letter from. Surfacing the letter (and, for a new file, a
/// forensic preview — see <see cref="Preview"/>) lets a human debugging a <c>needs-human</c>
/// write-scope violation immediately tell "a brand-new untracked file with no history at this
/// task's base commit" (suspicious/unattributable — <c>git add -A</c> sweeps up ANY untracked file
/// present in the worktree, not just ones the agent's own tool calls wrote) apart from "a
/// modification of a file that genuinely existed before this attempt" (far more likely a real
/// agent mistake).
/// </summary>
public sealed record WriteScopeOffense
{
    /// <summary>The offending path, forward-slash separated, workspace-relative.</summary>
    public required string Path { get; init; }

    /// <summary>The raw git status letter for this path (see the type doc comment for the full set).</summary>
    public required char Status { get; init; }

    /// <summary>True for a brand-new/untracked addition (<c>Status == 'A'</c>) — the class of write
    /// this issue's diagnostic improvement targets.</summary>
    public bool IsNewFile => Status == 'A';

    /// <summary>
    /// For an <see cref="IsNewFile"/> offense only: a best-effort forensic snapshot captured while the
    /// file still existed in the worktree, before <see cref="WriteScopeCheck.ScopedRevert"/> deletes
    /// it — otherwise the file is simply gone with no trace for a later post-mortem (issue #253).
    /// Null for <c>M</c>/<c>D</c> offenses (the taskBase blob is always separately recoverable via
    /// <c>git show taskBase:&lt;path&gt;</c>) and for an <see cref="IsNewFile"/> offense whose content
    /// could not be captured (missing/unreadable file, race, etc.).
    /// </summary>
    public WriteScopeOffensePreview? Preview { get; init; }
}

/// <summary>A forensic snapshot of a newly-added out-of-scope file, captured before it is reverted.</summary>
public sealed record WriteScopeOffensePreview
{
    /// <summary>The file's size in bytes at capture time.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Up to the first 500 bytes of the file, decoded as UTF-8, or the literal string
    /// <c>"&lt;binary content&gt;"</c> when a NUL byte in that prefix suggests binary content.
    /// </summary>
    public required string TextPreview { get; init; }
}
