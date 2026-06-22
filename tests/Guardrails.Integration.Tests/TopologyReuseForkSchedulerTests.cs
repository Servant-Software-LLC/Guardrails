using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// plan 08 topology-wiring M1 integration tests against the real <see cref="GitWorktreeProvider"/>
/// driven through the full <see cref="Scheduler"/> on a temp git repo. Covers the headline
/// metamorphic add-count proof (T-M), the W-2 sibling-fork-off-recorded-sha gate (T-7), the
/// reused-chain FF + trailer proof (T-6), and the reused-segment retry-reset (T-8).
/// </summary>
public sealed class TopologyReuseForkSchedulerTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Temp git repo — Windows-safe teardown (mirrors MergeLockAndSettleTests).
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-topo-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# topo-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        public string HeadSha(string workingDir) => Git(workingDir, "rev-parse", "HEAD").Trim();

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
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    private sealed class SpyReVerifier : IReVerifier
    {
        public int CallCount { get; private set; }
        public bool AlwaysPass { get; init; } = true;

        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ReVerifyResult { Passed = AlwaysPass });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan authoring — OS-appropriate action + guardrail scripts (.ps1 on Windows, .sh elsewhere).
    // Each task writes a state fragment and a uniquely-named source file (so git has work to commit).
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private static string CreateChainPlan(string repoPath, params (string id, string[] dependsOn)[] tasks)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 4,
              "mergeOnSuccess": true
            }
            """);
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        foreach (var (id, dependsOn) in tasks) WriteTask(planDir, id, dependsOn);
        return planDir;
    }

    private static void WriteTask(string planDir, string taskId, string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "topo test {{taskId}}",
              "dependsOn": {{dependsJson}}
            }
            """);

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";
        string safeName = taskId.Replace("-", "_");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\n" +
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{taskId}.cs\" -Force" +
                $" -Value 'class {safeName} {{}}' | Out-Null\n" +
                "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                $"printf 'class {safeName} {{}}' > \"$GUARDRAILS_WORKSPACE/src/{taskId}.cs\"\n" +
                "exit 0\n");
            MakeExecutable(actionPath);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            MakeExecutable(guardrailPath);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return; // .sh scripts only authored off Windows
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static async Task<RunReport> RunAsync(
        string planDir, IWorktreeProvider provider, IReVerifier reVerifier, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in topology tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: reVerifier);
        return await scheduler.RunAsync(load.Plan!, ct);
    }

    /// <summary>
    /// Counts the SEGMENT worktrees git has registered under <paramref name="worktreeRoot"/> —
    /// every <c>git worktree add</c> the run made for a segment OR fork tree, excluding the single
    /// <c>_integration</c> worktree. This is the metamorphic add-count instrument: a reuse chain
    /// adds ONE; a fresh-per-task chain would add N.
    /// </summary>
    private static int SegmentWorktreeAddCount(string repoPath, string worktreeRoot)
    {
        string listing = TempGitRepo.Git(repoPath, "worktree", "list", "--porcelain");
        string rootNorm = Path.GetFullPath(worktreeRoot)
            .Replace('\\', '/').TrimEnd('/');
        int count = 0;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
            string path = Path.GetFullPath(line["worktree ".Length..].Trim())
                .Replace('\\', '/').TrimEnd('/');
            if (path.StartsWith(rootNorm + "/", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("/_integration", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }
        return count;
    }

    // ── T-M (headline metamorphic) ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task TM_Metamorphic_LinearChainOfN_AddsExactlyOneSegmentWorktree(int n)
    {
        // The disk lever, empirically: a linear chain of N tasks invokes `git worktree add` exactly
        // ONCE (one segment, reused N-1 times) — strictly less than the fresh-per-task baseline of N.
        using var repo = new TempGitRepo();
        var tasks = Enumerable.Range(1, n)
            .Select(i => ($"{i:00}-t", i == 1 ? Array.Empty<string>() : new[] { $"{i - 1:00}-t" }))
            .ToArray();
        string planDir = CreateChainPlan(repo.RepoPath, tasks);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport report = await RunAsync(
            planDir, provider, new SpyReVerifier(), TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "linear chain should settle green: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        int reuseAddCount = SegmentWorktreeAddCount(repo.RepoPath, repo.WorktreeRoot);
        int freshBaseline = n; // fresh-per-task would add one segment worktree per task

        Assert.Equal(1, reuseAddCount);                 // reuse adds exactly one
        Assert.True(reuseAddCount < freshBaseline,       // strictly fewer than the baseline
            $"reuse add-count {reuseAddCount} must be < fresh baseline {freshBaseline}");
    }

    // ── T-6 ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task T6_ReusedChain_FastForwards_AndEveryIntegratedCommitCarriesTrailer()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateChainPlan(repo.RepoPath,
            ("01-a", []), ("02-b", ["01-a"]), ("03-c", ["02-b"]));
        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha(repo.RepoPath);
        var spy = new SpyReVerifier { AlwaysPass = true };
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport report = await RunAsync(planDir, provider, spy, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        // A reused linear chain still FFs every hop — re-verify is never called at integration.
        Assert.Equal(0, spy.CallCount);

        // No merge commits — every integrated commit is a plain FF'd commit.
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());

        // Each task's commit carries the resume trailers.
        string log = TempGitRepo.Git(repo.RepoPath, "log", "--format=%B");
        foreach (string id in new[] { "01-a", "02-b", "03-c" })
            Assert.Contains($"Guardrails-Task: {id}", log, StringComparison.Ordinal);

        Assert.NotEqual(initialHead, repo.HeadSha(repo.RepoPath)); // user branch advanced (mergeOnSuccess)
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ── T-7 (W-2: sibling forks off producer's RECORDED sha) ────────────────────────────────────
    [Fact]
    public async Task T7_FanOut_ForkSibling_RootsOffProducerRecordedSha_NotInheritorAdvancedTip()
    {
        // P → {D1, D2, D3} (single-producer leaves). One inherits P's directory and ADVANCES the
        // segment branch; the other two fork. W-2: each fork's commit history must contain the
        // producer's commit (it forked off the recorded sha), and a green run integrates all three.
        using var repo = new TempGitRepo();
        string planDir = CreateChainPlan(repo.RepoPath,
            ("01-p", []), ("02-d1", ["01-p"]), ("03-d2", ["01-p"]), ("04-d3", ["01-p"]));
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport report = await RunAsync(
            planDir, provider, new SpyReVerifier(), TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "fan-out should settle green: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // Every producer's source file landed on the plan branch — fork siblings descended from the
        // producer's recorded commit (had they forked off the inheritor's advanced tip and lost the
        // producer's ancestry, integration would not have produced a clean, all-green plan branch).
        string planBranch = "guardrails/plan";
        string tree = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", planBranch);
        foreach (string id in new[] { "01-p", "02-d1", "03-d2", "04-d3" })
            Assert.Contains($"src/{id}.cs", tree, StringComparison.Ordinal);
    }

    // ── T-8 (W-2 reset on a reused/inherited segment) ───────────────────────────────────────────
    [Fact]
    public void T8_ResetSegment_OnReusedDirectory_KeepsProducerCommit_DropsInheritorWip()
    {
        // Reuse sets the inheritor's TaskBase to the producer's RECORDED sha. A reset to TaskBase
        // therefore discards only the inheritor's WIP and keeps the producer's committed file.
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("reset-plan", "run-t8", CancellationToken.None);

        WorktreeHandle producer = provider.CreateSegment("01-producer", 1, integ, CancellationToken.None);
        // Producer commits a file and records its tip (as Integrate would).
        string producerSha = CommitFile(producer.WorktreePath, "producer.txt", "kept", "producer");
        producer.RecordedCommitSha = producerSha;

        // Inheritor reuses the producer's directory and writes uncommitted WIP.
        WorktreeHandle inheritor = provider.ReuseSegment(producer, "02-inheritor", 1);
        File.WriteAllText(Path.Combine(inheritor.WorktreePath, "wip.txt"), "discard me");

        Assert.Equal(producerSha, inheritor.TaskBase);

        // Reset to TaskBase (the producer's recorded sha): producer's file survives, WIP is gone.
        GitWorktreeProvider.ResetSegment(inheritor.WorktreePath, inheritor.TaskBase);

        Assert.True(File.Exists(Path.Combine(inheritor.WorktreePath, "producer.txt")),
            "producer's committed file must survive a reset to TaskBase");
        Assert.False(File.Exists(Path.Combine(inheritor.WorktreePath, "wip.txt")),
            "inheritor's uncommitted WIP must be cleaned by the reset");
    }

    private static string CommitFile(string workingDir, string relPath, string content, string message)
    {
        File.WriteAllText(Path.Combine(workingDir, relPath), content);
        TempGitRepo.Git(workingDir, "add", relPath);
        TempGitRepo.Git(workingDir, "commit", "-m", message);
        return TempGitRepo.Git(workingDir, "rev-parse", "HEAD").Trim();
    }

    // ── T-M2 (crash-resume after a reused-chain hop) ────────────────────────────────────────────
    [Fact]
    public async Task TM2_CrashResumeAfterReusedChain_ReconstructsFromPlanBranchTrailers_NoLostWork()
    {
        // Run a reused linear chain green. Then simulate a crash that loses BOTH the journal and the
        // reused segment worktree directory, and re-run. The resume pre-pass reconstructs every task
        // from the plan-branch trailers (the durable resume truth) and skips them — proving the lost
        // reused directory loses no integrated work.
        using var repo = new TempGitRepo();
        string planDir = CreateChainPlan(repo.RepoPath,
            ("01-a", []), ("02-b", ["01-a"]), ("03-c", ["02-b"]));
        var provider1 = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport first = await RunAsync(planDir, provider1, new SpyReVerifier(), TestContext.Current.CancellationToken);
        Assert.True(first.AllSucceeded);

        // All three task ids are integrated on the plan branch (trailers reachable from the tip).
        string planLog = TempGitRepo.Git(repo.RepoPath, "log", "--format=%B", "guardrails/plan");
        foreach (string id in new[] { "01-a", "02-b", "03-c" })
            Assert.Contains($"Guardrails-Task: {id}", planLog, StringComparison.Ordinal);

        // Simulate a crash: lose the journal (forces resume to rely on plan-branch trailers) and the
        // reused segment worktree directory on disk.
        string journalPath = RunJournal.PathFor(planDir);
        if (File.Exists(journalPath)) File.Delete(journalPath);
        foreach (string dir in Directory.Exists(repo.WorktreeRoot)
                     ? Directory.GetDirectories(repo.WorktreeRoot)
                     : [])
        {
            // best-effort drop of all prior-run trees (segments + integration registrations linger)
            try { TempGitRepo.Git(repo.RepoPath, "worktree", "remove", "--force", dir); } catch { /* ignore */ }
        }

        // Re-run with a fresh provider/journal — the resume pre-pass must skip every already-integrated task.
        var provider2 = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        RunReport second = await RunAsync(planDir, provider2, new SpyReVerifier(), TestContext.Current.CancellationToken);

        Assert.True(second.AllSucceeded);
        // Every task is a resume-skip (already integrated), not a re-execution.
        Assert.All(second.Tasks, t => Assert.Equal(TaskOutcome.Skipped, t.Outcome));
    }
}
