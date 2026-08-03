using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #383 (Windows short-junction worktree root). Layered ON TOP of the short default root +
/// <c>GUARDRAILS_WORKTREE_ROOT</c>/<c>worktreeRoot</c> override + the GR2038 preflight, this is the
/// STRONGER primary Windows lever: in worktree mode on Windows the harness roots segment worktrees under a
/// short directory JUNCTION (a reparse point — needs NO admin / Developer Mode, unlike a symlink) at
/// <c>&lt;drive&gt;:\.a</c> … <c>\.z</c> pointing at the real worktree root, and hands that junction path
/// to <see cref="GitWorktreeProvider"/> as the effective root. Each task's child-process cwd — and thus
/// <c>dotnet test</c>'s built <c>bin\…\&lt;assembly&gt;.exe</c> path — then stays clear of Windows MAX_PATH
/// (260), which <c>CreateProcessW</c> enforces on a spawned process's application name REGARDLESS of
/// <c>LongPathsEnabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>git canonicalizes the junction away — so the link is a PROCESS-SCOPED ALIAS, never run state (issue
/// #419).</b> Empirically (git 2.53), <c>git worktree add</c> resolves a junction path back to its real
/// target and stores the REAL long path in its own worktree registrations — so the chosen short link exists
/// NOWHERE in git's records. Two consequences: (1) PRUNE/teardown already key on the real root
/// (git-authoritative), so only the FORWARD segment-creation path (the child-process cwd) uses the junction
/// alias; (2) because the deterministic segment subpath (<c>&lt;root&gt;/&lt;runId&gt;/&lt;taskId&gt;/attempt-N</c>)
/// resolves to the SAME physical tree under ANY letter that junctions to the real root, a resume does NOT
/// need the same <c>.a</c>..<c>.z</c> letter. So the link is NOT journaled: each run ALLOCATES A FRESH
/// first-free letter, and <see cref="WorktreeJunctionLifetime"/> RELEASES it on every recoverable process
/// exit. The live-junction count therefore collapses to the number of concurrently-running processes
/// (normally 1); a hard-kill that skips the release leaks at most ONE link, reclaimed by the startup GC
/// (<see cref="WorktreeReclaim"/> B). The junction functions exactly as the "child-process cwd only" short
/// alias — which is precisely why the fix holds: the harness, not git, chooses the cwd string it launches
/// build/test under.
/// </para>
/// <para>
/// <b>Safety.</b> Teardown removes the LINK ONLY — <see cref="RemoveJunctionLink"/> deletes a reparse point
/// with <c>recursive:false</c> (which removes the junction, never the target's contents) and refuses to
/// touch a path that is not a reparse point, so it can never recurse into or delete a real directory.
/// </para>
/// <para>
/// <b>Testability.</b> The primitives (<see cref="TryCreateJunction"/>, <see cref="RemoveJunctionLink"/>,
/// <see cref="IsJunctionTo"/>) and the allocation (<see cref="AllocateUnder"/> / <see cref="ResolveForRun"/>)
/// all take an explicit link/base path, so tests exercise the create→use→teardown cycle against a temp
/// directory — never the real <c>C:\</c> root — and gate the Windows-only junction-creation assertions on
/// <see cref="OperatingSystem.IsWindows"/>.
/// </para>
/// </remarks>
public static class WorktreeJunction
{
    /// <summary>
    /// The candidate leaf names <c>.a</c>..<c>.z</c> tried in order. A leading <c>.</c> marks the link as
    /// harness-owned/hidden; combined with a drive root it forms a 5-char path (<c>C:\.a</c>). The first
    /// name that is free (or already OUR junction) wins; if all 26 are taken by OTHER targets — many
    /// concurrent runs, rare — allocation returns null and the caller falls back to the real root.
    /// </summary>
    public static IReadOnlyList<string> CandidateLeaves { get; } =
        Enumerable.Range('a', 26).Select(c => "." + (char)c).ToArray();

    /// <summary>
    /// Issue #407 C (lazy / predictive creation) — the EXTRA MAX_PATH headroom (chars), on top of the
    /// GR2038 build-output reserve (<see cref="WorktreePathPreflight.BuildOutputReserve"/>), the REAL root
    /// must leave for EVERY task before a fresh run SKIPS the junction. A junction only ever SHORTENS the
    /// root, so when the real root already clears MAX_PATH with this comfortable margin the junction is
    /// pure churn — and, unmanaged, that churn is the #407 leak. We skip it only with margin to spare
    /// because a false skip is a MAX_PATH halt (GR2038): err toward CREATING when close.
    /// <para>
    /// #407 review Finding 4: pre-#407 every Windows worktree run got a junction, so
    /// <see cref="WorktreePathPreflight.BuildOutputReserve"/>'s accuracy never mattered here; C now sends the
    /// "fits" set onto the REAL root, making that reserve load-bearing. This margin is sized to ABSORB a
    /// reserve under-estimate (a deep monorepo <c>src/.../bin/&lt;cfg&gt;/&lt;tfm&gt;/&lt;assembly&gt;.exe</c>
    /// tail that exceeds the reserve) — a skip therefore needs the real root to clear MAX_PATH by
    /// reserve + this margin, not the reserve alone.
    /// </para>
    /// </summary>
    public const int JunctionSkipMargin = 60;

    /// <summary>
    /// Issue #407 C: does the REAL (non-junction) worktree root lack comfortable MAX_PATH headroom for at
    /// least one task — i.e. must this run create a short junction? True when ANY task's segment base
    /// <c>&lt;realRoot&gt;/&lt;runId&gt;/&lt;taskId&gt;/attempt-1</c> + the GR2038 build-output reserve +
    /// <see cref="JunctionSkipMargin"/> would exceed Windows MAX_PATH; false only when EVERY task fits with
    /// that margin (skip the junction, use the real short-default root directly). Reuses the EXACT GR2038
    /// segment-base computation so "skip the junction" ⇔ "the real root comfortably clears the very
    /// preflight the run enforces". Pure + OS-agnostic (a separator is one char on every platform); the
    /// Windows-only gate is at the caller. An EMPTY task set returns false (no segment paths ⇒ no risk;
    /// a partially-authored waved plan self-corrects on the resume that authors its deeper wave).
    /// </summary>
    public static bool RealRootNeedsJunction(string realRoot, string runId, IEnumerable<string> taskIds)
    {
        foreach (string taskId in taskIds)
        {
            // Include the "fork" segment (#407 review Finding 4): a forked segment lives at
            // <realRoot>/<runId>/fork/<taskId>/attempt-N — the DEEPEST shape — so measuring the fork base (not
            // the shorter _integration base) keeps the skip decision conservative. attempt-1 is length-
            // representative of attempt-N (single-digit N).
            int baseLength = Path.Combine(realRoot, runId, "fork", taskId, "attempt-1").Length;
            if (baseLength + WorktreePathPreflight.BuildOutputReserve + JunctionSkipMargin
                > WorktreePathPreflight.MaxPathLimit)
            {
                return true; // at least one task is at risk under the real root → create the junction
            }
        }

        return false; // every task fits under the real root with margin → the junction is unneeded churn
    }

    /// <summary>
    /// Resolve the run's effective worktree root (Windows worktree mode), ALLOCATING A FRESH junction every
    /// run (issue #419 — the link is a process-scoped alias, never resume state). Returns the first-free
    /// <c>&lt;baseDir&gt;\.a</c>..<c>\.z</c> junction to <paramref name="realRoot"/> when one is created, else
    /// <paramref name="realRoot"/> itself (non-Windows, the #407 C lazy-skip, or a graceful create failure).
    /// The caller detects "a junction was created" by <c>result != realRoot</c> and constructs a
    /// <see cref="WorktreeJunctionLifetime"/> to release it on exit. There is NO resume restore and NO
    /// same-letter reuse: a resume just allocates its own free letter, and the deterministic segment subpath
    /// resolves the SAME tree under it (git canonicalized the junction away). Any junction creation failure
    /// (locked-down root ACL, non-NTFS, sandbox) degrades GRACEFULLY to the real root — the run proceeds,
    /// relying on the short default + GR2038 backstop; the junction is an optimization that must never block
    /// an otherwise-workable run.
    /// </summary>
    /// <param name="realRoot">The real worktree root (<see cref="SchedulerFactory.WorktreeRootFor"/>): the env/config/short-default result and the junction's target.</param>
    /// <param name="baseDir">The directory the short link is allocated under — the drive root in production (<c>Path.GetPathRoot(realRoot)</c>); a temp dir in tests.</param>
    /// <param name="log">Best-effort operator log (a one-line note on create/skip/fallback).</param>
    /// <param name="runId">The run id (issue #407 C — the lazy-creation predicate's segment shape); null skips the lazy decision (always create).</param>
    /// <param name="taskIds">The run's task ids (issue #407 C); null/absent skips the lazy decision — err toward CREATING when the run shape is unknown.</param>
    public static string ResolveForRun(
        string realRoot, string baseDir, TextWriter log,
        string? runId = null, IReadOnlyCollection<string>? taskIds = null)
    {
        // Junctions are a Windows-only optimization; elsewhere the effective root is the real root.
        if (!OperatingSystem.IsWindows())
        {
            return realRoot;
        }

        // Issue #407 C (lazy / predictive creation): most runs' real root already clears MAX_PATH with room
        // to spare, so creating a junction just adds churn. Only create one when a segment path under the
        // real root would be at risk. Err toward CREATING when the run shape is unknown (null runId/taskIds)
        // — a false negative is a MAX_PATH halt, not a leak.
        if (runId is not null && taskIds is not null
            && !RealRootNeedsJunction(realRoot, runId, taskIds))
        {
            log.WriteLine(
                $"[guardrails] worktree junction not needed — the real root '{realRoot}' leaves MAX_PATH "
                + "headroom for every task; using it directly, no junction created (issue #407 C).");
            return realRoot;
        }

        // Allocate a FRESH first-free short junction under the drive root (issue #419 — no same-letter
        // restore; the link is released on exit and re-allocated fresh next run).
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return Fallback(realRoot, log, "no drive root is available to allocate a short junction under");
        }

        string? chosen = AllocateUnder(baseDir.Trim(), realRoot, log);
        if (chosen is null)
        {
            return Fallback(realRoot, log,
                $"could not allocate a short worktree junction under '{baseDir.Trim()}' "
                + $"(all {CandidateLeaves.Count} names are taken or creation was refused)");
        }

        log.WriteLine(
            $"[guardrails] worktree junction: '{chosen}' -> '{realRoot}' — a short root keeps build/test "
            + "paths clear of Windows MAX_PATH (issue #383); released on exit (issue #419).");
        return chosen;
    }

    /// <summary>
    /// Remove every <c>&lt;baseDir&gt;\.a</c>..<c>\.z</c> junction whose target resolves to
    /// <paramref name="target"/> (LINK-ONLY, reparse-point-guarded). Used by <c>--fresh</c>
    /// (<see cref="State.RunReset"/>) to tear down THIS plan's short link without any journal record — the
    /// link is no longer journaled (issue #419), so <c>--fresh</c> sweeps the drive-root candidates for a
    /// junction pointing at the plan's real root instead. Best-effort and a no-op on non-Windows (junctions
    /// exist only there) or a missing/whitespace argument.
    /// </summary>
    public static void RemoveJunctionsTo(string baseDir, string target)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        foreach (string leaf in CandidateLeaves)
        {
            string link = Path.Combine(baseDir, leaf);
            if (IsJunctionTo(link, target))
            {
                RemoveJunctionLink(link); // link-only; the target (the real worktree root) survives
            }
        }
    }

    /// <summary>
    /// Return the first <c>&lt;baseDir&gt;\.a</c>..<c>\.z</c> that is free (create the junction to
    /// <paramref name="target"/>) or is ALREADY our junction to <paramref name="target"/> (reuse — a
    /// prior-run leftover for the SAME plan, or an idempotent re-entry); skip any name held by something
    /// else. Null when every name is taken by another target or every creation is refused (locked-down
    /// root). Testable cross-OS for the SKIP/exhaustion path (no junction is created there); the CREATE
    /// path needs Windows.
    /// </summary>
    public static string? AllocateUnder(string baseDir, string target, TextWriter log)
    {
        foreach (string leaf in CandidateLeaves)
        {
            string candidate = Path.Combine(baseDir, leaf);
            if (Directory.Exists(candidate))
            {
                if (IsJunctionTo(candidate, target))
                {
                    return candidate; // already ours — reuse (idempotent / same-plan leftover)
                }

                continue; // a real dir, or a junction to a different target — belongs to someone else
            }

            if (TryCreateJunction(candidate, target))
            {
                return candidate;
            }
            // Creation refused on a free name (ACL / non-NTFS root) — try the next; if all refuse → null.
        }

        return null;
    }

    /// <summary>
    /// Create a directory JUNCTION at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>
    /// via <c>cmd /c mklink /J</c> (a reparse point — needs no elevation / Developer Mode, unlike a
    /// symlink). The target directory is created first (it is the harness-owned real worktree root).
    /// Returns true only when the on-disk result is an actual reparse point (trusts the filesystem, not
    /// just the process exit code). No-op false on non-Windows or a spawn/IO failure — the caller then
    /// falls back to the real root, never crashing.
    /// </summary>
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

            // ArgumentList only (never a concatenated command string) — .NET escapes each arg, and the
            // harness's own worktree/drive-root paths carry no cmd metacharacters.
            var psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(targetPath);

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 && IsReparsePoint(linkPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove the junction LINK at <paramref name="linkPath"/> and NOTHING ELSE (the data-loss guard).
    /// Deletes only when the path is an actual reparse point, with <c>recursive:false</c> — which removes
    /// the junction itself, leaving the target directory and its contents intact. A path that is NOT a
    /// reparse point (a real directory) is never touched, so this can never recurse into or delete a real
    /// tree. Best-effort and no-op on non-Windows: a lingering junction is harmless (a later run reuses or
    /// skips it).
    /// </summary>
    public static void RemoveJunctionLink(string linkPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            if (!IsReparsePoint(linkPath))
            {
                return; // SAFETY: only ever delete a reparse point, never a real directory
            }

            Directory.Delete(linkPath, recursive: false); // removes the LINK only; target contents survive
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — a lingering junction is harmless.
        }
    }

    /// <summary>True when <paramref name="path"/> exists and is a reparse point (a junction/symlink).</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return Directory.Exists(path)
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="linkPath"/> is a reparse point whose target resolves to <paramref name="targetPath"/>.</summary>
    public static bool IsJunctionTo(string linkPath, string targetPath)
    {
        if (!IsReparsePoint(linkPath))
        {
            return false;
        }

        try
        {
            string? actual = new DirectoryInfo(linkPath).LinkTarget;
            return actual is not null && SamePath(actual, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Full-path, trailing-separator-insensitive, case-insensitive (Windows) path equality. Shared with <see cref="WorktreeReclaim"/> (issue #407).</summary>
    internal static bool SamePath(string a, string b)
    {
        try
        {
            string na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>Log the graceful fallback (junction unavailable) and return the real root as the effective root.</summary>
    private static string Fallback(string realRoot, TextWriter log, string why)
    {
        log.WriteLine(
            $"[guardrails] worktree junction unavailable ({why}); using the real worktree root '{realRoot}'. "
            + "The run proceeds; if a path-length halt (GR2038) follows, set GUARDRAILS_WORKTREE_ROOT to a "
            + "short path (issue #383).");
        return realRoot;
    }
}
