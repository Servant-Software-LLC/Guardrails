using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #407 (junction/root LIFECYCLE) — the terminal-completion cleanup (A,
/// <see cref="WorktreeReclaim.ShouldReclaimOnCompletion"/>) predicate and the startup-GC (B) sweep logic
/// (<see cref="WorktreeReclaim.TreeIsStale"/>, <see cref="WorktreeReclaim.SweepRoots"/>,
/// <see cref="WorktreeReclaim.SweepJunctions"/>).
/// <para>
/// The SAFETY-CRITICAL property — B NEVER reclaims an active/current run's tree — is pinned hard here:
/// the current run's own root + junction are excluded, and a FRESH tree (a proxy for a possibly-active run)
/// is always kept. The sweeps are tested against CONTROLLED temp parents / base dirs (never the real
/// <c>gr-wt</c> dir or the real drive-root <c>.a</c>..<c>.z</c>); the Windows-only junction create/remove
/// assertions are gated on <see cref="OperatingSystem.IsWindows"/>.
/// </para>
/// </summary>
public sealed class WorktreeReclaimTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = [];

    public WorktreeReclaimTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-reclaim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── A — ShouldReclaimOnCompletion (terminal vs resumable) ─────────────────────────────────

    [Fact]
    public void ShouldReclaim_WhollyGreenDelivered_True()
    {
        // Green + terminal gate passed + delivered (ff) → non-resumable → reclaim.
        Assert.True(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(MergeOnSuccessResult.FastForwarded), terminalGatePassed: true));
        Assert.True(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(MergeOnSuccessResult.Merged), terminalGatePassed: true));
        // Green + no delivery outcome at all (serial / no mergeOnSuccess) → reclaim.
        Assert.True(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(mergeOutcome: null), terminalGatePassed: true));
    }

    [Fact]
    public void ShouldReclaim_GreenButTerminalGateFailed_False()
    {
        // The DAG drained green but the terminal gate failed → the user fixes + re-runs → KEEP.
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(mergeOutcome: null), terminalGatePassed: false));
    }

    [Fact]
    public void ShouldReclaim_GreenButDeliveryHalted_False()
    {
        // Green but delivery HALTED (conflict / dirty tree / hook-rejected) → the user must act → KEEP.
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(MergeOnSuccessResult.Conflict), terminalGatePassed: true));
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(MergeOnSuccessResult.DirtyWorkingTree), terminalGatePassed: true));
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            GreenReport(MergeOnSuccessResult.HookRejected), terminalGatePassed: true));
    }

    [Fact]
    public void ShouldReclaim_WhollyGreenButUndelivered_False()
    {
        // Opt-out: the verified work sits on the plan branch for the user to inspect/deliver → KEEP.
        RunReport undelivered = GreenReport(mergeOutcome: null) with { WhollyGreenButUndelivered = true };
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(undelivered, terminalGatePassed: true));
    }

    [Fact]
    public void ShouldReclaim_NonGreenOutcomes_False()
    {
        // needs-human / blocked / failed are all RESUMABLE → KEEP.
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            NonGreenReport(TaskOutcome.NeedsHuman), terminalGatePassed: true));
        Assert.False(WorktreeReclaim.ShouldReclaimOnCompletion(
            NonGreenReport(TaskOutcome.Blocked), terminalGatePassed: true));
    }

    // ── B — TreeIsStale (the reclaimable predicate's staleness half) ──────────────────────────

    [Fact]
    public void TreeIsStale_AllEntriesOld_True()
    {
        string tree = MakeTree("stale", filesPerDir: 2, subdirs: 2);
        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(3);
        SetTreeMtime(tree, old);

        Assert.True(WorktreeReclaim.TreeIsStale(tree, DateTime.UtcNow - TimeSpan.FromHours(24)));
    }

    [Fact]
    public void TreeIsStale_OneFreshNestedFile_False()
    {
        // A single fresh entry ANYWHERE in the tree ⇒ NOT stale (an active run touches deep build outputs).
        string tree = MakeTree("fresh", filesPerDir: 2, subdirs: 2);
        SetTreeMtime(tree, DateTime.UtcNow - TimeSpan.FromDays(3));
        // Touch the deepest nested file to "now".
        string deepFile = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Last();
        File.SetLastWriteTimeUtc(deepFile, DateTime.UtcNow);

        Assert.False(WorktreeReclaim.TreeIsStale(tree, DateTime.UtcNow - TimeSpan.FromHours(24)));
    }

    [Fact]
    public void TreeIsStale_FreshRootDir_False()
    {
        // The root dir itself freshly touched (a new <runId> segment just created) ⇒ NOT stale.
        string tree = MakeTree("fresh-root", filesPerDir: 1, subdirs: 1);
        SetTreeMtime(tree, DateTime.UtcNow - TimeSpan.FromDays(3));
        Directory.SetLastWriteTimeUtc(tree, DateTime.UtcNow);

        Assert.False(WorktreeReclaim.TreeIsStale(tree, DateTime.UtcNow - TimeSpan.FromHours(24)));
    }

    [Fact]
    public void TreeIsStale_MissingRoot_False_ErrsTowardKeeping()
    {
        // Cannot scan (the dir does not exist) ⇒ treated as NOT stale (KEEP) — err toward keeping.
        Assert.False(WorktreeReclaim.TreeIsStale(Path.Combine(_root, "does-not-exist"),
            DateTime.UtcNow - TimeSpan.FromHours(24)));
    }

    // ── B — SweepRoots (cross-OS; a non-git workspace makes RemoveWorktreeRoot delete-only) ────

    [Fact]
    public void SweepRoots_ReclaimsStale_KeepsFresh_AndNeverTheCurrentRun()
    {
        string parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(parent);

        string staleRoot = MakeChild(parent, "stale", DateTime.UtcNow - TimeSpan.FromDays(3));
        string freshRoot = MakeChild(parent, "fresh", DateTime.UtcNow);
        // The CURRENT run's root — OLD (a resume's root legitimately looks stale) — must NOT be reclaimed.
        string currentRoot = MakeChild(parent, "current", DateTime.UtcNow - TimeSpan.FromDays(3));

        string nonGitWorkspace = Path.Combine(_root, "workspace"); // not a repo → RemoveWorktreeRoot = delete-only
        Directory.CreateDirectory(nonGitWorkspace);

        WorktreeReclaim.SweepRoots(
            [parent], nonGitWorkspace, currentRoot, DateTime.UtcNow - TimeSpan.FromHours(24), TextWriter.Null);

        Assert.False(Directory.Exists(staleRoot));   // reclaimed
        Assert.True(Directory.Exists(freshRoot));    // active → kept
        Assert.True(Directory.Exists(currentRoot));  // THIS run → kept (the safety-critical exclusion)
    }

    // ── B — SweepJunctions (Windows: create/remove; the dangling + fresh-target + current cases) ─

    [Fact]
    public void SweepJunctions_DanglingReclaimed_FreshTargetKept_CurrentAndStaleHandled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string baseDir = Path.Combine(_root, "drive");

        // .a → a GONE target (dangling): reclaim.
        string goneTarget = Path.Combine(_root, "gone-target");
        string dangling = Track(Path.Combine(baseDir, ".a"));
        Assert.True(WorktreeJunction.TryCreateJunction(dangling, Directory.CreateDirectory(goneTarget).FullName));
        Directory.Delete(goneTarget, recursive: true); // now dangling

        // .b → a FRESH target (a possibly-active run): KEEP.
        string freshTarget = MakeChild(Path.Combine(_root, "roots"), "fresh-tgt", DateTime.UtcNow);
        string toFresh = Track(Path.Combine(baseDir, ".b"));
        Assert.True(WorktreeJunction.TryCreateJunction(toFresh, freshTarget));

        // .c → a STALE target: reclaim the LINK (the root sweep would delete the target itself).
        string staleTarget = MakeChild(Path.Combine(_root, "roots"), "stale-tgt", DateTime.UtcNow - TimeSpan.FromDays(3));
        string toStale = Track(Path.Combine(baseDir, ".c"));
        Assert.True(WorktreeJunction.TryCreateJunction(toStale, staleTarget));

        // .d → this RUN's own recorded junction, pointing at a STALE target: must NOT be reclaimed.
        string currentTarget = MakeChild(Path.Combine(_root, "roots"), "current-tgt", DateTime.UtcNow - TimeSpan.FromDays(3));
        string current = Track(Path.Combine(baseDir, ".d"));
        Assert.True(WorktreeJunction.TryCreateJunction(current, currentTarget));

        WorktreeReclaim.SweepJunctions(
            baseDir, currentJunctionRoot: current, DateTime.UtcNow - TimeSpan.FromHours(24), TextWriter.Null);

        Assert.False(Directory.Exists(dangling));            // dangling → reclaimed
        Assert.True(WorktreeJunction.IsReparsePoint(toFresh)); // fresh target → kept
        Assert.False(Directory.Exists(toStale));             // stale target → link reclaimed
        Assert.True(WorktreeJunction.IsReparsePoint(current)); // THIS run's junction → kept (safety-critical)

        // The link-only guarantee: reclaiming .c removed the LINK, never the (stale) target's contents.
        Assert.True(Directory.Exists(staleTarget));
        Assert.True(Directory.Exists(freshTarget)); // untouched
    }

    // ── #419 — the EXIT root-sweep is count-capped (never delays the visible exit) ────────────────
    // (Its keeps-own / keeps-fresh / keeps-live-locked / reclaims-stale semantics ARE SweepRoots, proven by
    //  the two tests above + SweepRoots_StaleButLiveLocked_IsKept_F1 below — ReclaimRootsOnExit delegates to
    //  SweepRoots with the same exclusions; only the count cap is new.)

    [Fact]
    public void SweepRoots_HonorsMaxReclaimsCap_LeavesTheRestForTheStartupGc()
    {
        // Five stale, reclaimable roots but a cap of 2 → exactly 2 reclaimed this exit; the rest wait for a
        // later run's startup GC. The cap is what keeps the exit sweep from delaying the visible exit (#419).
        string parent = Path.Combine(_root, "capped");
        Directory.CreateDirectory(parent);
        for (int i = 0; i < 5; i++)
        {
            MakeChild(parent, $"stale{i}", DateTime.UtcNow - TimeSpan.FromDays(3));
        }

        string nonGitWorkspace = Path.Combine(_root, "ws-cap");
        Directory.CreateDirectory(nonGitWorkspace);

        WorktreeReclaim.SweepRoots(
            [parent], nonGitWorkspace, currentRealRoot: null,
            DateTime.UtcNow - TimeSpan.FromHours(24), TextWriter.Null, maxReclaims: 2);

        Assert.Equal(3, Directory.GetDirectories(parent).Length); // 5 - 2 = 3 left for the startup GC
    }

    // ── B — the #407 Finding-1 live-process LOCK (idle-run protection + PID-reuse guard) ─────────

    [Fact]
    public void IsLockedByLiveProcess_LiveNone_DeadPid_MismatchedTicks()
    {
        string live = MakeTree("lock-live", filesPerDir: 1, subdirs: 0);
        WorktreeReclaim.WriteRunLock(live);
        Assert.True(WorktreeReclaim.IsLockedByLiveProcess(live));    // this process's own lock → live

        string none = MakeTree("lock-none", filesPerDir: 1, subdirs: 0);
        Assert.False(WorktreeReclaim.IsLockedByLiveProcess(none));   // no lock file → no live signal

        string dead = MakeTree("lock-dead", filesPerDir: 1, subdirs: 0);
        File.WriteAllText(Path.Combine(dead, WorktreeReclaim.RunLockFileName), "999999999\n0\n");
        Assert.False(WorktreeReclaim.IsLockedByLiveProcess(dead));   // no such pid → not live

        string reuse = MakeTree("lock-reuse", filesPerDir: 1, subdirs: 0);
        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        File.WriteAllText(Path.Combine(reuse, WorktreeReclaim.RunLockFileName), $"{pid}\n123\n");
        Assert.False(WorktreeReclaim.IsLockedByLiveProcess(reuse));  // live pid but WRONG start-ticks (PID reuse) → not ours
    }

    [Fact]
    public void SweepRoots_StaleButLiveLocked_IsKept_F1()
    {
        // A STALE tree carrying THIS (live) process's lock must be KEPT — the live-but-idle guard (F1).
        string parent = Path.Combine(_root, "parent-locked");
        string lockedStale = MakeChild(parent, "locked-stale", DateTime.UtcNow - TimeSpan.FromDays(3));
        WorktreeReclaim.WriteRunLock(lockedStale);                            // lock names this live process
        SetTreeMtime(lockedStale, DateTime.UtcNow - TimeSpan.FromDays(3));    // re-age incl. the lock file → tree is stale
        // Control: same parent, stale, NO lock → reclaimed.
        string unlockedStale = MakeChild(parent, "unlocked-stale", DateTime.UtcNow - TimeSpan.FromDays(3));

        string nonGitWorkspace = Path.Combine(_root, "ws-locked");
        Directory.CreateDirectory(nonGitWorkspace);

        WorktreeReclaim.SweepRoots(
            [parent], nonGitWorkspace, currentRealRoot: null, DateTime.UtcNow - TimeSpan.FromHours(24), TextWriter.Null);

        Assert.True(Directory.Exists(lockedStale));     // stale but LIVE-LOCKED → kept
        Assert.False(Directory.Exists(unlockedStale));  // stale + no lock → reclaimed
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static RunReport GreenReport(MergeOnSuccessResult? mergeOutcome) => new()
    {
        Tasks =
        [
            new TaskResult { TaskId = "01-a", Outcome = TaskOutcome.Succeeded, Summary = "ok" }
        ],
        MergeOnSuccessOutcome = mergeOutcome
    };

    private static RunReport NonGreenReport(TaskOutcome outcome) => new()
    {
        Tasks =
        [
            new TaskResult { TaskId = "01-a", Outcome = outcome, Summary = "not green" }
        ]
    };

    /// <summary>Create <paramref name="name"/> under <see cref="_root"/> with a small tree of files/subdirs; returns its path.</summary>
    private string MakeTree(string name, int filesPerDir, int subdirs)
    {
        string tree = Path.Combine(_root, name);
        Directory.CreateDirectory(tree);
        for (int f = 0; f < filesPerDir; f++)
        {
            File.WriteAllText(Path.Combine(tree, $"f{f}.txt"), "x");
        }

        for (int s = 0; s < subdirs; s++)
        {
            string sub = Path.Combine(tree, $"sub{s}");
            Directory.CreateDirectory(sub);
            for (int f = 0; f < filesPerDir; f++)
            {
                File.WriteAllText(Path.Combine(sub, $"g{f}.txt"), "y");
            }
        }

        return tree;
    }

    /// <summary>Create a child root under <paramref name="parent"/> with one file, then stamp the WHOLE tree to <paramref name="mtimeUtc"/>.</summary>
    private static string MakeChild(string parent, string name, DateTime mtimeUtc)
    {
        Directory.CreateDirectory(parent);
        string child = Path.Combine(parent, name);
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "marker.txt"), "m");
        SetTreeMtime(child, mtimeUtc);
        return child;
    }

    /// <summary>Stamp the root and every descendant (dirs + files) to <paramref name="mtimeUtc"/> — descendants first, root last.</summary>
    private static void SetTreeMtime(string root, DateTime mtimeUtc)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            // Directories need Directory.SetLastWriteTimeUtc — File.SetLastWriteTimeUtc throws on a dir path
            // (silently swallowed before), leaving nested subdirs fresh so TreeIsStale saw them as not-stale.
            try
            {
                if (Directory.Exists(entry)) { Directory.SetLastWriteTimeUtc(entry, mtimeUtc); }
                else { File.SetLastWriteTimeUtc(entry, mtimeUtc); }
            }
            catch { /* best-effort */ }
        }

        Directory.SetLastWriteTimeUtc(root, mtimeUtc);
    }

    private string Track(string link)
    {
        _links.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string link in _links)
        {
            WorktreeJunction.RemoveJunctionLink(link); // link-only, so the tree delete never follows a reparse point
        }

        try { Io.SafeDelete.DeleteDirectory(_root); } catch { /* best-effort temp cleanup */ }
    }
}
