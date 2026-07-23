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

    // ── ResolveForRun: fresh allocate / resume restore / mismatch / fallback ──────────────────

    [Fact]
    public void ResolveForRun_Fresh_AllocatesJunctionAndReturnsRecordRoot()
    {
        string realRoot = Path.Combine(_root, "realroot");
        string baseDir = Path.Combine(_root, "base");

        WorktreeJunction.JunctionResolution r =
            WorktreeJunction.ResolveForRun(realRoot, recordedRoot: null, baseDir, TextWriter.Null);

        Assert.Null(r.RestoreError);
        if (!OperatingSystem.IsWindows())
        {
            // Non-Windows: junctions are a no-op; the effective root is the real root, nothing recorded.
            Assert.Equal(realRoot, r.EffectiveRoot);
            Assert.Null(r.RecordRoot);
            return;
        }

        Assert.Equal(Track(r.EffectiveRoot), r.RecordRoot);          // the chosen link is recorded for resume
        Assert.Equal(".a", Path.GetFileName(r.EffectiveRoot));
        Assert.True(WorktreeJunction.IsJunctionTo(r.EffectiveRoot, realRoot));
    }

    [Fact]
    public void ResolveForRun_Resume_ReusesMatchingJunction_NoReRecord()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string realRoot = Path.Combine(_root, "realroot");
        string link = Path.Combine(_root, "base", ".a");
        Assert.True(TrackCreate(link, realRoot));

        WorktreeJunction.JunctionResolution r =
            WorktreeJunction.ResolveForRun(realRoot, recordedRoot: link, Path.Combine(_root, "base"), TextWriter.Null);

        Assert.Null(r.RestoreError);
        Assert.Equal(link, r.EffectiveRoot);
        Assert.Null(r.RecordRoot); // already recorded — nothing to re-persist
    }

    [Fact]
    public void ResolveForRun_Resume_MissingJunction_Recreates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string realRoot = Path.Combine(_root, "realroot");
        string link = Track(Path.Combine(_root, "base", ".a"))!; // recorded but NOT on disk

        WorktreeJunction.JunctionResolution r =
            WorktreeJunction.ResolveForRun(realRoot, recordedRoot: link, Path.Combine(_root, "base"), TextWriter.Null);

        Assert.Null(r.RestoreError);
        Assert.Equal(link, r.EffectiveRoot);
        Assert.True(WorktreeJunction.IsJunctionTo(link, realRoot));
    }

    [Fact]
    public void ResolveForRun_Resume_MismatchedJunction_HardFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string realRoot = Path.Combine(_root, "realroot");
        string otherRoot = Path.Combine(_root, "otherroot");
        string link = Path.Combine(_root, "base", ".a");
        Assert.True(TrackCreate(link, otherRoot)); // the recorded link now points ELSEWHERE (a concurrent run)

        WorktreeJunction.JunctionResolution r =
            WorktreeJunction.ResolveForRun(realRoot, recordedRoot: link, Path.Combine(_root, "base"), TextWriter.Null);

        Assert.NotNull(r.RestoreError);
        Assert.Contains("points elsewhere", r.RestoreError!, StringComparison.Ordinal);
        Assert.Contains(link, r.RestoreError!, StringComparison.Ordinal);
        Assert.Equal(realRoot, r.EffectiveRoot); // no junction adopted
    }

    [Fact]
    public void ResolveForRun_Fresh_AllNamesTaken_FallsBackToRealRoot_NoRecord()
    {
        // Graceful fallback: when a junction cannot be allocated (here: all 26 names are held by real dirs,
        // simulating exhaustion / a locked-down root), the effective root is the REAL root and nothing is
        // recorded — the run-config path is unchanged and the run proceeds (GR2038 backstop). Cross-OS: on
        // non-Windows ResolveForRun short-circuits to the same real-root result.
        string realRoot = Path.Combine(_root, "realroot");
        string baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseDir);
        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            Directory.CreateDirectory(Path.Combine(baseDir, leaf));
        }

        WorktreeJunction.JunctionResolution r =
            WorktreeJunction.ResolveForRun(realRoot, recordedRoot: null, baseDir, TextWriter.Null);

        Assert.Null(r.RestoreError);
        Assert.Equal(realRoot, r.EffectiveRoot);
        Assert.Null(r.RecordRoot);
    }

    // ── journal field: the sole durable record (git canonicalizes the junction away) ─────────

    [Fact]
    public void JournalDocument_WorktreeJunctionRoot_RoundTrips_AndIsOmittedWhenNull()
    {
        var withRoot = new JournalDocument
        {
            RunId = "r1", PlanHash = "sha256:abc", WorktreeJunctionRoot = @"C:\.a"
        };
        string json = JsonSerializer.Serialize(withRoot, JournalJson.Options);
        Assert.Contains("worktreeJunctionRoot", json, StringComparison.Ordinal);
        JournalDocument back = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        Assert.Equal(@"C:\.a", back.WorktreeJunctionRoot);

        // Additive/backward-compatible: absent (not null noise) when unset.
        var without = new JournalDocument { RunId = "r2", PlanHash = "sha256:def" };
        Assert.DoesNotContain(
            "worktreeJunctionRoot", JsonSerializer.Serialize(without, JournalJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void RunJournal_RecordWorktreeJunctionRoot_PersistsAcrossReload()
    {
        string planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks =
            [
                new TaskNode
                {
                    Id = "01-task",
                    Directory = taskDir,
                    Description = "t",
                    Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
                    Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "x", Kind = ActionKind.Script }]
                }
            ]
        };

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        Assert.Null(journal.Document.WorktreeJunctionRoot); // absent on a fresh run

        journal.RecordWorktreeJunctionRoot(@"C:\.a");

        RunJournal reloaded = RunJournal.LoadOrCreate(plan);
        Assert.Equal(@"C:\.a", reloaded.Document.WorktreeJunctionRoot); // survived the reload (resume record)
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
