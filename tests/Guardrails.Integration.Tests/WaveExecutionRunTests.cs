using System.Diagnostics;
using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// M2b wave-execution end-to-end with REAL git worktrees + a real <see cref="GitWorktreeProvider"/> and a
/// real <see cref="GuardrailReVerifier"/> for the per-wave entry/exit gates (SSOT §14). OS-picked
/// <c>.ps1</c>/<c>.sh</c> scripts. Proves the continuity contract the fakes cannot: ONE integration
/// worktree, ONE continuous plan branch carrying BOTH waves' outputs + a <c>Guardrails-Wave:</c> marker
/// commit per wave (decision E), a wave-2 ENTRY preflight that actually sees wave-1's output materialized
/// on the branch, cross-wave resume, and a wave-scoped reset that really rewinds the plan branch.
/// </summary>
public sealed class WaveExecutionRunTests
{
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wave-it-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# wave-e2e");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public IReadOnlyList<string> PlanBranchFiles(string planName) =>
            Git(RepoPath, "ls-tree", "-r", "--name-only", $"guardrails/{planName}")
                .Replace('\\', '/').Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        /// <summary>The wave dirs that carry a <c>Guardrails-Wave:</c> marker commit on the plan branch, newest-first.</summary>
        public IReadOnlyList<string> WaveMarkers(string planName) =>
            Git(RepoPath, "log", "--first-parent", "--format=%B", $"guardrails/{planName}")
                .Replace("\r\n", "\n").Split('\n')
                .Where(l => l.StartsWith("Guardrails-Wave: ", StringComparison.Ordinal))
                .Select(l => l["Guardrails-Wave: ".Length..].Trim())
                .ToList();

        public string PlanBranchTip(string planName) => Git(RepoPath, "rev-parse", $"guardrails/{planName}").Trim();

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); } catch { /* best-effort */ }
        }
    }

    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    /// <summary>Write a file-creating action script (writes <paramref name="file"/> = "x" under the workspace, exit 0).</summary>
    private static void WriteAction(string path, string file)
    {
        string body = Ps
            ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/{file}\" -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(path, body);
    }

    /// <summary>Write a file-existence guardrail/gate script (opens with a catches: comment; passes iff <paramref name="file"/> exists).</summary>
    private static void WriteExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\nif (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/{file}\")) {{ Write-Output '{file} missing'; exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo '{file} missing'; exit 1; }}\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void WriteTask(string waveTasksDir, string folder, string file, bool guardrailPasses = true)
    {
        string taskDir = Path.Combine(waveTasksDir, folder);
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), $$"""{ "description": "{{folder}}" }""");
        WriteAction(Path.Combine(taskDir, Script("action")), file);
        // Task guardrail: check the file exists (guardrailPasses=false → check a file that never exists).
        WriteExistsGate(Path.Combine(taskDir, "guardrails", Script("01-check")),
            guardrailPasses ? file : "never-created-" + Guid.NewGuid().ToString("N")[..6]);
    }

    /// <summary>
    /// Build the standard 2-wave e2e plan under <c>repo/plan</c>:
    /// wave-01-scaffold writes config.txt (+ exit gate asserts it); wave-02-build has an ENTRY preflight
    /// asserting config.txt materialized, writes build.txt (+ exit gate asserts it).
    /// </summary>
    private static string CreateTwoWavePlan(string repoPath, bool wave2GuardrailPasses = true)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);

        string w1 = Path.Combine(planDir, "wave-01-scaffold");
        WriteTask(Path.Combine(w1, "tasks"), "01-config", "config.txt");
        WriteExistsGate(Path.Combine(w1, "guardrails", Script("01-scaffold-sound")), "config.txt");

        string w2 = Path.Combine(planDir, "wave-02-build");
        WriteExistsGate(Path.Combine(w2, "preflights", Script("01-config-materialized")), "config.txt");
        WriteTask(Path.Combine(w2, "tasks"), "01-compile", "build.txt", wave2GuardrailPasses);
        WriteExistsGate(Path.Combine(w2, "guardrails", Script("01-build-passes")), "build.txt");

        return planDir;
    }

    private static async Task<(RunReport report, RunJournal journal)> RunAsync(string planDir, string repoPath, string worktreeRoot)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in wave e2e tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var provider = new GitWorktreeProvider(repoPath, worktreeRoot);
        var reVerifier = new GuardrailReVerifier(new ProcessRunner(), interpreterMap);
        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: reVerifier);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoWavePlan_RunsGreen_OneContinuousPlanBranch_BothOutputs_MarkerPerWave()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);

        var (report, journal) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        // ONE continuous plan branch carries BOTH waves' outputs (continuity: wave-1's output survived wave-2).
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("config.txt", committed);
        Assert.Contains("build.txt", committed);

        // A Guardrails-Wave: marker commit per wave (decision E), both waves.
        IReadOnlyList<string> markers = repo.WaveMarkers("plan");
        Assert.Contains("wave-01-scaffold", markers);
        Assert.Contains("wave-02-build", markers);

        // Journal records both waves complete on the ONE journal.
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-01-scaffold")!.Status);
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-02-build")!.Status);
    }

    [Fact]
    public async Task Barrier_Wave2GuardrailFails_HaltsRun_Wave1StaysDurableOnBranch()
    {
        using var repo = new TempGitRepo();
        // wave-2's task guardrail checks a file that never exists → wave-2 halts (needs-human).
        string planDir = CreateTwoWavePlan(repo.RepoPath, wave2GuardrailPasses: false);

        var (report, journal) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.False(report.AllSucceeded);
        // wave-1 is durable on the plan branch (config.txt present) even though the run halted at wave-2.
        Assert.Contains("config.txt", repo.PlanBranchFiles("plan"));
        Assert.DoesNotContain("build.txt", repo.PlanBranchFiles("plan")); // wave-2 rolled back / not integrated
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-01-scaffold")!.Status);
        Assert.Contains("wave-01-scaffold", repo.WaveMarkers("plan"));
        Assert.DoesNotContain("wave-02-build", repo.WaveMarkers("plan"));
    }

    [Fact]
    public async Task Resume_AfterWave2Fails_ThenFixed_SkipsWave1_CompletesWave2()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath, wave2GuardrailPasses: false);

        // Run 1: wave-1 completes, wave-2 halts.
        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.False(r1.AllSucceeded);
        string wave1MarkerTipAfterRun1 = repo.PlanBranchTip("plan"); // wave-1 marker is at/near the tip
        IReadOnlyList<string> filesAfter1 = repo.PlanBranchFiles("plan");
        Assert.Contains("config.txt", filesAfter1);

        // Fix wave-2's task guardrail to pass, then resume.
        WriteExistsGate(
            Path.Combine(planDir, "wave-02-build", "tasks", "01-compile", "guardrails", Script("01-check")),
            "build.txt");

        var (r2, journal2) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(r2.AllSucceeded, string.Join("; ", r2.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Contains("build.txt", repo.PlanBranchFiles("plan"));
        // wave-1 was SKIPPED on resume — its task shows as a resume skip, not re-run.
        Assert.Equal(TaskOutcome.Skipped, r2.Tasks.Single(t => t.TaskId == "wave-01-scaffold/01-config").Outcome);
        // The wave-1 marker is unchanged (its commit was not re-created), and wave-2 now has a marker.
        Assert.Contains("wave-02-build", repo.WaveMarkers("plan"));
        Assert.Equal(WaveStatus.Completed, journal2.WaveEntryOf("wave-02-build")!.Status);
    }

    [Fact]
    public async Task WaveScopedReset_RealRewind_RemovesWave2FromBranch_ReRunRestoresIt()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);

        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r1.AllSucceeded);
        Assert.Contains("build.txt", repo.PlanBranchFiles("plan"));

        // Wave-scoped reset of wave-2 REALLY rewinds the plan branch past it.
        PlanLoadResult load = new PlanLoader().Load(planDir);
        RunReset.WaveResetResult reset = RunReset.WaveReset(load.Plan!, "wave-02-build");
        Assert.Equal(RunReset.WaveResetOutcome.Done, reset.Outcome);
        Assert.NotNull(reset.RewindTarget); // a real rewind happened (there is a predecessor marker / base)

        IReadOnlyList<string> afterReset = repo.PlanBranchFiles("plan");
        Assert.Contains("config.txt", afterReset);      // wave-1 preserved
        Assert.DoesNotContain("build.txt", afterReset);  // wave-2 rewound off the branch
        Assert.DoesNotContain("wave-02-build", repo.WaveMarkers("plan"));

        // Re-run: wave-1 skipped, wave-2 re-runs and restores build.txt.
        var (r2, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r2.AllSucceeded, string.Join("; ", r2.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Contains("build.txt", repo.PlanBranchFiles("plan"));
    }

    // ── #311 remediation red-bars (destructive rewind path) ─────────────────────────────────────

    [Fact]
    public async Task WaveReset_TrailerlessHumanHandFixInRange_Refuses_DoesNotDiscardIt()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);
        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r1.AllSucceeded);

        // #197 human hand-fix: a TRAILER-LESS commit committed directly onto the plan branch (appended via
        // commit-tree/update-ref so no worktree checkout is needed). It carries no Guardrails-Task: trailer
        // and is NOT a Guardrails-Wave: marker.
        string tip = repo.PlanBranchTip("plan");
        string tree = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{tip}^{{tree}}").Trim();
        string handFix = TempGitRepo.Git(repo.RepoPath, "commit-tree", tree, "-p", tip, "-m", "human hand-fix (no trailer)").Trim();
        TempGitRepo.Git(repo.RepoPath, "update-ref", "refs/heads/guardrails/plan", handFix);

        // Wave-scoped reset of wave-2 would discard the hand-fix (it is in the removed range) → REFUSE.
        RunReset.WaveResetResult reset = RunReset.WaveReset(new PlanLoader().Load(planDir).Plan!, "wave-02-build");

        Assert.Equal(RunReset.WaveResetOutcome.Refused, reset.Outcome);
        Assert.Equal(handFix, repo.PlanBranchTip("plan")); // nothing was discarded — the hand-fix is intact
    }

    [Fact]
    public async Task WaveReset_WaveTrailedButNonEmptyHandFix_Refuses_DoesNotDiscardIt()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);
        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r1.AllSucceeded);

        // #311 WEAK-1 red-bar: a human hand-fix that CHANGES A FILE and (via `git commit --amend` onto a
        // marker tip, or a copy-pasted trailer) carries a Guardrails-Wave: trailer. The marker exemption
        // gates on empty-tree-delta, so this impersonator is NOT exempted → falls through to the trailer-less
        // REFUSE and is preserved. Committed in a DETACHED worktree (the branch is checked out elsewhere),
        // then the branch ref is moved to it.
        string handFixWt = Path.Combine(repo.WorktreeRoot, "handfix");
        TempGitRepo.Git(repo.RepoPath, "worktree", "add", "--detach", handFixWt, "guardrails/plan");
        try
        {
            File.WriteAllText(Path.Combine(handFixWt, "config.txt"), "hand-fixed by a human");
            TempGitRepo.Git(handFixWt, "add", "-A");
            TempGitRepo.Git(handFixWt, "commit", "-m", "human hand-fix\n\nGuardrails-Wave: wave-99-impersonator");
            string handFix = TempGitRepo.Git(handFixWt, "rev-parse", "HEAD").Trim();
            TempGitRepo.Git(repo.RepoPath, "update-ref", "refs/heads/guardrails/plan", handFix);

            RunReset.WaveResetResult reset = RunReset.WaveReset(new PlanLoader().Load(planDir).Plan!, "wave-02-build");

            Assert.Equal(RunReset.WaveResetOutcome.Refused, reset.Outcome);
            Assert.Equal(handFix, repo.PlanBranchTip("plan")); // the Wave-trailered impersonator was NOT discarded
        }
        finally
        {
            try { TempGitRepo.Git(repo.RepoPath, "worktree", "remove", "--force", handFixWt); }
            catch (InvalidOperationException) { /* best-effort teardown */ }
        }
    }

    [Fact]
    public async Task WaveReset_IgnoresDanglingJournalMarkerSha_RewindsFromLiveHistory()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);
        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r1.AllSucceeded);

        // Corrupt the journal's recorded wave MarkerSha to a bogus/dangling sha — the BLOCKER-1b hazard.
        // The rewind must DERIVE its target from the live first-parent history (evaluator), never trust
        // this stored sha (which would `git reset --hard` to a nonexistent object / sideways).
        string journalPath = RunJournal.PathFor(planDir);
        string corrupted = Regex.Replace(
            File.ReadAllText(journalPath), "\"markerSha\": \"[0-9a-f]+\"",
            "\"markerSha\": \"0000000000000000000000000000000000000000\"");
        File.WriteAllText(journalPath, corrupted);

        RunReset.WaveResetResult reset = RunReset.WaveReset(new PlanLoader().Load(planDir).Plan!, "wave-02-build");

        Assert.Equal(RunReset.WaveResetOutcome.Done, reset.Outcome);
        Assert.Contains("config.txt", repo.PlanBranchFiles("plan"));      // wave-1 preserved (correct target)
        Assert.DoesNotContain("build.txt", repo.PlanBranchFiles("plan")); // wave-2 rewound (not sideways)
    }

    [Fact]
    public async Task WaveReset_IsHeadIndependent_AfterUserSwitchesBranch()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoWavePlan(repo.RepoPath);
        var (r1, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(r1.AllSucceeded);

        // The user switches to a divergent branch rooted at the FIRST commit (WEAK-2): a merge-base-vs-HEAD
        // target would compute the wrong base. The rewind must use the plan branch's OWN history instead.
        string firstCommit = TempGitRepo.Git(repo.RepoPath, "rev-list", "--max-parents=0", "HEAD").Trim();
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", "legacy", firstCommit);

        RunReset.WaveResetResult reset = RunReset.WaveReset(new PlanLoader().Load(planDir).Plan!, "wave-01-scaffold");

        Assert.Equal(RunReset.WaveResetOutcome.Done, reset.Outcome);
        Assert.Equal(["wave-01-scaffold", "wave-02-build"], reset.ResetWaves.ToArray());
        // Rewound to the plan branch's own base (both wave outputs gone), NOT below it (README.md intact).
        IReadOnlyList<string> files = repo.PlanBranchFiles("plan");
        Assert.Contains("README.md", files);
        Assert.DoesNotContain("config.txt", files);
        Assert.DoesNotContain("build.txt", files);
    }
}
