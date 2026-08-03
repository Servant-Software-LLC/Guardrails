using Guardrails.Core.Io;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #407 — the Windows short-junction + worktree-root LIFECYCLE. Before #407 a junction
/// (<c>&lt;drive&gt;:\.a</c>..<c>\.z</c> → <c>&lt;temp&gt;/gr-wt/&lt;hash&gt;</c>) and its real root were torn
/// down ONLY by <c>--fresh</c> (<see cref="State.RunReset"/>), so every distinct plan run leaked both forever
/// — the drive letters exhausted at 26, and past exhaustion the run fell back to the long real root and the
/// MAX_PATH halt (GR2038) the junction exists to prevent RETURNED. Two levers here bound the accumulation:
/// <list type="bullet">
/// <item><b>A — <see cref="CleanupCompletedRun"/></b>: at a run's TERMINAL, NON-resumable completion (wholly
///   green AND delivered) reclaim its junction LINK + real root. A resumable outcome
///   (needs-human / halt / undelivered / cancelled) KEEPS both — a resume needs them.</item>
/// <item><b>B — <see cref="Reclaim"/></b>: at every worktree-mode run START, a robust GC that reclaims
///   LEAKS from crashed / killed / abandoned runs that never reached A — but NEVER an active or
///   freshly-halted run's tree. This is what bounds accumulation regardless of how runs end.</item>
/// </list>
/// (Lever C — lazy/predictive junction creation — lives in <see cref="WorktreeJunction.RealRootNeedsJunction"/>,
/// so most runs create no junction at all. A cleans up those that do; B reclaims the leaks.)
/// </summary>
/// <remarks>
/// <para>
/// <b>The B reclaimable predicate + its safety argument (the highest-risk part).</b> A false reclaim of an
/// ACTIVE run's tree (its build/test crashing mid-run) is far worse than a lingering leak, so B reclaims a
/// tree ONLY when it is provably not in use:
/// </para>
/// <list type="number">
/// <item>The CURRENT run's own real root + recorded junction are EXCLUDED up-front (a resume's root is the
///   one case that legitimately looks stale yet must be kept).</item>
/// <item>A junction whose target is GONE (dangling) is reclaimed link-only — a dangling junction cannot
///   belong to a live run, and <see cref="WorktreeJunction.RemoveJunctionLink"/> only ever deletes a reparse
///   point, never a real directory (the data-loss guard).</item>
/// <item>Any OTHER root/junction-target is reclaimed only when its whole tree is STALE — untouched for
///   longer than <see cref="StalenessThreshold"/>. A live run (this OR a concurrent one, same repo or
///   foreign) writes to its worktree tree — git checkouts, build outputs, per-attempt commits — far more
///   frequently than the threshold, so its mtime stays fresh; and a live-but-IDLE run (nothing written yet:
///   a <c>prompt</c>-policy run parked at a TTY) is kept by its live-process LOCK
///   (<see cref="IsLockedByLiveProcess"/>, #407 review Finding 1) — the liveness signal mtime alone cannot
///   give. On ANY uncertainty (an unscannable tree, an IO fault) the tree is treated as NOT stale (kept):
///   err toward KEEPING.</item>
/// </list>
/// <para>
/// A HALTED (EXITED) resumable run is NOT lock-protected — its process is gone — so once its tree passes
/// <see cref="StalenessThreshold"/> a concurrent run's GC CAN reclaim it (expected for a needs-human halt
/// left past the threshold, not "impossible"); even then no work is lost:
/// the durable deliverable lives on the plan branch <c>guardrails/&lt;plan&gt;</c> + the salvage refs in the
/// USER's repo (never in the reclaimed worktree root), and the resume simply RECREATES the junction + root
/// from the journal record (<see cref="WorktreeJunction.ResolveForRun"/> recreates a missing link; <c>git
/// worktree add</c> recreates a missing segment). B thus trades only warm cache, never correctness.
/// </para>
/// </remarks>
public static class WorktreeReclaim
{
    /// <summary>
    /// The conservative age (issue #407 B) beyond which a worktree tree untouched for that long is treated
    /// as an ABANDONED leak eligible for reclaim. Generous by design (err toward KEEPING): a live run
    /// refreshes its tree within minutes, so this never races an active run; the common green case is
    /// reclaimed promptly by A (<see cref="CleanupCompletedRun"/>), leaving B to bound only the crashed /
    /// killed / long-abandoned tail. A single named constant so the policy is tunable in one place.
    /// </summary>
    public static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(24);

    /// <summary>
    /// The per-run liveness sentinel (#407 review Finding 1) stamped into the worktree ROOT at run start:
    /// <c>&lt;pid&gt;\n&lt;process-start-ticks-utc&gt;</c>. The startup GC (B) treats a root whose lock names a
    /// STILL-LIVE process as in-use and never reclaims it — restoring the "no active run" signal mtime alone
    /// cannot give (a live-but-IDLE run — e.g. a <c>prompt</c>-policy run parked at a TTY — writes nothing yet
    /// must not be reclaimed). The start-ticks stamp guards PID reuse. A crashed/exited run's lock names a
    /// dead (or reused-but-mismatched) pid, so it does NOT protect the tree — mtime staleness reclaims it.
    /// </summary>
    public const string RunLockFileName = ".gr-run.lock";

    /// <summary>
    /// Stamp this run's liveness lock into its worktree ROOT (#407 F1). Best-effort — a missing lock just
    /// means B falls back to mtime staleness for this root, so a write failure never breaks the run.
    /// </summary>
    public static void WriteRunLock(string realRoot)
    {
        try
        {
            Directory.CreateDirectory(realRoot);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(
                Path.Combine(realRoot, RunLockFileName),
                $"{self.Id}\n{self.StartTime.ToUniversalTime().Ticks}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Best-effort — the mtime-staleness backstop still bounds the leak.
        }
    }

    /// <summary>
    /// True when <paramref name="root"/> carries a liveness lock (<see cref="RunLockFileName"/>) naming a
    /// STILL-RUNNING process whose start time MATCHES (PID-reuse-safe) — an ACTIVE run owns this tree, so B
    /// KEEPS it regardless of mtime (#407 F1). A missing / unparseable / dead-pid / mismatched lock returns
    /// false — NOT "reclaim now" but "no live-lock signal": the caller then falls to mtime staleness. A live
    /// AND writing run is also protected by its fresh mtime; the lock adds the live-but-IDLE case. Internal +
    /// pure over the filesystem / process table — testable by locking a root to the test's own live process.
    /// </summary>
    internal static bool IsLockedByLiveProcess(string root)
    {
        string lockPath = Path.Combine(root, RunLockFileName);
        string content;
        try
        {
            if (!File.Exists(lockPath)) return false;
            content = File.ReadAllText(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        string[] parts = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long startTicks))
        {
            return false;
        }

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.StartTime.ToUniversalTime().Ticks == startTicks; // same LIVE process (guards PID reuse)
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // no such process / exited / inaccessible → no confirmed live lock
        }
    }

    // ── A — terminal-completion cleanup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #407 A: is this run's outcome TERMINAL + NON-RESUMABLE, so its junction + worktree root should
    /// be reclaimed on completion? True ONLY for a wholly-green run whose terminal gate passed AND whose
    /// verified work was DELIVERED (or needed no delivery — serial/ff/clean-merge). False — KEEP both — for:
    /// <list type="bullet">
    /// <item>any non-green outcome (needs-human / blocked / failed / drift / wave-halt / abort / cancel):
    ///   RESUMABLE, a resume forks fresh segments under the recorded root;</item>
    /// <item>a wholly-green run whose delivery HALTED (conflict / dirty tree / hook-rejected — the outcome is
    ///   not FastForwarded/Merged): the user must act, then re-run to deliver;</item>
    /// <item>a wholly-green-but-UNDELIVERED opt-out (<see cref="RunReport.WhollyGreenButUndelivered"/>): the
    ///   verified work sits on the plan branch for the user to inspect / deliver, so its integration worktree
    ///   must survive (the startup GC reclaims it later once clearly abandoned).</item>
    /// </list>
    /// Pure over the report — unit-testable with fabricated outcomes. The caller ALSO requires worktree mode.
    /// </summary>
    public static bool ShouldReclaimOnCompletion(RunReport report, bool terminalGatePassed) =>
        report.AllSucceeded && terminalGatePassed
        && report.MergeOnSuccessOutcome is null
            or MergeOnSuccessResult.FastForwarded or MergeOnSuccessResult.Merged
        && !report.WhollyGreenButUndelivered;


    /// <summary>
    /// Issue #407 A: reclaim a TERMINALLY-COMPLETE (wholly green + delivered, non-resumable) run's worktree
    /// root <paramref name="realRoot"/> and, when present, its short junction LINK
    /// <paramref name="junctionRoot"/>. The caller gates this on the terminal-green determination and skips
    /// it for every RESUMABLE outcome (needs-human / halt / undelivered / cancelled), which keeps both for
    /// the resume. Cross-platform: the <c>gr-wt/&lt;hash&gt;</c> root leaks on every OS, while
    /// <see cref="WorktreeJunction.RemoveJunctionLink"/> is a Windows-only no-op elsewhere. Best-effort — a
    /// cleanup hiccup never changes the run's verdict or exit code.
    /// </summary>
    public static void CleanupCompletedRun(string workspace, string realRoot, string? junctionRoot, TextWriter log)
    {
        try
        {
            // Prune the git worktrees + delete the real root FIRST (git stores real paths — the junction
            // aliased them during the run but its own registrations are canonical), THEN remove the link.
            // Mirrors RunReset's --fresh order; never deletes the plan branch (the delivered work survives).
            if (Directory.Exists(realRoot))
            {
                GitWorktreeProvider.RemoveWorktreeRoot(workspace, realRoot);
            }

            // Link-only, reparse-point-guarded — can never recurse into or delete the (already-removed) target.
            if (!string.IsNullOrWhiteSpace(junctionRoot))
            {
                WorktreeJunction.RemoveJunctionLink(junctionRoot);
            }

            log.WriteLine(
                $"[guardrails] worktree reclaimed on completion: removed the run's worktree root '{realRoot}'"
                + (string.IsNullOrWhiteSpace(junctionRoot) ? "" : $" and junction '{junctionRoot}'")
                + " (issue #407 A).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — a lingering leak is harmless and the startup GC (B) is the backstop.
        }
    }

    // ── B — startup garbage collection ───────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #407 B: at a worktree-mode run START, sweep and reclaim LEAKED junctions + roots from crashed /
    /// killed / abandoned runs, while NEVER touching an active or resumable run's tree (see the type
    /// remarks for the full safety argument). <paramref name="currentRealRoot"/> and
    /// <paramref name="currentJunctionRoot"/> are this run's own root + recorded link — EXCLUDED from the
    /// sweep. Windows drive-root junctions are swept only on Windows; the <c>gr-wt/*</c> + legacy
    /// <c>guardrails-worktrees/*</c> roots are swept on every OS. Best-effort — a GC hiccup never blocks the
    /// run; each reclaim logs one line.
    /// </summary>
    public static void Reclaim(string workspace, string currentRealRoot, string? currentJunctionRoot, TextWriter log)
    {
        DateTime cutoffUtc = DateTime.UtcNow - StalenessThreshold;

        // 1) Drive-root junctions .a..z (Windows only): a link whose target is GONE (dangling) or STALE.
        if (OperatingSystem.IsWindows())
        {
            try { ReclaimStaleJunctions(currentRealRoot, currentJunctionRoot, cutoffUtc, log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }

        // 2) Worktree roots (cross-platform): a gr-wt/* or guardrails-worktrees/* root whose whole tree is STALE.
        try { ReclaimStaleRoots(workspace, currentRealRoot, cutoffUtc, log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// The default cap on how many stale roots the EXIT sweep (<see cref="ReclaimRootsOnExit"/>) reclaims in
    /// one run — small so it never delays the visible exit. The rest are mopped up by the next run's startup
    /// GC (<see cref="Reclaim"/>), so the two together still bound the accumulation.
    /// </summary>
    public const int ExitSweepCap = 16;

    /// <summary>
    /// Issue #419 — reclaim LEAKED worktree ROOTS at a run's EXIT path (the run's <c>finally</c>), so a
    /// dogfood session's LAST run reclaims the session's abandoned roots ON ITS WAY OUT rather than leaving
    /// them for a future run (the exact #408 gap — the startup GC B fires only at the START of a
    /// worktree-mode run, so the final run of a session never swept). ROOT-ONLY (the junction LINK is
    /// released by <see cref="WorktreeJunctionLifetime"/>), cross-OS, best-effort, and COUNT-CAPPED
    /// (<paramref name="maxReclaims"/>, default <see cref="ExitSweepCap"/>) so it never delays exit. Safety
    /// is identical to B: <paramref name="currentRealRoot"/> is EXCLUDED (this run's own root — kept for a
    /// resumable outcome, and doubly protected by its still-live process lock since this process is alive in
    /// its own finally), a live-locked or fresh (&lt; <see cref="StalenessThreshold"/>) tree is KEPT, and
    /// only a STALE, unlocked, non-current root is reclaimed.
    /// </summary>
    public static void ReclaimRootsOnExit(
        string workspace, string currentRealRoot, TextWriter log, int maxReclaims = ExitSweepCap)
    {
        DateTime cutoffUtc = DateTime.UtcNow - StalenessThreshold;
        try
        {
            SweepRoots(
                CandidateRootParents(currentRealRoot), workspace, currentRealRoot, cutoffUtc, log, maxReclaims);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — a sweep hiccup never changes the run's verdict or exit code.
        }
    }

    private static void ReclaimStaleJunctions(
        string currentRealRoot, string? currentJunctionRoot, DateTime cutoffUtc, TextWriter log)
    {
        string? drive = Path.GetPathRoot(currentRealRoot);
        if (string.IsNullOrWhiteSpace(drive)) return;

        SweepJunctions(drive, currentJunctionRoot, cutoffUtc, log);
    }

    /// <summary>
    /// Sweep <c>&lt;baseDir&gt;\.a</c>..<c>\.z</c> (issue #407 B), reclaiming (link-only) any junction whose
    /// target is GONE (dangling) or whose target tree is STALE, while KEEPING the current run's own link
    /// (<paramref name="currentJunctionRoot"/>) and any junction to a FRESH target (a possibly-active run).
    /// Internal + <paramref name="baseDir"/>-parameterized so tests exercise it against a controlled temp
    /// base — never the real drive root. Windows-only in effect (junctions exist only there).
    /// </summary>
    internal static void SweepJunctions(
        string baseDir, string? currentJunctionRoot, DateTime cutoffUtc, TextWriter log)
    {
        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            string link = Path.Combine(baseDir, leaf);

            // Never THIS run's own junction (a resume's recorded link is legitimately old yet in-use).
            if (currentJunctionRoot is not null && WorktreeJunction.SamePath(link, currentJunctionRoot))
            {
                continue;
            }

            if (!WorktreeJunction.IsReparsePoint(link)) continue; // a free name or a real dir — not ours

            try
            {
                string? target = TryLinkTarget(link);

                // Finding 3: DANGLING only when the read SUCCEEDED and the target is genuinely gone. A FAILED
                // read (transient IO / sharing lock ⇒ target null) is UNKNOWN → KEEP (err toward keeping, as
                // TreeIsStale does), never a blind removal that could break a foreign live run.
                if (target is null) continue;
                bool dangling = !Directory.Exists(target);

                // F1: a live-locked target (an active run, incl. idle) is KEPT regardless of mtime.
                if (!dangling && (IsLockedByLiveProcess(target) || !TreeIsStale(target, cutoffUtc))) continue;

                WorktreeJunction.RemoveJunctionLink(link); // link-only; the root sweep reclaims a stale target dir
                log.WriteLine(
                    $"[guardrails] GC: reclaimed leaked worktree junction '{link}'"
                    + (dangling ? " (target gone)" : $" (target '{target}' idle > {StalenessThreshold.TotalHours:0}h, no live run)")
                    + " (issue #407 B).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // N2: one racy link never aborts the rest of the sweep.
            }
        }
    }

    private static void ReclaimStaleRoots(
        string workspace, string currentRealRoot, DateTime cutoffUtc, TextWriter log) =>
        SweepRoots(CandidateRootParents(currentRealRoot), workspace, currentRealRoot, cutoffUtc, log);

    /// <summary>
    /// Sweep the child subdirs of each of <paramref name="parents"/> (issue #407 B), reclaiming (git-prune +
    /// delete via <see cref="GitWorktreeProvider.RemoveWorktreeRoot"/>) any whose whole tree is STALE, while
    /// KEEPING the current run's own root (<paramref name="currentRealRoot"/>) and any FRESH tree (a
    /// possibly-active run). Internal + <paramref name="parents"/>-parameterized so tests exercise it against
    /// a controlled temp parent — never the real <c>gr-wt</c> / <c>guardrails-worktrees</c> dirs. Cross-OS.
    /// </summary>
    internal static void SweepRoots(
        IEnumerable<string> parents, string workspace, string? currentRealRoot, DateTime cutoffUtc,
        TextWriter log, int maxReclaims = int.MaxValue)
    {
        int reclaimed = 0;
        foreach (string parent in parents)
        {
            if (reclaimed >= maxReclaims) return;   // #419: honor the exit-sweep cap
            if (!Directory.Exists(parent)) continue;

            string[] children;
            try { children = Directory.GetDirectories(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string root in children)
            {
                if (reclaimed >= maxReclaims) return; // #419: stop once the cap is hit (never delay the exit)
                try
                {
                    // Never THIS run's root (a resume's root is legitimately old yet in-use).
                    if (currentRealRoot is not null && WorktreeJunction.SamePath(root, currentRealRoot)) continue;
                    // F1: a live-locked root (an active run, incl. idle) is KEPT regardless of mtime.
                    if (IsLockedByLiveProcess(root)) continue;
                    if (!TreeIsStale(root, cutoffUtc)) continue; // fresh → maybe active → KEEP

                    GitWorktreeProvider.RemoveWorktreeRoot(workspace, root);
                    reclaimed++;
                    log.WriteLine(
                        $"[guardrails] GC: reclaimed leaked worktree root '{root}' "
                        + $"(idle > {StalenessThreshold.TotalHours:0}h, no live run) (issue #407 B).");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // N2: one racy/locked root never aborts the rest of the sweep.
                }
            }
        }
    }

    /// <summary>
    /// The directories whose child subdirs are candidate leaked worktree roots (issue #407 B): the current
    /// run's OWN root parent (covers an env/config <c>GUARDRAILS_WORKTREE_ROOT</c> override drive, e.g.
    /// <c>C:\gw</c>), the default short <c>&lt;temp&gt;/gr-wt</c> parent, and the legacy long-path
    /// <c>&lt;temp&gt;/guardrails-worktrees</c> parent (issue #384/#407 — sweep BOTH). Deduplicated.
    /// </summary>
    private static IEnumerable<string> CandidateRootParents(string currentRealRoot)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (Path.GetDirectoryName(currentRealRoot) is { Length: > 0 } ownParent && seen.Add(ownParent))
        {
            yield return ownParent;
        }

        string temp = Path.GetTempPath();
        string grWt = Path.Combine(temp, "gr-wt");
        if (seen.Add(grWt)) yield return grWt;

        string legacy = Path.Combine(temp, "guardrails-worktrees");
        if (seen.Add(legacy)) yield return legacy;
    }

    // ── shared / testable helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when NOTHING in the tree rooted at <paramref name="root"/> was modified after
    /// <paramref name="cutoffUtc"/> — the issue #407 B staleness test. Short-circuits on the first fresh
    /// entry (the common active-root case is cheap). On any scan failure returns FALSE (not stale = KEEP):
    /// err toward keeping when the tree cannot be inspected. Pure over the filesystem — testable cross-OS by
    /// controlling entry mtimes.
    /// </summary>
    internal static bool TreeIsStale(string root, DateTime cutoffUtc)
    {
        try
        {
            if (Directory.GetLastWriteTimeUtc(root) > cutoffUtc) return false;

            foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(entry) > cutoffUtc) return false; // a fresh entry ⇒ not stale
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // cannot scan ⇒ err toward KEEPING
        }
    }

    private static string? TryLinkTarget(string link)
    {
        try { return new DirectoryInfo(link).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
