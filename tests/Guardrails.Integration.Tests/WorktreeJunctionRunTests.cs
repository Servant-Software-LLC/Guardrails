using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #419 — the PROCESS-SCOPED junction, proven END-TO-END through the REAL <c>run</c> command
/// (#120 composition-root discipline). A worktree-mode run that ends in ANY state (green-delivered /
/// needs-human) leaves NO junction behind AND never journals one — the decouple + the release-on-exit
/// lifetime.
/// <para>
/// The junction is created only when the real root is long enough (#407 C lazy predicate) — usually NOT on a
/// short CI temp — so the "no junction survives" assertion is Windows-gated and, when no junction was created,
/// holds vacuously; the CROSS-OS proof is that <c>run.json</c> never carries <c>worktreeJunctionRoot</c>. When
/// a junction IS created (a long temp), the run RELEASES it on exit — so scanning the drive-root <c>.a</c>..
/// <c>.z</c> for a link to THIS run's real root and asserting none is the faithful end-to-end release proof,
/// and it can never pollute the drive because the release is the feature under test.
/// </para>
/// </summary>
public sealed class WorktreeJunctionRunTests
{
    [Fact]
    public async Task GreenRun_LeavesNoJunction_AndNeverJournalsOne()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: false);

        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(0, exit);
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task NeedsHumanRun_LeavesNoJunction_AndNeverJournalsOne()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: true);

        // defaultRetries 0 + a guardrail that always fails → needs-human (exit 2). The link is RELEASED even
        // for this RESUMABLE outcome (#419), while the worktree ROOT is KEPT for the resume.
        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(2, exit);
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task LongTaskId_ForcesAJunction_ThatIsCreatedThenReleasedOnExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // junctions are Windows-only
        }

        // A deliberately LONG task id makes the #407 C lazy predicate FORCE a real junction, so this
        // exercises the FULL RunCommand → PrepareWorktreeJunction → WorktreeJunctionLifetime → release-on-exit
        // wiring on any Windows machine (a shallow-task run usually skips the junction on a short temp). The
        // captured log proves a junction was BOTH allocated AND released, and nothing is left behind.
        using var repo = new TempGitRepo();
        string planDir = CreatePlanWithLongTaskId(repo.RepoPath);

        (int exit, string output) =
            await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(0, exit);
        Assert.Contains("worktree junction:", output, StringComparison.Ordinal);    // a junction WAS allocated
        Assert.Contains("released on exit", output, StringComparison.Ordinal);      // ...and released (#419)
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task Resume_WithOldJournalCarryingWorktreeJunctionRoot_ResumesClean()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: false);

        (int firstExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(0, firstExit);

        // Simulate a run.json written by a PRE-#419 binary: inject the retired worktreeJunctionRoot key.
        string journalPath = Path.Combine(planDir, "state", "run.json");
        string journal = await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken);
        journal = journal.TrimEnd().TrimEnd('}') + $",\n  \"worktreeJunctionRoot\": \"{JunctionLiteral()}\"\n}}";
        await File.WriteAllTextAsync(journalPath, journal, TestContext.Current.CancellationToken);

        // The unknown member is skipped on load (no JsonUnmappedMemberHandling.Disallow) → the resume drains
        // clean (idempotent green), never crashing on the retired field.
        (int resumeExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(0, resumeExit);
        AssertNoJournaledJunction(planDir);
    }

    /// <summary>
    /// Issue #419 WEAK-2 — the DELIVERED-green cleanup-A-then-Dispose-no-op path, END-TO-END with a REAL forced
    /// junction. The other e2e cases all pass <c>--no-merge-on-success</c>, routing through
    /// <see cref="Guardrails.Core.Execution.WorktreeReclaim.ShouldReclaimOnCompletion"/> == false, so the LIFETIME
    /// alone releases the link. This case forces delivery (<c>--merge-on-success</c>), so a wholly-green run routes
    /// through <c>ShouldReclaimOnCompletion == true</c>: completion-cleanup A removes the junction LINK FIRST, and
    /// the method-scoped <c>junctionLifetime</c> Dispose → <c>ReleaseOnce</c> must then NO-OP via the
    /// <see cref="WorktreeJunction.IsJunctionTo"/> guard — proving A-removes-then-lifetime-no-ops is non-destructive
    /// and leak-free. Windows-gated: only Windows creates a junction (the whole cleanup-A-vs-lifetime interplay
    /// under test is the Windows link lifecycle).
    /// </summary>
    [Fact]
    public async Task DeliveredGreenRun_ForcesAJunction_CleanupARemovesIt_ThenLifetimeDisposeNoOps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // a junction is only ever created on Windows — nothing to cleanup-A vs lifetime-release elsewhere
        }

        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // A long task id FORCES the junction (the #407 C lazy predicate), and the task writes a real deliverable
        // so delivery is a genuine fast-forward (not an "already up to date" no-op) — HEAD actually moves.
        string planDir = CreatePlanWithLongTaskIdThatDelivers(repo.RepoPath);

        // NOT --no-merge-on-success: force delivery ON so the run is wholly-green AND DELIVERED — the ONLY route
        // into ShouldReclaimOnCompletion == true (green + FastForwarded/Merged + not undelivered), which fires
        // completion-cleanup A.
        (int exit, string output) =
            await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--merge-on-success");

        // (i) delivered (green, merged): exit 0, the user branch fast-forwarded to the plan tip (HEAD moved, same
        //     named branch), and the deliverable landed in the user's checkout.
        Assert.Equal(0, exit);
        Assert.NotEqual(initialHead, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
        Assert.True(
            File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "the delivered-green run must land its deliverable in the user's checkout (real FF, not a no-op).");

        // A junction WAS forced, and completion-cleanup A removed the LINK first — the A message names the junction
        // and is only reachable when ShouldReclaimOnCompletion is true (green AND delivered), so it doubly confirms
        // delivery.
        Assert.Contains("worktree junction:", output, StringComparison.Ordinal);                 // forced
        Assert.Contains("worktree reclaimed on completion", output, StringComparison.Ordinal);   // A fired
        Assert.Contains("and junction", output, StringComparison.Ordinal);                       // A removed the LINK
        Assert.Contains("issue #407 A", output, StringComparison.Ordinal);

        // The cleanup-A-then-Dispose-no-op path: A already removed the link, so the lifetime's ReleaseOnce guards
        // into a NON-DESTRUCTIVE no-op and never logs its OWN release line. That line ("worktree junction released
        // on exit:" — with the colon) is DISTINCT from the allocation line's trailing "released on exit (issue
        // #419)." substring, so its absence pins the no-op precisely.
        Assert.DoesNotContain("worktree junction released on exit:", output, StringComparison.Ordinal);

        // (ii) NO junction survives for the plan, and (iii) run.json carries no worktreeJunctionRoot — leak-free.
        AssertNoSurvivingJunctionForPlan(planDir);
        AssertNoJournaledJunction(planDir);
    }

    /// <summary>
    /// Issue #419 WEAK-2 — <see cref="Guardrails.Core.Execution.WorktreeReclaim.ReclaimRootsOnExit"/> (invoked in
    /// <c>RunCommand</c>'s OUTER <c>finally</c>) reclaims a stale foreign root, proven END-TO-END through a real
    /// worktree-mode run. The run's root is pinned into a CONTROLLED temp dir so the sweep's candidate-root parent
    /// resolves THERE, never the real <c>%TEMP%\gr-wt</c> or the drive root.
    /// <para>
    /// <b>Why the exit sweep is the LOAD-BEARING reclaimer (not the startup GC).</b> The startup GC
    /// (<see cref="Guardrails.Core.Execution.WorktreeReclaim.Reclaim"/>) and the exit sweep share the SAME
    /// <c>SweepRoots</c> over the SAME candidate parents, so a pre-aged stale root would be reclaimed at run START
    /// and the exit sweep would prove nothing. To isolate the exit sweep, the planted stale root starts FRESH (the
    /// startup GC KEEPS it) and the plan's OWN task ages it to &gt; 24h MID-RUN — so ONLY the exit sweep, running
    /// after the task, can reclaim it. Deterministic via task-execution ORDER (a gate), no sleeps: if
    /// <c>ReclaimRootsOnExit</c> were removed, the aged root would survive and this test fails.
    /// </para>
    /// <para>
    /// <b>Why the per-plan <c>worktreeRoot</c> key, not the <c>GUARDRAILS_WORKTREE_ROOT</c> env override.</b>
    /// <c>WorktreeRootFor</c> resolves env → config → default, and env and config BOTH compute
    /// <c>Combine(value, planHash)</c>, so the resulting <c>currentRealRoot</c> and thus
    /// <c>CandidateRootParents</c>/<c>SweepRoots</c> path is byte-identical. The env var is process-global and
    /// other Integration tests drive real worktree runs that read it concurrently, so mutating it in the parallel
    /// suite is not isolation-safe; the per-plan key drives the identical code path with zero global state.
    /// Cross-platform (the root leak is not Windows-only); a junction may or may not be forced and is irrelevant to
    /// the ROOT sweep under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorktreeRun_ExitSweep_ReclaimsStaleForeignRoot_KeepsFreshLiveLockedAndCurrent()
    {
        using var repo = new TempGitRepo();

        // A CONTROLLED temp dir the run's root-sweep candidate parent resolves to (via the plan's worktreeRoot
        // key). EVERYTHING planted + asserted lives HERE, never the real %TEMP%\gr-wt or the drive root.
        string worktreeRootDir = Path.Combine(Path.GetTempPath(), "gr419b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreeRootDir);
        try
        {
            // Planted siblings — all DIRECT children of worktreeRootDir, so the run's root sweep scans them.
            string plantedStale = PlantForeignRoot(worktreeRootDir, "planted-stale");   // aged MID-RUN → exit sweep reclaims
            string freshControl = PlantForeignRoot(worktreeRootDir, "fresh-control");   // never aged → kept (fresh = possibly-active)

            // A STALE-but-LIVE-LOCKED control: WriteRunLock names THIS live test process (which bumps the dir mtime
            // by writing the lock file), THEN age the whole tree past 24h so the ONLY thing keeping it is the lock
            // (F1 — a live run parked idle writes nothing yet must not be reclaimed).
            string lockedControl = PlantForeignRoot(worktreeRootDir, "locked-control");
            WorktreeReclaim.WriteRunLock(lockedControl);
            AgeTreeThreeDaysOld(lockedControl);

            string planDir = CreatePlanThatAgesAForeignRoot(repo.RepoPath, worktreeRootDir, plantedStale);

            // Undelivered green (exit 0) KEEPS the run's OWN root (ShouldReclaimOnCompletion false), so we can
            // assert the exit sweep's self-exclusion keeps it. The exit sweep fires regardless of outcome.
            (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
            Assert.Equal(0, exit);

            // The load-bearing assertion: the stale (aged-mid-run) foreign root was reclaimed — and ONLY the exit
            // sweep could have done it, since the startup GC saw it FRESH.
            Assert.False(
                Directory.Exists(plantedStale),
                "ReclaimRootsOnExit must reclaim a stale, unlocked, non-current foreign root at run exit (#419).");

            // Controls survive: a FRESH sibling (possibly-active) and a STALE-but-LIVE-LOCKED sibling (F1) ...
            Assert.True(Directory.Exists(freshControl), "a fresh (possibly-active) foreign root must be KEPT.");
            Assert.True(
                Directory.Exists(lockedControl),
                "a live-locked foreign root must be KEPT regardless of mtime (#407 F1).");

            // ... and the run's OWN root is excluded from its own exit sweep (kept for the undelivered/resumable outcome).
            PlanLoadResult ownLoad = new PlanLoader().Load(planDir);
            Assert.NotNull(ownLoad.Plan);
            string ownRoot = SchedulerFactory.WorktreeRootFor(ownLoad.Plan!);
            Assert.True(
                Directory.Exists(ownRoot),
                "the current run's own worktree root must never be reclaimed by its own exit sweep.");
        }
        finally
        {
            try { SafeDelete.DeleteDirectory(worktreeRootDir); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ── assertions ───────────────────────────────────────────────────────────────────────────

    private static void AssertNoJournaledJunction(string planDir)
    {
        string journalPath = Path.Combine(planDir, "state", "run.json");
        Assert.True(File.Exists(journalPath), "run.json should exist after a run");
        Assert.DoesNotContain(
            "worktreeJunctionRoot", File.ReadAllText(journalPath), StringComparison.Ordinal);
    }

    private static void AssertNoSurvivingJunctionForPlan(string planDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // junctions are Windows-only
        }

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        string realRoot = SchedulerFactory.WorktreeRootFor(load.Plan!);
        string? drive = Path.GetPathRoot(realRoot);
        if (string.IsNullOrEmpty(drive))
        {
            return;
        }

        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            string link = Path.Combine(drive, leaf);
            Assert.False(
                WorktreeJunction.IsJunctionTo(link, realRoot),
                $"a worktree-mode run must leave NO junction to its real root; '{link}' still points at '{realRoot}' (#419).");
        }
    }

    private static string JunctionLiteral() =>
        OperatingSystem.IsWindows() ? @"C:\\.a" : "/tmp/.a";

    // ── the #120 driver + fixture ──────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("worktree-junction-run test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>A worktree-mode plan (maxParallelism 2 + a real git repo) with two green script tasks; the guardrail of one optionally always fails (→ needs-human).</summary>
    private static string CreatePlan(string repoPath, bool guardrailFails)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGreenScriptTask(Path.Combine(planDir, "tasks", "01-first"), guardrailFails: false, dependsOn: []);
        WriteGreenScriptTask(Path.Combine(planDir, "tasks", "02-second"), guardrailFails, dependsOn: ["01-first"]);
        return planDir;
    }

    /// <summary>A worktree-mode plan whose single task has a deliberately LONG folder name, so the #407 C lazy predicate FORCES a Windows short junction.</summary>
    private static string CreatePlanWithLongTaskId(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGreenScriptTask(
            Path.Combine(planDir, "tasks", "01-force-a-windows-short-junction-allocation-for-max-path-headroom"),
            guardrailFails: false, dependsOn: []);
        return planDir;
    }

    /// <summary>
    /// A worktree-mode plan (issue #419 Case A) whose single, deliberately-LONG-id task WRITES a deliverable
    /// (<c>src/app.cs</c>) so a wholly-green run FF-delivers it — the long id FORCES the junction, and the file
    /// makes delivery a real HEAD-advancing fast-forward rather than an "already up to date" no-op.
    /// </summary>
    private static string CreatePlanWithLongTaskIdThatDelivers(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteDeliverableScriptTask(
            Path.Combine(planDir, "tasks", "01-force-a-windows-short-junction-allocation-for-max-path-headroom"));
        return planDir;
    }

    /// <summary>A green script task whose action writes <c>src/app.cs</c> into the segment workspace (so a wholly-green worktree-mode run delivers a real file), guardrail exit-0, OS-picked flavour.</summary>
    private static void WriteDeliverableScriptTask(string taskDir)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "green script task that writes a deliverable", "writeScope": ["src/app.cs"], "dependsOn": [] }""");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                "New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\app.cs\" -Force -Value 'class App {}' | Out-Null\r\nexit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\nmkdir -p \"$GUARDRAILS_WORKSPACE/src\"\nprintf 'class App {}' > \"$GUARDRAILS_WORKSPACE/src/app.cs\"\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>
    /// A worktree-mode plan (issue #419 Case B) pinned to a CONTROLLED <paramref name="worktreeRootDir"/> via the
    /// per-plan <c>worktreeRoot</c> key (the parallel-safe equivalent of the <c>GUARDRAILS_WORKTREE_ROOT</c> env
    /// override — same <c>WorktreeRootFor</c> precedence family, same <c>Combine(value, planHash)</c>, so the exit
    /// sweep's candidate-parent/<c>SweepRoots</c> path is identical). Its single green task AGES
    /// <paramref name="foreignRootToAge"/> to &gt; 24h MID-RUN, so the startup GC (which ran while it was still
    /// fresh) cannot have reclaimed it — leaving the exit sweep as the only possible reclaimer.
    /// </summary>
    private static string CreatePlanThatAgesAForeignRoot(string repoPath, string worktreeRootDir, string foreignRootToAge)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        string worktreeRootJson = worktreeRootDir.Replace("\\", "\\\\"); // JSON-escape Windows backslashes (no-op on Unix)
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "worktreeRoot": "{{worktreeRootJson}}",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteRootAgingTask(Path.Combine(planDir, "tasks", "01-age-a-foreign-root"), foreignRootToAge);
        return planDir;
    }

    /// <summary>A green script task whose action ages <paramref name="rootToAge"/> (root + all entries) to &gt; 24h old, OS-picked flavour; guardrail exit-0. writeScope empty — the age target is an absolute path OUTSIDE the workspace (test setup mid-run, not a workspace write).</summary>
    private static void WriteRootAgingTask(string taskDir, string rootToAge)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "ages a planted foreign worktree root so ONLY the exit sweep can reclaim it", "writeScope": [], "dependsOn": [] }""");

        if (OperatingSystem.IsWindows())
        {
            string psPath = rootToAge.Replace("'", "''"); // double single-quotes for the PS literal (temp paths carry none, but be safe)
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                "$t = [DateTime]::UtcNow.AddDays(-3)\r\n"
                + $"Get-ChildItem -LiteralPath '{psPath}' -Recurse -Force | ForEach-Object {{ $_.LastWriteTimeUtc = $t }}\r\n"
                + $"(Get-Item -LiteralPath '{psPath}' -Force).LastWriteTimeUtc = $t\r\n"
                + "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            // touch -t CCYYMMDDhhmm is portable across GNU (Linux) and BSD (macOS) touch; 2020-01-01 is well past 24h.
            // `find <path>` includes <path> itself, so the root dir mtime is aged too (TreeIsStale checks it first).
            WriteExecutable(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\n" + $"find '{rootToAge}' -exec touch -t 202001010000 {{}} +\n" + "exit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>Create a FRESH (mtime ~now) foreign worktree root <paramref name="name"/> with one marker file under <paramref name="parent"/>; returns its path.</summary>
    private static string PlantForeignRoot(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "marker.txt"), "m");
        return root;
    }

    /// <summary>Stamp <paramref name="root"/> and every descendant to 3 days ago — descendants first, root last (matching the reclaim tests' aging).</summary>
    private static void AgeTreeThreeDaysOld(string root)
    {
        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(3);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry)) { Directory.SetLastWriteTimeUtc(entry, old); }
            else { File.SetLastWriteTimeUtc(entry, old); }
        }

        Directory.SetLastWriteTimeUtc(root, old);
    }

    private static void WriteGreenScriptTask(string taskDir, bool guardrailFails, string[] dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        string depends = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "green script task", "writeScope": [], "dependsOn": {{depends}} }""");

        string guardBody = guardrailFails ? "exit 1" : "exit 0";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), guardBody + "\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\n" + guardBody + "\n");
        }
    }

    private static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
            psi.ArgumentList.Add("+x");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-junc-run-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_rootDir, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# junc-run-test");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        /// <summary>The repo's current HEAD sha (used by Case A to prove the delivered-green FF advanced the user branch).</summary>
        public string HeadSha() => Git("rev-parse", "HEAD").Trim();

        /// <summary>The repo's current branch (Case A asserts delivery stays on the user's original named branch, not a detached HEAD).</summary>
        public string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        private string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
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
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_rootDir); } catch { /* best-effort temp cleanup */ }
        }
    }
}
