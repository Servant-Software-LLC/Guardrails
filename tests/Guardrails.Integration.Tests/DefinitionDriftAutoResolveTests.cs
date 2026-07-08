using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Part C real-git tests (issue #274, SSOT §7.2): the DESTRUCTIVE plan-branch rewind primitive end to
/// end. Proves the git-backed history gatherer builds the safe-suffix model correctly (linear + a fan-in
/// merge, the merge-tip caveat with REAL git), the rewind physically removes commits while leaving them
/// reflog-recoverable, the run-time auto-resolve rewinds + re-runs the safe suffix green, a strict
/// <c>halt</c> policy always halts, and the manual scoped reset rewinds a safe set / refuses an unsafe one.
/// The pure decision logic is exhaustively pinned in <c>SafeSuffixEvaluatorTests</c>; these tests pin the
/// git plumbing and the wiring.
/// </summary>
public sealed class DefinitionDriftAutoResolveTests
{
    private static bool Ps => OperatingSystem.IsWindows();
    private static string GuardrailFile => Ps ? "01-check.ps1" : "01-check.sh";

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        /// <summary>The repo's default branch name (main/master, whatever this git configured) — for checkout-back.</summary>
        public string OriginalBranch { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-driftc-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# driftc-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
            OriginalBranch = Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        }

        public void CommitAll(string message)
        {
            Git(RepoPath, "add", "-A");
            Git(RepoPath, "commit", "-m", message);
        }

        public static string Git(string workingDir, params string[] args)
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
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    private sealed class PassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails, CancellationToken ct = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    private static string Trailer(string taskId, string hash = "sha256:deadbeef") =>
        $"integrate\n\nGuardrails-Task: {taskId}\nGuardrails-Run: r1\nGuardrails-Task-Hash: {hash}";

    // ---------------------------------------------------------------------------------------------
    // 1. The git-backed gatherer + safe-suffix check over hand-built history (deterministic).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Gatherer_LinearHistory_EvaluatesSafe_WithCorrectResetTarget()
    {
        using var repo = new TempGitRepo();
        const string branch = "guardrails/plan";
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", branch);
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("01-a"));
        string sha01 = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("02-b"));
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("03-c"));

        IReadOnlyList<TrailerCommit> history = GitWorktreeProvider.GatherFirstParentHistory(repo.RepoPath, branch);
        // newest-first: 03, 02, 01, base(no trailer)
        Assert.Equal("03-c", history[0].Task);
        Assert.Equal("02-b", history[1].Task);
        Assert.Equal("01-a", history[2].Task);
        Assert.Null(history[3].Task); // the base README commit has no trailer

        SafeSuffixDecision decision = GitWorktreeProvider.EvaluateSafeSuffix(
            repo.RepoPath, branch, new HashSet<string>(["02-b", "03-c"], StringComparer.Ordinal));

        Assert.Equal(SafeSuffixOutcome.Safe, decision.Outcome);
        Assert.Equal(sha01, decision.ResetTarget); // parent of the oldest removed commit (02) == 01
        Assert.Equal(2, decision.RemovedCommitCount);
    }

    [Fact]
    public void Gatherer_FanInMerge_SurfacesLineage_MergeTipCaveat_RealGit()
    {
        using var repo = new TempGitRepo();
        const string branch = "guardrails/plan";
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", branch);
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("01-a"));

        // A side lineage carrying 07-upstream, reachable ONLY through the merge's non-first-parent.
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", "side");
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("07-upstream"));
        TempGitRepo.Git(repo.RepoPath, "checkout", branch);
        TempGitRepo.Git(repo.RepoPath, "merge", "--no-ff", "side", "-m", Trailer("04-d"));

        var setWithoutUpstream = new HashSet<string>(["04-d", "01-a"], StringComparer.Ordinal);
        SafeSuffixDecision refused = GitWorktreeProvider.EvaluateSafeSuffix(repo.RepoPath, branch, setWithoutUpstream);
        Assert.Equal(SafeSuffixOutcome.Refused, refused.Outcome); // the merge pulls in 07-upstream, not in S
        Assert.Equal("07-upstream", refused.BlockingTask);

        var setWithUpstream = new HashSet<string>(["04-d", "01-a", "07-upstream"], StringComparer.Ordinal);
        SafeSuffixDecision safe = GitWorktreeProvider.EvaluateSafeSuffix(repo.RepoPath, branch, setWithUpstream);
        Assert.Equal(SafeSuffixOutcome.Safe, safe.Outcome); // now the whole lineage is contained
    }

    [Fact]
    public void Rewind_PhysicallyRemovesCommits_ButLeavesThemReflogRecoverable()
    {
        using var repo = new TempGitRepo();
        const string branch = "guardrails/plan";
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", branch);
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("01-a"));
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("02-b"));
        string sha02 = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("03-c"));
        string sha03 = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();

        SafeSuffixDecision decision = GitWorktreeProvider.EvaluateSafeSuffix(
            repo.RepoPath, branch, new HashSet<string>(["03-c"], StringComparer.Ordinal));
        Assert.Equal(SafeSuffixOutcome.Safe, decision.Outcome);
        Assert.Equal(sha02, decision.ResetTarget);

        GitWorktreeProvider.RewindPlanBranch(repo.RepoPath, branch, decision.ResetTarget!);

        // The branch tip is now 02 — 03 is physically gone from the branch's first-parent history.
        Assert.Equal(sha02, TempGitRepo.Git(repo.RepoPath, "rev-parse", branch).Trim());
        string fpLog = TempGitRepo.Git(repo.RepoPath, "log", "--first-parent", "--format=%H", branch);
        Assert.DoesNotContain(sha03, fpLog);

        // …but the discarded commit is recoverable: the object still exists AND the branch reflog's
        // pre-rewind position (@{1}) is exactly the discarded tip.
        Assert.Equal("commit", TempGitRepo.Git(repo.RepoPath, "cat-file", "-t", sha03).Trim());
        string reflogPrev = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{branch}@{{1}}").Trim();
        Assert.Equal(sha03, reflogPrev);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. End-to-end run-time auto-resolve (real scheduler, real GitWorktreeProvider).
    // ---------------------------------------------------------------------------------------------

    private static string CreateLinearPlan(string repoPath, string driftPolicyLine)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2{{driftPolicyLine}}
            }
            """);
        WriteTask(planDir, "01-task-a", []);
        WriteTask(planDir, "02-task-b", ["01-task-a"]);
        return planDir;
    }

    private static void WriteTask(string planDir, string taskId, string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        string dependsJson = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "driftc {{taskId}}", "dependsOn": {{dependsJson}} }""");

        string safe = taskId.Replace("-", "_");
        if (Ps)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{taskId}.cs\" -Force -Value 'class {safe} {{}}' | Out-Null\nexit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", GuardrailFile), "# original guardrail\nexit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                $"printf 'class {safe} {{}}' > \"$GUARDRAILS_WORKSPACE/src/{taskId}.cs\"\nexit 0\n");
            MakeExec(actionPath);
            string gp = Path.Combine(taskDir, "guardrails", GuardrailFile);
            File.WriteAllText(gp, "#!/usr/bin/env bash\n# original guardrail\nexit 0\n");
            MakeExec(gp);
        }
    }

    private static void MakeExec(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static async Task<RunReport> RunAsync(
        string planDir, GitWorktreeProvider provider, bool driftPreConfirmed, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("no prompt runners in drift tests"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: new PassReVerifier(),
            driftPreConfirmed: driftPreConfirmed);
        return await scheduler.RunAsync(load.Plan!, ct);
    }

    [Fact]
    public async Task Reprocess_EditedSucceededTask_RewindsReRunsGreen_StaleGone_Journaled()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath, ",\n  \"driftPolicy\": \"reprocess\"");
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        RunReport first = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), false, ct);
        Assert.True(first.AllSucceeded, "phase 1 must fully succeed");

        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";
        string staleHash = ReadTaskHashOnBranch(repo, planBranch, "01-task-a");

        // Edit the already-succeeded 01's guardrail (a real definition change; still exits 0).
        File.WriteAllText(Path.Combine(planDir, "tasks", "01-task-a", "guardrails", GuardrailFile),
            (Ps ? "" : "#!/usr/bin/env bash\n") + "# edited guardrail — an extra assertion line\nexit 0\n");

        RunReport second = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), false, ct);

        Assert.Null(second.DefinitionDrift);                 // auto-resolved, not halted
        Assert.True(second.AllSucceeded);
        Assert.NotNull(second.DriftResolution);
        Assert.Equal("reprocess", second.DriftResolution!.Trigger);
        Assert.Contains(second.DriftResolution.Tasks, t => t.TaskId == "01-task-a");
        Assert.Contains(second.DriftResolution.Tasks, t => t.TaskId == "02-task-b"); // descendant re-run too

        // The stale 01 integration is physically gone: the branch now records 01's NEW definition hash.
        string newHash = ReadTaskHashOnBranch(repo, planBranch, "01-task-a");
        Assert.NotEqual(staleHash, newHash);
        Assert.Equal(TaskDefinitionHash(planDir, "01-task-a"), newHash);

        // The durable driftResolutions[] audit was written.
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.NotNull(journal.DriftResolutions);
        DriftResolution recorded = Assert.Single(journal.DriftResolutions!);
        Assert.Equal("reprocess", recorded.Trigger);
        Assert.NotNull(recorded.RewindTarget);
    }

    [Fact]
    public async Task HaltPolicy_AlwaysHalts_EvenWhenPreConfirmed()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath, ",\n  \"driftPolicy\": \"halt\"");
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        RunReport first = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), false, ct);
        Assert.True(first.AllSucceeded);

        File.WriteAllText(Path.Combine(planDir, "tasks", "01-task-a", "guardrails", GuardrailFile),
            (Ps ? "" : "#!/usr/bin/env bash\n") + "# edited\nexit 0\n");

        // Even with driftPreConfirmed=true (simulating an operator y), a strict halt policy still halts.
        RunReport second = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), true, ct);

        Assert.NotNull(second.DefinitionDrift);
        Assert.Null(second.DriftResolution);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Manual scoped reset (RunReset.ScopedReset) — safe rewinds, unsafe refuses.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ManualScopedReset_Safe_RewindsSet_ThenReRunGreen()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath, "");
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        RunReport first = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), false, ct);
        Assert.True(first.AllSucceeded);

        PlanLoadResult load = new PlanLoader().Load(planDir);
        RunReset.ScopedResetResult result = RunReset.ScopedReset(load.Plan!, ["01-task-a"]);

        Assert.Equal(RunReset.ScopedResetOutcome.Done, result.Outcome);
        Assert.NotNull(result.RewindTarget);                                   // the plan branch was rewound
        Assert.Equal(["01-task-a", "02-task-b"], result.ResetTasks.OrderBy(x => x)); // set ∪ descendants

        // A plain resume now re-runs the reset set from the clean base and goes green — no drift halt.
        RunReport second = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), false, ct);
        Assert.Null(second.DefinitionDrift);
        Assert.True(second.AllSucceeded);
    }

    [Fact]
    public void ManualScopedReset_Unsafe_Refuses_NamesBlocker_AndDoesNotRewind()
    {
        using var repo = new TempGitRepo();

        // A fan-out plan (01 → {02, 03}) with a HAND-BUILT plan branch integrated in the order 01, 02, 03.
        // Resetting ONLY 02 (its descendants are empty) is NOT a trailing suffix — 03 sits newer than it.
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "workspace": "..", "maxParallelism": 2 }""");
        WriteTask(planDir, "01-a", []);
        WriteTask(planDir, "02-b", ["01-a"]);
        WriteTask(planDir, "03-c", ["01-a"]);
        repo.CommitAll("add plan");

        string branch = $"guardrails/{Path.GetFileName(planDir)}";
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", branch);
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("01-a"));
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("02-b"));
        TempGitRepo.Git(repo.RepoPath, "commit", "--allow-empty", "-m", Trailer("03-c"));
        string tipBefore = TempGitRepo.Git(repo.RepoPath, "rev-parse", branch).Trim();
        TempGitRepo.Git(repo.RepoPath, "checkout", repo.OriginalBranch);

        PlanLoadResult load = new PlanLoader().Load(planDir);
        // A journal must exist for ScopedReset to act; mark the tasks succeeded.
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        journal.RecordSettle("01-a", Guardrails.Core.Journal.TaskStatus.Succeeded);
        journal.RecordSettle("02-b", Guardrails.Core.Journal.TaskStatus.Succeeded);
        journal.RecordSettle("03-c", Guardrails.Core.Journal.TaskStatus.Succeeded);

        RunReset.ScopedResetResult result = RunReset.ScopedReset(load.Plan!, ["02-b"]);

        Assert.Equal(RunReset.ScopedResetOutcome.Refused, result.Outcome);
        Assert.Equal("03-c", result.BlockingTask);
        // The plan branch was left UNTOUCHED (refuse floor = HALT, never destroy).
        Assert.Equal(tipBefore, TempGitRepo.Git(repo.RepoPath, "rev-parse", branch).Trim());
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Read the <c>Guardrails-Task-Hash:</c> trailer recorded on the plan branch for <paramref name="taskId"/>.</summary>
    private static string ReadTaskHashOnBranch(TempGitRepo repo, string branch, string taskId)
    {
        IReadOnlyDictionary<string, PlanBranchTaskRecord> hashes =
            GitWorktreeProvider.ReadPlanBranchTaskHashes(repo.RepoPath, branch["guardrails/".Length..]);
        Assert.True(hashes.TryGetValue(taskId, out PlanBranchTaskRecord? record) && record.DefinitionHash is not null,
            $"expected a recorded hash for {taskId}");
        return record!.DefinitionHash!;
    }

    /// <summary>Compute the current on-disk <c>TaskDefinitionHash</c> for a task in <paramref name="planDir"/>.</summary>
    private static string TaskDefinitionHash(string planDir, string taskId)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Core.Model.TaskNode task = load.Plan!.Tasks.Single(t => t.Id == taskId);
        return Guardrails.Core.Journal.TaskDefinitionHash.Compute(task);
    }
}
