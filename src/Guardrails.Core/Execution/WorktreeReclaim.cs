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
    /// run.
    /// <para>
    /// Issue #450 — the startup sweep is bounded in BOTH dimensions a user experiences it in. Its
    /// wall-clock cost is capped by <see cref="StartupSweepBudget"/> (a shared deadline across both sweeps,
    /// so pre-run latency is bounded no matter how large the backlog), and its console output is capped by
    /// <see cref="ReclaimLogDetailCap"/> detail lines plus one summary — because what actually made a user
    /// kill a healthy run was hundreds of near-identical lines, not the work itself. A staleness memo is
    /// shared between the two sweeps so a confirmed-stale junction TARGET is not tree-scanned again as a
    /// root (issue #409 N1).
    /// </para>
    /// </summary>
    public static void Reclaim(string workspace, string currentRealRoot, string? currentJunctionRoot, TextWriter log)
    {
        DateTime cutoffUtc = DateTime.UtcNow - StalenessThreshold;
        DateTime deadlineUtc = DateTime.UtcNow + StartupSweepBudget;
        var staleness = new Dictionary<string, bool>(PathKeyComparer);

        // 1) Drive-root junctions .a..z (Windows only): a link whose target is GONE (dangling) or STALE.
        if (OperatingSystem.IsWindows())
        {
            try { ReclaimStaleJunctions(currentRealRoot, currentJunctionRoot, cutoffUtc, log, deadlineUtc, staleness); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }

        // 2) Worktree roots (cross-platform): a gr-wt/* or guardrails-worktrees/* root whose whole tree is STALE.
        try { ReclaimStaleRoots(workspace, currentRealRoot, cutoffUtc, log, deadlineUtc, staleness); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// The default cap on how many stale roots the EXIT sweep (<see cref="ReclaimRootsOnExit"/>) reclaims in
    /// one run — small so it never delays the visible exit. The rest are mopped up by the next run's startup
    /// GC (<see cref="Reclaim"/>), so the two together still bound the accumulation.
    /// </summary>
    public const int ExitSweepCap = 16;

    /// <summary>
    /// Issue #450 — the wall-clock budget the STARTUP sweep (<see cref="Reclaim"/>) may spend before it
    /// stops and leaves the remainder to the next run.
    /// <para>
    /// <b>Why a TIME budget and not the exit sweep's count cap.</b> A count cap bounds the wrong quantity:
    /// what blocks the user is elapsed time, and per-root cost varies by three orders of magnitude (a small
    /// fixture root is a directory delete; a full checkout is a recursive stat of a whole source tree). A
    /// cap tuned for the expensive root throttles the cheap one pointlessly — <see cref="ExitSweepCap"/>'s
    /// 16 would need ~63 runs to drain the ~1000-root backlog that provoked this issue, while a 5-second
    /// budget drains it in one. A deadline instead bounds exactly the thing the user feels, and
    /// self-adjusts: it reclaims as much as fits, whatever each root costs.
    /// </para>
    /// <para>
    /// Five seconds is chosen against the alternative it delays: a run measured in minutes-to-hours. It is
    /// long enough to clear a large backlog of cheap roots in one pass and short enough that a worst case
    /// is never mistaken for a hang — especially since the first reclaims print immediately, so the silent
    /// window is bounded by this value and nothing else.
    /// </para>
    /// </summary>
    public static readonly TimeSpan StartupSweepBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Issue #450 — how many per-root reclaim lines a sweep prints in FULL before it falls back to a single
    /// trailing count. The flood that made a user abort a healthy run was ~1000 lines of 130 characters
    /// carrying ~8 characters of signal each (only the hex root id differed), emitted before any of the
    /// run's own output. The first few lines are kept because they carry the real diagnostic content — which
    /// parent, what kind of root — and because output appearing IMMEDIATELY is what distinguishes a working
    /// sweep from a stuck one; beyond that, repetition adds nothing a count does not.
    /// </summary>
    public const int ReclaimLogDetailCap = 3;

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
        string currentRealRoot, string? currentJunctionRoot, DateTime cutoffUtc, TextWriter log,
        DateTime? deadlineUtc, IDictionary<string, bool>? stalenessCache)
    {
        string? drive = Path.GetPathRoot(currentRealRoot);
        if (string.IsNullOrWhiteSpace(drive)) return;

        SweepJunctions(drive, currentJunctionRoot, cutoffUtc, log, deadlineUtc, stalenessCache);
    }

    /// <summary>
    /// Sweep <c>&lt;baseDir&gt;\.a</c>..<c>\.z</c> (issue #407 B), reclaiming (link-only) any junction whose
    /// target is GONE (dangling) or whose target tree is STALE, while KEEPING the current run's own link
    /// (<paramref name="currentJunctionRoot"/>) and any junction to a FRESH target (a possibly-active run).
    /// Internal + <paramref name="baseDir"/>-parameterized so tests exercise it against a controlled temp
    /// base — never the real drive root. Windows-only in effect (junctions exist only there).
    /// </summary>
    internal static void SweepJunctions(
        string baseDir, string? currentJunctionRoot, DateTime cutoffUtc, TextWriter log,
        DateTime? deadlineUtc = null, IDictionary<string, bool>? stalenessCache = null)
    {
        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            // #450: the shared startup budget bounds this sweep too — a junction TARGET can be a whole
            // checkout, so its staleness scan is not automatically cheap.
            if (BudgetExhausted(deadlineUtc)) return;

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
                if (!dangling && (IsLockedByLiveProcess(target)
                    || !TreeIsStale(target, cutoffUtc, stalenessCache))) continue;

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
        string workspace, string currentRealRoot, DateTime cutoffUtc, TextWriter log,
        DateTime? deadlineUtc, IDictionary<string, bool>? stalenessCache) =>
        SweepRoots(
            CandidateRootParents(currentRealRoot), workspace, currentRealRoot, cutoffUtc, log,
            deadlineUtc: deadlineUtc, stalenessCache: stalenessCache);

    /// <summary>
    /// Sweep the child subdirs of each of <paramref name="parents"/> (issue #407 B), reclaiming (git-unregister
    /// + delete via <see cref="GitWorktreeProvider.RemoveWorktreeRoot(string, string, IReadOnlyList{string})"/>)
    /// any whose whole tree is STALE, while KEEPING the current run's own root
    /// (<paramref name="currentRealRoot"/>) and any FRESH tree (a possibly-active run). Internal +
    /// <paramref name="parents"/>-parameterized so tests exercise it against a controlled temp parent — never
    /// the real <c>gr-wt</c> / <c>guardrails-worktrees</c> dirs. Cross-OS.
    /// <para>
    /// Issue #450: bounded by <paramref name="maxReclaims"/> (the exit sweep's count cap, #419) AND
    /// <paramref name="deadlineUtc"/> (the startup sweep's time budget), whichever bites first; git
    /// bookkeeping is batched (one <c>worktree list</c> for the sweep, one <c>prune</c> at the end, and
    /// NEITHER for a root this repo never registered); and output is capped at
    /// <see cref="ReclaimLogDetailCap"/> detail lines plus a trailing count.
    /// </para>
    /// </summary>
    /// <param name="deadlineUtc">
    /// When the sweep must stop, or <c>null</c> for no time bound. Checked BEFORE each root, so it bounds
    /// the staleness SCAN of kept roots too — which is where the latency of a large backlog actually lives.
    /// </param>
    /// <param name="stalenessCache">
    /// Optional memo of <see cref="TreeIsStale"/> results shared with <see cref="SweepJunctions"/> in the
    /// same <see cref="Reclaim"/> (issue #409 N1: a confirmed-stale junction target was scanned twice — once
    /// as a target, once as a root). <c>null</c> disables memoisation.
    /// </param>
    internal static void SweepRoots(
        IEnumerable<string> parents, string workspace, string? currentRealRoot, DateTime cutoffUtc,
        TextWriter log, int maxReclaims = int.MaxValue, DateTime? deadlineUtc = null,
        IDictionary<string, bool>? stalenessCache = null)
    {
        int reclaimed = 0;
        bool stoppedEarly = false;
        bool prunePending = false;

        // Fetched lazily on the FIRST actual reclaim: a sweep that reclaims nothing (the overwhelmingly
        // common case) spawns no git at all, exactly as before (#450).
        IReadOnlyList<string>? registeredWorktrees = null;

        foreach (string parent in parents)
        {
            if (Exhausted()) { stoppedEarly = true; break; }
            if (!Directory.Exists(parent)) continue;

            string[] children;
            try { children = Directory.GetDirectories(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string root in children)
            {
                // #419 count cap / #450 time budget: stop once either bites (never delay the exit or the start).
                if (Exhausted()) { stoppedEarly = true; break; }
                try
                {
                    // Never THIS run's root (a resume's root is legitimately old yet in-use).
                    if (currentRealRoot is not null && WorktreeJunction.SamePath(root, currentRealRoot)) continue;
                    // F1: a live-locked root (an active run, incl. idle) is KEPT regardless of mtime.
                    if (IsLockedByLiveProcess(root)) continue;
                    if (!TreeIsStale(root, cutoffUtc, stalenessCache)) continue; // fresh → maybe active → KEEP

                    registeredWorktrees ??= GitWorktreeProvider.RegisteredWorktreePaths(workspace);
                    prunePending |= GitWorktreeProvider.RemoveWorktreeRoot(workspace, root, registeredWorktrees);
                    reclaimed++;

                    if (reclaimed <= ReclaimLogDetailCap)
                    {
                        log.WriteLine(
                            $"[guardrails] GC: reclaimed leaked worktree root '{root}' "
                            + $"(idle > {StalenessThreshold.TotalHours:0}h, no live run) (issue #407 B).");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // N2: one racy/locked root never aborts the rest of the sweep.
                }
            }

            if (stoppedEarly) break;
        }

        // One prune for the whole sweep, and only when something was actually unregistered (#450).
        if (prunePending)
        {
            GitWorktreeProvider.PruneWorktrees(workspace);
        }

        WriteSweepSummary(log, reclaimed, stoppedEarly);
        return;

        bool Exhausted() =>
            reclaimed >= maxReclaims || BudgetExhausted(deadlineUtc);
    }

    /// <summary>
    /// Issue #450 — close a root sweep's output: the count of reclaims BEYOND the per-root detail lines, and
    /// a note when the sweep stopped on its cap/budget with work possibly left. Silent when nothing was
    /// reclaimed, and silent when every reclaim already printed its own line — so the ordinary one-or-two-root
    /// sweep reads exactly as it did before, and only the flood case changes.
    /// </summary>
    private static void WriteSweepSummary(TextWriter log, int reclaimed, bool stoppedEarly)
    {
        if (reclaimed > ReclaimLogDetailCap)
        {
            log.WriteLine(
                $"[guardrails] GC: …and {reclaimed - ReclaimLogDetailCap} more leaked worktree root(s) "
                + $"reclaimed — {reclaimed} total (idle > {StalenessThreshold.TotalHours:0}h, no live run) "
                + "(issue #407 B).");
        }

        if (stoppedEarly && reclaimed > 0)
        {
            log.WriteLine(
                "[guardrails] GC: sweep stopped at its budget; any remaining leaked roots are reclaimed by "
                + "the next run (issue #450).");
        }
    }

    /// <summary>True once <paramref name="deadlineUtc"/> has passed. A null deadline never expires (#450).</summary>
    private static bool BudgetExhausted(DateTime? deadlineUtc) =>
        deadlineUtc is { } deadline && DateTime.UtcNow >= deadline;

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

    /// <summary>
    /// <see cref="TreeIsStale(string, DateTime)"/> through an optional per-sweep memo (issue #409 N1). The
    /// two sweeps of one <see cref="Reclaim"/> ask about the SAME directory whenever a junction points at a
    /// root under a swept parent — the junction sweep as a link target, the root sweep as a root — and a full
    /// recursive stat of a checkout is the single most expensive thing the GC does. Confining the memo to one
    /// <see cref="Reclaim"/> call keeps the answer seconds fresh, and the two readings are already separated
    /// only by the link removal between them, which does not touch the tree.
    /// </summary>
    private static bool TreeIsStale(string root, DateTime cutoffUtc, IDictionary<string, bool>? cache)
    {
        if (cache is null) return TreeIsStale(root, cutoffUtc);

        string key = PathKey(root);
        if (cache.TryGetValue(key, out bool cached)) return cached;

        bool stale = TreeIsStale(root, cutoffUtc);
        cache[key] = stale;
        return stale;
    }

    /// <summary>
    /// The staleness memo's key: the resolved full path without a trailing separator, so <c>root</c> and
    /// <c>root\</c> (and a junction target reported either way) collapse to one entry. Falls back to the raw
    /// string if the path cannot be resolved — a miss is only a repeated scan, never a wrong answer.
    /// </summary>
    private static string PathKey(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return path; }
    }

    /// <summary>
    /// The memo's comparer — matching <see cref="WorktreeJunction.SamePath"/>, which the sweeps already use
    /// to decide root identity, so the memo can never disagree with the exclusion logic about what "the same
    /// directory" means (issue #409 N4 notes the same unconditional case-insensitivity; harness worktree
    /// roots are lowercase hex, so a case-only collision is not reachable).
    /// </summary>
    private static StringComparer PathKeyComparer => StringComparer.OrdinalIgnoreCase;

    private static string? TryLinkTarget(string link)
    {
        try { return new DirectoryInfo(link).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
