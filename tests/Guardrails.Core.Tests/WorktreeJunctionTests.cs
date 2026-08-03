using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #383 (Windows short-junction worktree root, layered on the #383 short default + env/config
/// override + GR2038). The harness roots segment worktrees under a short directory JUNCTION
/// (<c>&lt;drive&gt;:\.a</c>..<c>\.z</c> → the real worktree root) so each task's child-process cwd — and
/// thus <c>dotnet test</c>'s built exe path — stays clear of Windows MAX_PATH (260).
/// <para>
/// These tests exercise the allocation/naming logic, the create→use→teardown cycle, the LINK-ONLY teardown
/// (the data-loss guard), the graceful fallback, and resume restore/mismatch — all against UNIQUE TEMP link
/// targets (never the real <c>C:\</c> root), with the Windows-only junction-creation assertions gated on
/// <see cref="OperatingSystem.IsWindows"/>. Cleanup removes junction LINKS first, then deletes the temp tree
/// WITHOUT following any reparse point.
/// </para>
/// </summary>
public sealed class WorktreeJunctionTests : IDisposable
{
    private readonly string _root;

    public WorktreeJunctionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-junc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── naming logic ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandidateLeaves_AreDotAThroughDotZ_TwoCharLeafShape()
    {
        IReadOnlyList<string> leaves = WorktreeJunction.CandidateLeaves;

        Assert.Equal(26, leaves.Count);
        Assert.Equal(".a", leaves[0]);
        Assert.Equal(".z", leaves[25]);
        Assert.All(leaves, leaf => Assert.Matches("^\\.[a-z]$", leaf));
    }

    [Fact]
    public void DriveRootPlusLeaf_IsTheFiveCharShape()
    {
        // The documented shape: <drive>:\.a is 5 chars. Windows-only (backslash is a separator there).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine("C:\\", WorktreeJunction.CandidateLeaves[0]);
        Assert.Equal("C:\\.a", path);
        Assert.Equal(5, path.Length);
    }

    // ── allocation: first-free / skip / reuse / exhaustion ───────────────────────────────────

    [Fact]
    public void AllocateUnder_AllNamesTakenByRealDirs_ReturnsNull()
    {
        // Cross-OS: every candidate name is a REAL directory (not our junction), so allocation skips all
        // 26 and returns null — the "all names taken (rare, many concurrent runs)" exhaustion path. No
        // junction is created here, so this needs no Windows gate.
        string baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseDir);
        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            Directory.CreateDirectory(Path.Combine(baseDir, leaf));
        }

        Assert.Null(WorktreeJunction.AllocateUnder(baseDir, Path.Combine(_root, "target"), TextWriter.Null));
    }

    [Fact]
    public void AllocateUnder_FreshBaseDir_CreatesFirstFreeDotA()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string baseDir = Path.Combine(_root, "base");
        string target = Path.Combine(_root, "target");

        string? chosen = Track(WorktreeJunction.AllocateUnder(baseDir, target, TextWriter.Null));

        Assert.NotNull(chosen);
        Assert.Equal(".a", Path.GetFileName(chosen));
        Assert.True(WorktreeJunction.IsJunctionTo(chosen!, target));
    }

    [Fact]
    public void AllocateUnder_DotATakenByOtherTarget_AllocatesDotB()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string baseDir = Path.Combine(_root, "base");
        string otherTarget = Path.Combine(_root, "other");
        string myTarget = Path.Combine(_root, "mine");

        // .a already junctions to a DIFFERENT target → allocation must skip it and take .b.
        Assert.True(TrackCreate(Path.Combine(baseDir, ".a"), otherTarget));

        string? chosen = Track(WorktreeJunction.AllocateUnder(baseDir, myTarget, TextWriter.Null));

        Assert.NotNull(chosen);
        Assert.Equal(".b", Path.GetFileName(chosen));
        Assert.True(WorktreeJunction.IsJunctionTo(chosen!, myTarget));
    }

    [Fact]
    public void AllocateUnder_ExistingJunctionToSameTarget_IsReused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string baseDir = Path.Combine(_root, "base");
        string target = Path.Combine(_root, "target");
        string dotA = Path.Combine(baseDir, ".a");
        Assert.True(TrackCreate(dotA, target));

        // Idempotent re-entry / a same-plan leftover: an existing junction to OUR target is reused, not skipped.
        string? chosen = WorktreeJunction.AllocateUnder(baseDir, target, TextWriter.Null);

        Assert.Equal(dotA, chosen);
    }

    // ── teardown: LINK ONLY (the data-loss guard) ────────────────────────────────────────────

    [Fact]
    public void RemoveJunctionLink_RemovesLinkOnly_LeavesTargetAndSentinelIntact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The data-loss guard: teardown deletes the reparse-point LINK, NEVER the target's contents.
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "KEEP-ME");

        string link = Path.Combine(_root, "base", ".a");
        Assert.True(WorktreeJunction.TryCreateJunction(link, target));
        Assert.True(File.Exists(Path.Combine(link, "sentinel.txt"))); // visible THROUGH the junction

        WorktreeJunction.RemoveJunctionLink(link);

        Assert.False(Directory.Exists(link));   // the link is gone
        Assert.True(Directory.Exists(target));  // the target survives
        Assert.True(File.Exists(sentinel));     // ...and so does its content
        Assert.Equal("KEEP-ME", File.ReadAllText(sentinel));
    }

    [Fact]
    public void RemoveJunctionLink_OnRealDirectory_IsNoOp()
    {
        // A path that is NOT a reparse point (a real dir) is never touched — so the guard can never
        // recurse into or delete a real tree. Cross-OS.
        string realDir = Path.Combine(_root, "not-a-junction");
        Directory.CreateDirectory(realDir);
        string sentinel = Path.Combine(realDir, "keep.txt");
        File.WriteAllText(sentinel, "x");

        WorktreeJunction.RemoveJunctionLink(realDir);

        Assert.True(Directory.Exists(realDir));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void CreateUseTeardownCycle_ThroughTempLinkPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The full allocation → use → teardown cycle against a temp link path (never C:\).
        string baseDir = Path.Combine(_root, "base");
        string target = Path.Combine(_root, "target");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(target).FullName, "file.txt"), "data");

        string? link = WorktreeJunction.AllocateUnder(baseDir, target, TextWriter.Null);
        Assert.NotNull(link);
        Assert.True(WorktreeJunction.IsReparsePoint(link!));
        Assert.Equal("data", File.ReadAllText(Path.Combine(link!, "file.txt"))); // use it

        WorktreeJunction.RemoveJunctionLink(link!);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "file.txt"))); // target intact
    }

    // ── ResolveForRun: FRESH allocate / skip-foreign / fallback (#419 — no resume restore) ────────

    [Fact]
    public void ResolveForRun_Fresh_AllocatesJunction()
    {
        string realRoot = Path.Combine(_root, "realroot");
        string baseDir = Path.Combine(_root, "base");

        string effective = WorktreeJunction.ResolveForRun(realRoot, baseDir, TextWriter.Null);

        if (!OperatingSystem.IsWindows())
        {
            // Non-Windows: junctions are a no-op; the effective root is the real root.
            Assert.Equal(realRoot, effective);
            return;
        }

        Track(effective);
        Assert.Equal(".a", Path.GetFileName(effective));
        Assert.True(WorktreeJunction.IsJunctionTo(effective, realRoot));
    }

    [Fact]
    public void ResolveForRun_Fresh_ForeignDotA_AllocatesDotB()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // #419: EVERY run allocates fresh — a foreign .a (a concurrent run's link to another target) is
        // skipped to the next free letter. This first-free allocation is the collision-safety that REPLACES
        // the removed same-letter-restore hard-fail: no resume can ever be dropped into another run's tree.
        string realRoot = Path.Combine(_root, "realroot");
        string otherRoot = Path.Combine(_root, "otherroot");
        string baseDir = Path.Combine(_root, "base");
        string foreignA = Path.Combine(baseDir, ".a");
        Track(foreignA);
        Assert.True(WorktreeJunction.TryCreateJunction(foreignA, otherRoot)); // .a belongs to someone else

        string effective = WorktreeJunction.ResolveForRun(realRoot, baseDir, TextWriter.Null);
        Track(effective);

        Assert.Equal(".b", Path.GetFileName(effective));
        Assert.True(WorktreeJunction.IsJunctionTo(effective, realRoot));
        Assert.True(WorktreeJunction.IsJunctionTo(foreignA, otherRoot)); // the foreign link is untouched
    }

    [Fact]
    public void ResolveForRun_Fresh_AllNamesTaken_FallsBackToRealRoot()
    {
        // Graceful fallback: when a junction cannot be allocated (here: all 26 names are held by real dirs,
        // simulating exhaustion / a locked-down root), the effective root is the REAL root — the run-config
        // path is unchanged and the run proceeds (GR2038 backstop). Cross-OS: on non-Windows ResolveForRun
        // short-circuits to the same real-root result.
        string realRoot = Path.Combine(_root, "realroot");
        string baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseDir);
        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            Directory.CreateDirectory(Path.Combine(baseDir, leaf));
        }

        Assert.Equal(realRoot, WorktreeJunction.ResolveForRun(realRoot, baseDir, TextWriter.Null));
    }

    // ── #407 C: lazy / predictive junction creation ──────────────────────────────────────────

    [Fact]
    public void RealRootNeedsJunction_ShortRootShallowTasks_False()
    {
        // A short real root with comfortable headroom (base + reserve + margin ≤ 260) for every task → the
        // junction is unneeded churn; skip it. Pure path-length maths, cross-OS.
        Assert.False(WorktreeJunction.RealRootNeedsJunction(@"C:\gw\abc12345", "abcd1234", ["01-init", "02-build"]));
    }

    [Fact]
    public void RealRootNeedsJunction_LongRootDeepTask_True()
    {
        // The #383 long-root + deep wave-qualified task shape → a segment path is at risk → CREATE.
        const string longRoot =
            @"C:\Users\SomeDeveloper\AppData\Local\Temp\guardrails-worktrees\autonomous-mode-impl-a1b2c3d4";
        const string deepTask = "wave-03-classify-and-escalate/17-wire-classifier-into-executor";

        Assert.True(WorktreeJunction.RealRootNeedsJunction(longRoot, "abcd1234", [deepTask]));
    }

    [Fact]
    public void RealRootNeedsJunction_EmptyTaskSet_False()
    {
        // No segment paths ⇒ no MAX_PATH risk ⇒ no junction (a partially-authored waved plan self-corrects on
        // the resume that authors its deeper wave).
        Assert.False(WorktreeJunction.RealRootNeedsJunction(@"C:\any\root\at\all", "abcd1234", []));
    }

    [Fact]
    public void RealRootNeedsJunction_ConservativeMarginBand_CreatesEvenWhenGr2038WouldPass()
    {
        // The err-toward-CREATING margin: pick a root whose base lands EXACTLY on GR2038's pass ceiling
        // (base + reserve == 260, so the real root would PASS GR2038) — yet it is inside the conservative
        // margin band (base + reserve + margin > 260), so C STILL creates the junction. A false skip is a
        // MAX_PATH halt, so the extra margin buys headroom the bare GR2038 check does not.
        const string runId = "abcd1234";
        const string task = "01-x";
        int suffix = Path.Combine("X", runId, task, "attempt-1").Length - 1; // "/<runId>/<task>/attempt-1"
        int targetBase = WorktreePathPreflight.MaxPathLimit - WorktreePathPreflight.BuildOutputReserve; // GR2038 ceiling
        string root = new('a', targetBase - suffix);
        int baseLength = Path.Combine(root, runId, task, "attempt-1").Length;

        Assert.Equal(targetBase, baseLength);
        Assert.True(baseLength + WorktreePathPreflight.BuildOutputReserve <= WorktreePathPreflight.MaxPathLimit,
            "precondition: the real root would PASS the bare GR2038 check");
        Assert.True(
            baseLength + WorktreePathPreflight.BuildOutputReserve + WorktreeJunction.JunctionSkipMargin
            > WorktreePathPreflight.MaxPathLimit,
            "precondition: yet it is inside the conservative margin band");

        Assert.True(WorktreeJunction.RealRootNeedsJunction(root, runId, [task]));
    }

    [Fact]
    public void ResolveForRun_Fresh_ShortRootWithHeadroom_SkipsJunction()
    {
        // #407 C: a fresh run whose real root fits every task with margin creates NO junction — the effective
        // root IS the real root. Cross-OS: non-Windows short-circuits to the same real-root result; Windows
        // takes the lazy-skip branch BEFORE AllocateUnder (nothing is created, so no Windows gate is needed).
        const string realRoot = @"C:\gw\abc12345";
        string baseDir = Path.Combine(_root, "base");

        string effective = WorktreeJunction.ResolveForRun(
            realRoot, baseDir, TextWriter.Null, runId: "abcd1234", taskIds: ["01-a", "02-b"]);

        Assert.Equal(realRoot, effective);
        Assert.False(Directory.Exists(Path.Combine(baseDir, ".a"))); // no junction allocated
    }

    [Fact]
    public void ResolveForRun_Fresh_LongRootWithoutHeadroom_CreatesJunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A real root too long for a deep task → the lazy predicate CREATES a fresh junction (allocates .a).
        string realRoot = Path.Combine(_root, "realroot");
        string baseDir = Path.Combine(_root, "base");
        const string deepTask = "wave-03-classify-and-escalate/17-wire-classifier-into-executor";

        // Self-validating precondition: this shape genuinely needs a junction (independent of temp length).
        Assert.True(WorktreeJunction.RealRootNeedsJunction(realRoot, "abcd1234", [deepTask]));

        string effective = WorktreeJunction.ResolveForRun(
            realRoot, baseDir, TextWriter.Null, runId: "abcd1234", taskIds: [deepTask]);
        Track(effective); // register the created link for LINK-FIRST cleanup

        Assert.Equal(".a", Path.GetFileName(effective));
        Assert.True(WorktreeJunction.IsJunctionTo(effective, realRoot));
    }

    // ── RemoveJunctionsTo: --fresh tears down THIS plan's link with NO journal record (#419) ───────

    [Fact]
    public void RemoveJunctionsTo_RemovesOnlyLinksToTarget_LinkOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // #419: the junction is no longer journaled, so --fresh sweeps the drive-root candidates for a
        // junction pointing at THIS plan's real root and removes it (link-only), leaving a link to a DIFFERENT
        // target (a concurrent run / another plan) untouched.
        string baseDir = Path.Combine(_root, "drive");
        string target = Path.Combine(_root, "realroot");
        string other = Path.Combine(_root, "otherroot");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(target).FullName, "keep.txt"), "KEEP");

        string toTargetA = Path.Combine(baseDir, ".a"); Track(toTargetA);
        string toTargetB = Path.Combine(baseDir, ".b"); Track(toTargetB);
        string toOther = Path.Combine(baseDir, ".c"); Track(toOther);
        Assert.True(WorktreeJunction.TryCreateJunction(toTargetA, target));
        Assert.True(WorktreeJunction.TryCreateJunction(toTargetB, target));
        Assert.True(WorktreeJunction.TryCreateJunction(toOther, other));

        WorktreeJunction.RemoveJunctionsTo(baseDir, target);

        Assert.False(Directory.Exists(toTargetA)); // links to THIS target → removed
        Assert.False(Directory.Exists(toTargetB));
        Assert.True(WorktreeJunction.IsReparsePoint(toOther)); // a link to another target → untouched
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(target, "keep.txt"))); // link-only, target intact
    }

    // ── journal back-compat: an OLD run.json carrying worktreeJunctionRoot deserializes clean (#419) ──

    [Fact]
    public void OldJournalWithWorktreeJunctionRoot_DeserializesClean_FieldIgnored()
    {
        // #419 removed the journal field. JournalJson sets no JsonUnmappedMemberHandling.Disallow (default =
        // Skip), so an old run.json still carrying the key deserializes clean (the unknown member is skipped)
        // and resumes with no migration — the whole point of the decouple's back-compat guarantee.
        const string oldJournal =
            """
            {
              "version": 1,
              "runId": "r1",
              "planHash": "sha256:abc",
              "worktreeJunctionRoot": "C:\\.a",
              "tasks": {}
            }
            """;

        JournalDocument doc = JsonSerializer.Deserialize<JournalDocument>(oldJournal, JournalJson.Options)!;

        Assert.Equal("r1", doc.RunId);
        Assert.Equal("sha256:abc", doc.PlanHash);

        // Round-trips WITHOUT re-emitting the retired key.
        Assert.DoesNotContain(
            "worktreeJunctionRoot", JsonSerializer.Serialize(doc, JournalJson.Options), StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private readonly List<string> _links = [];

    /// <summary>Register a created junction link for LINK-FIRST cleanup; returns the same value.</summary>
    private string? Track(string? link)
    {
        if (link is not null)
        {
            _links.Add(link);
        }

        return link;
    }

    /// <summary>Create a junction and register it for cleanup; returns whether creation succeeded.</summary>
    private bool TrackCreate(string link, string target)
    {
        Track(link);
        return WorktreeJunction.TryCreateJunction(link, target);
    }

    public void Dispose()
    {
        // Remove tracked junction LINKS first (link-only) so the tree delete below never follows a reparse
        // point into a target.
        foreach (string link in _links)
        {
            WorktreeJunction.RemoveJunctionLink(link);
        }

        SafeDeleteTree(_root);
    }

    /// <summary>Recursively delete a temp tree, treating any reparse point as link-only (never followed).</summary>
    private static void SafeDeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                {
                    try { Directory.Delete(sub, recursive: false); } catch { /* best-effort */ }
                }
                else
                {
                    SafeDeleteTree(sub);
                }
            }

            foreach (string file in Directory.GetFiles(dir))
            {
                try { File.Delete(file); } catch { /* best-effort */ }
            }

            Directory.Delete(dir, recursive: false);
        }
        catch
        {
            // Best-effort test cleanup — a leftover temp dir must never fail the test.
        }
    }
}
