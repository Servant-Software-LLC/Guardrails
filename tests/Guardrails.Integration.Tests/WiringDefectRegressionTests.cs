using System.Diagnostics;
using System.Reflection;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar regression tests for the 11 defects catalogued in plan 08 before any of them are
/// fixed. Every method name matches the plan 08 defect code so the "scenarios-present" guardrail
/// can grep for them. All tests are authored against the CURRENT (un-fixed) source, so the whole
/// class fails on current code — that is the intended red-bar signal.
/// Do NOT fix any defect here; tests only.
/// </summary>
public sealed class WiringDefectRegressionTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — Windows-safe temp repo + worktree root (strips read-only bits before delete).
    // Copied from MergeLockAndSettleTests to keep each test file self-contained.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wdr-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# defect-regression-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string HeadSha(string workingDir) =>
            Git(workingDir, "rev-parse", "HEAD").Trim();

        public string CurrentBranch(string workingDir) =>
            Git(workingDir, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public bool BranchExists(string branchName)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--verify");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add(branchName);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // SpyReVerifier — records calls; configurable pass/fail. Used by B2, C1, B1_1_F1.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SpyReVerifier : IReVerifier
    {
        public int CallCount { get; private set; }
        public bool AlwaysPass { get; init; }

        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(AlwaysPass
                ? new ReVerifyResult { Passed = true }
                : new ReVerifyResult
                {
                    Passed = false,
                    FailedGuardrails = [new GuardrailResult
                    {
                        Name = "spy-re-verify",
                        Passed = false,
                        Reason = "spy: forced failure"
                    }]
                });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan helpers — mirror of MergeLockAndSettleTests helpers for independent test hygiene.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Linear plan A → B inside <paramref name="repoPath"/>, maxParallelism=2.</summary>
    private static string CreateLinearPlan(string repoPath)
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
              "maxParallelism": 2
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        WriteTaskInRepo(planDir, "01-task-a", []);
        WriteTaskInRepo(planDir, "02-task-b", ["01-task-a"]);
        return planDir;
    }

    /// <summary>Sibling plan (no deps) inside <paramref name="repoPath"/>, maxParallelism=2.</summary>
    private static string CreateSiblingPlan(string repoPath)
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
              "maxParallelism": 2
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        WriteTaskInRepo(planDir, "01-task-a", []);
        WriteTaskInRepo(planDir, "02-task-b", []);
        return planDir;
    }

    /// <summary>
    /// Linear plan A → B where B has <c>integrationGate:true</c> and an
    /// <c>integration</c>-scoped guardrail (for C1 terminal-gate test).
    /// </summary>
    private static string CreateLinearPlanWithIntegrationGate(string repoPath)
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
              "maxParallelism": 2
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        WriteTaskInRepo(planDir, "01-task-a", []);
        WriteIntegrationGateTaskInRepo(planDir, "02-gate", ["01-task-a"]);
        return planDir;
    }

    private static void WriteTaskInRepo(string planDir, string taskId, string[] dependsOn)
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
              "description": "defect regression {{taskId}}",
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
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void WriteIntegrationGateTaskInRepo(string planDir, string taskId, string[] dependsOn)
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
              "description": "integration gate {{taskId}}",
              "dependsOn": {{dependsJson}},
              "integrationGate": true
            }
            """);

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\nexit 0\n");

            // Integration-scoped guardrail — the terminal gate must re-run this via IReVerifier
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-integration-check.ps1"),
                "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-integration-check.json"),
                """{"scope": "integration", "description": "whole-repo integration check"}""");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.ps1");
            // Use ps1 extension as a fallback – won't be found unless interpreter override added.
            // On non-Windows, write .sh instead.
            string ext = ".sh";
            string actionSh = Path.Combine(taskDir, "action" + ext);
            File.WriteAllText(actionSh,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n");
            File.SetUnixFileMode(actionSh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            string guardrailSh = Path.Combine(taskDir, "guardrails", "01-integration-check.sh");
            File.WriteAllText(guardrailSh, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailSh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-integration-check.json"),
                """{"scope": "integration", "description": "whole-repo integration check"}""");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // RunWithProviderAsync — wires provider + reVerifier into Scheduler and runs.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(RunReport report, RunJournal journal)> RunWithProviderAsync(
        string planDir,
        IWorktreeProvider worktreeProvider,
        IReVerifier reVerifier,
        CancellationToken ct = default)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in defect regression tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: worktreeProvider,
            reVerifier: reVerifier);

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // B2 — non-FF settle must commit the merged bytes with a Guardrails-Task: trailer
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect B2: after a non-FF merge succeeds and re-verify passes,
    /// <see cref="Scheduler.SettleAsync"/> calls only <c>_journal.RecordSettle</c> and
    /// never commits the staged merge onto the plan branch. The plan branch tip stays at the
    /// first task's FF commit; the second task's <c>Guardrails-Task:</c> trailer never lands.
    /// RED until the Scheduler commits the non-FF merge with a proper trailer.
    /// </summary>
    [Fact]
    public async Task B2_NonFfSettle_CommitsWithTrailer()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateSiblingPlan(repo.RepoPath);
        var spyRv = new SpyReVerifier { AlwaysPass = true };
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(
            planDir, provider, spyRv, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "Run must succeed end-to-end before we can inspect the plan branch log.");

        // After both tasks integrate, the plan branch must have ≥ 2 commits bearing
        // Guardrails-Task: trailers — one FF commit for the first settler and one
        // committed non-FF merge for the second. Currently the non-FF path never commits,
        // so only the FF task's trailer appears → the assertion fails → RED.
        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";
        string planLog = TempGitRepo.Git(repo.RepoPath,
            "log", "--first-parent", "--format=%B", planBranch);

        int trailerCount = planLog.Split('\n')
            .Count(l => l.StartsWith("Guardrails-Task:", StringComparison.Ordinal));

        Assert.True(trailerCount >= 2,
            $"Plan branch '{planBranch}' must have ≥ 2 Guardrails-Task: trailers (one per settled task). " +
            $"Found {trailerCount}. Defect B2: non-FF settle runs re-verify but never commits the merged result.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // C1 — terminal gate must call re-verifier on the final plan-branch HEAD
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect C1: after all tasks succeed, the Scheduler has no terminal gate logic
    /// that calls <see cref="IReVerifier.ReVerifyAsync"/> on the final plan-branch HEAD.
    /// The SpyReVerifier records every call. For a plan with an integration-scoped guardrail
    /// at the sink task, the terminal gate must call the reVerifier at least once.
    /// RED until the Scheduler adds the post-run terminal gate call.
    /// </summary>
    [Fact]
    public async Task C1_TerminalGate_RunsOnFinalHead()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlanWithIntegrationGate(repo.RepoPath);
        var spyRv = new SpyReVerifier { AlwaysPass = true };
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(
            planDir, provider, spyRv, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "Plan must succeed before we can inspect terminal-gate calls.");

        // The terminal gate (after all tasks settle) must call the reVerifier with the
        // integration guardrails on the final plan-branch HEAD.
        // Currently Scheduler.RunAsync has no such call → SpyReVerifier.CallCount = 0 → RED.
        Assert.True(spyRv.CallCount > 0,
            "IReVerifier must be called at least once as the terminal integration gate " +
            "after all tasks succeed (defect C1: no terminal gate logic exists yet).");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // C2 — RecordedCommitSha must be captured by Integrate and reused for downstream forks
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect C2: <see cref="WorktreeHandle.RecordedCommitSha"/> is an <c>init</c>-only
    /// property initialised to <c>""</c> in <see cref="GitWorktreeProvider.CreateSegment"/> and
    /// never updated by <see cref="GitWorktreeProvider.Integrate"/>. A downstream
    /// <see cref="GitWorktreeProvider.ForkFromTip"/> call that passes <c>""</c> as the producer
    /// sha causes a git error and throws. RED until Integrate sets RecordedCommitSha.
    /// </summary>
    [Fact]
    public void C2_RecordedCommitSha_CapturedAndReusedOnProducerSha()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        string runId = "c2-test-" + Guid.NewGuid().ToString("N")[..4];

        var integ = provider.CreateIntegration("c2-plan", runId, CancellationToken.None);
        var producerHandle = provider.CreateSegment("01-producer", attempt: 1, integ, CancellationToken.None);

        // Commit something in the segment so Integrate has real bytes to commit.
        File.WriteAllText(Path.Combine(producerHandle.WorktreePath, "output.cs"), "class Output {}");
        TempGitRepo.Git(producerHandle.WorktreePath, "add", "output.cs");
        TempGitRepo.Git(producerHandle.WorktreePath, "commit", "-m", "Producer output");

        // Integrate: commits the segment onto the plan branch.
        provider.Integrate(producerHandle, integ, CancellationToken.None);

        // RecordedCommitSha must now be set to the commit sha placed on the plan branch.
        // ForkFromTip uses it to start the consumer's segment at exactly the producer's commit.
        // Currently RecordedCommitSha = "" → git worktree add … "" fails → throws → RED.
        WorktreeHandle consumerHandle = provider.ForkFromTip(
            producerHandle.RecordedCommitSha, "02-consumer", attempt: 1);

        Assert.NotEmpty(producerHandle.RecordedCommitSha);
        Assert.True(Directory.Exists(consumerHandle.WorktreePath),
            "ForkFromTip(producerHandle.RecordedCommitSha, …) must succeed " +
            "— RecordedCommitSha must be a valid commit sha set by Integrate (defect C2).");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // B1_1 / F1 — resume must consult ReconcileFromPlanBranch, not just the journal
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defects B1_1 / F1: the Scheduler resume pre-pass only consults the journal
    /// (<c>_journal.StatusOf</c>). When the journal is reset but the plan branch already has
    /// task trailers (from a crashed partial run), the Scheduler re-starts from scratch instead
    /// of calling <see cref="GitWorktreeProvider.ReconcileFromPlanBranch"/> to discover which
    /// tasks are already settled. The observable symptom today: <see cref="GitWorktreeProvider
    /// .CreateIntegration"/> tries <c>git branch guardrails/plan</c> on a repo that already has
    /// that branch, throws <see cref="InvalidOperationException"/>, and the run aborts.
    /// RED until: (a) CreateIntegration is idempotent for existing plan branches, and
    /// (b) the Scheduler calls ReconcileFromPlanBranch during the resume pre-pass.
    /// </summary>
    [Fact]
    public async Task B1_1_F1_Resume_ConsultsReconcileFromPlanBranch_NotJournalOnly()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        var spyRv = new SpyReVerifier { AlwaysPass = true };

        // Phase 1: full successful run — plan branch created, A and B trailers recorded.
        var provider1 = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var (report1, journal1) = await RunWithProviderAsync(
            planDir, provider1, spyRv, TestContext.Current.CancellationToken);
        Assert.True(report1.AllSucceeded, "Phase 1 must fully succeed.");

        // Simulate crash window: reset both tasks in the journal (as if the journal was lost),
        // but the plan branch keeps its trailers (git is durable).
        journal1.ResetTask("01-task-a");
        journal1.ResetTask("02-task-b");

        // Phase 2: resume. Scheduler must call ReconcileFromPlanBranch to discover that both
        // tasks are already settled on the plan branch and skip them. Currently, CreateIntegration
        // tries to create the already-existing 'guardrails/plan' branch → InvalidOperationException.
        var provider2 = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var (report2, _) = await RunWithProviderAsync(
            planDir, provider2, spyRv, TestContext.Current.CancellationToken);

        Assert.True(report2.AllSucceeded,
            "Resume after journal reset must reconcile from the plan branch and report all " +
            "tasks as succeeded/skipped. Defect B1_1/F1: CreateIntegration throws on the " +
            "existing plan branch instead of reconciling.");

        Assert.True(report2.Tasks.All(t => t.IsGreen),
            "All tasks must be green (Succeeded or Skipped) on plan-branch-reconciled resume.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // F2 — retry must call ResetForRetry so attempt 2 starts clean
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect F2: <see cref="TaskExecutor.ExecuteAsync"/>'s retry loop does NOT call
    /// <see cref="GitWorktreeProvider.ResetForRetry"/> between attempts. Attempt 2 therefore sees
    /// uncommitted WIP files written by attempt 1 (the segment working tree is not cleaned).
    /// The action uses <c>GUARDRAILS_ATTEMPT</c> to distinguish attempts: attempt 1 writes
    /// <c>wip.txt</c> in the segment and exits 1; attempt 2 exits 0 only if <c>wip.txt</c> is
    /// gone (cleaned by reset). Currently wip.txt survives → attempt 2 exits 1 → NeedsHuman.
    /// RED until the retry loop calls ResetForRetry before re-dispatching.
    /// </summary>
    [Fact]
    public async Task F2_Retry_ResetsSegment_Attempt2DoesNotSeeAttempt1Wip()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(planDir);
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

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        string taskDir = Path.Combine(planDir, "tasks", "01-retry-task");
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        // task.json: retries: 1 so the task gets 2 total attempts.
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            {
              "description": "F2 retry reset test",
              "dependsOn": [],
              "retries": 1
            }
            """);

        // Action: attempt 1 (GUARDRAILS_ATTEMPT=1) writes wip.txt + exits 1.
        // Attempt 2: if wip.txt present → F2 defect → exit 1; if absent (correctly cleaned) → exit 0.
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                """
                $wip = "$env:GUARDRAILS_WORKSPACE\wip.txt"
                if ([int]$env:GUARDRAILS_ATTEMPT -eq 1) {
                    Set-Content -Path $wip -Value "wip from attempt 1"
                    exit 1
                }
                if (Test-Path $wip) {
                    Write-Output "F2 DEFECT: wip.txt survived from attempt 1 (ResetForRetry not called)"
                    exit 1
                }
                exit 0
                """);
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                """
                #!/usr/bin/env bash
                wip="$GUARDRAILS_WORKSPACE/wip.txt"
                if [ "$GUARDRAILS_ATTEMPT" = "1" ]; then
                    echo "wip from attempt 1" > "$wip"
                    exit 1
                fi
                if [ -f "$wip" ]; then
                    echo "F2 DEFECT: wip.txt survived from attempt 1 (ResetForRetry not called)"
                    exit 1
                fi
                exit 0
                """);
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var spyRv = new SpyReVerifier { AlwaysPass = true };
        var (report, _) = await RunWithProviderAsync(
            planDir, provider, spyRv, TestContext.Current.CancellationToken);

        // With F2 fix: attempt 2 starts on a clean segment (wip.txt absent) → exits 0 → AllSucceeded.
        // Without fix (current): wip.txt survives across retry → attempt 2 exits 1 →
        //   budget exhausted → NeedsHuman → AllSucceeded = false → RED.
        Assert.True(report.AllSucceeded,
            "Attempt 2 must see a clean segment (wip.txt absent — cleaned by ResetForRetry). " +
            "Defect F2: the retry loop does not call ResetForRetry, so wip.txt survives.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // WS_1 — WriteScope.Normalize must treat dotted directories like .github as directories
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect WS_1: <see cref="WriteScope.Normalize"/> tests whether the last path
    /// segment contains a <c>.</c> to decide "file vs directory". Dotted directories like
    /// <c>.github</c> have a <c>.</c> in the last segment, so they are incorrectly treated as
    /// literal file paths rather than directories (which should expand to <c>.github/**</c>).
    /// RED until Normalize is fixed to recognise dotted directories.
    /// </summary>
    [Fact]
    public void WS_1_DottedDirScope()
    {
        // ".github" is a directory — any path under it should be in scope.
        // Normalize(".github") must produce ".github/**" so child paths match.
        // Current: lastSeg=".github" has '.' → treated as file literal → MatchPath fails → false → RED.
        IReadOnlyList<string> scope = [".github"];
        bool inScope = WriteScope.IsInScope(".github/workflows/ci.yml", scope);

        Assert.True(inScope,
            "WriteScope must treat '.github' as a DIRECTORY (normalise to '.github/**'), " +
            "not a file literal — dotted directories like .github are common in repos. " +
            "Defect WS_1: Normalize uses '.' in last segment to distinguish files, " +
            "but '.github' is a directory, not a file.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // WS_2 — WriteScopeCheck.RunGit must fail CLOSED on git error
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect WS_2: <see cref="WriteScopeCheck.RunGit"/> does not check the git process
    /// exit code. When git fails (e.g. bad repository, bad sha), stdout is empty and the method
    /// returns <c>""</c>. <see cref="WriteScopeCheck.Check"/> then iterates over zero diff lines
    /// and finds no offending paths → <c>Passed = true</c>. A git error silently "passes" the
    /// scope check — the harness fails OPEN. RED until RunGit checks the exit code and
    /// propagates the error so Check returns <c>Passed = false</c>.
    /// </summary>
    [Fact]
    public void WS_2_GitError_FailsClosed()
    {
        // Run the scope check in a valid git repo but with a SHA that doesn't exist.
        // Using a real repo avoids the slow directory-walk that "not a git repo" causes on Windows
        // (the null SHA is treated specially and can hang for minutes); an unknown SHA fails in
        // milliseconds with "fatal: ambiguous argument … unknown revision".
        // The defect (RunGit ignores exit code → silently passes) is the same in both cases.
        using var repo = new TempGitRepo();

        IReadOnlyList<string> scope = ["src/**"];
        WriteScopeCheckResult result = WriteScopeCheck.Check(
            repo.RepoPath,
            "cafebabecafebabecafebabecafebabecafebabe",
            scope);

        Assert.False(result.Passed,
            "A git error during the scope check (bad sha) must yield Passed=false (fail CLOSED). " +
            "Defect WS_2: WriteScopeCheck.RunGit ignores exit code — on git failure stdout is empty, " +
            "no offending paths are found, and the check silently passes.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // F7 — maxParallelism > 1 with no provider must be refused or clamped
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect F7: when <c>worktreeProvider</c> is null but <c>maxParallelism &gt; 1</c>,
    /// the <see cref="Scheduler"/> silently runs multiple workers that share the single workspace,
    /// creating an undetected race condition. The Scheduler must either refuse (throw) or clamp the
    /// effective <c>_maxParallelism</c> to 1. RED until one of those guards is added.
    /// </summary>
    [Fact]
    public void F7_MaxParallelismGtOne_NoProvider_RefusedOrClamped()
    {
        // Plan with maxParallelism=2 and NO worktreeProvider.
        using var builder = new StatePlanBuilder(maxParallelism: 2)
            .AddTask("01-a")
            .AddTask("02-b");

        PlanLoadResult load = new PlanLoader().Load(builder.PlanDir);
        Assert.NotNull(load.Plan);

        var journal = RunJournal.LoadOrCreate(load.Plan!);
        new StateManager(load.Plan!.PlanDirectory).Initialize();
        var map = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("no prompt runners"));
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), map,
            new StateManager(load.Plan!.PlanDirectory), journal, IRunObserver.Null, registry);

        // Construct with worktreeProvider: null (the defect scenario).
        // The fix: either throw InvalidOperationException here OR clamp _maxParallelism to 1.
        bool refused = false;
        Scheduler? scheduler = null;
        try
        {
            scheduler = new Scheduler(load.Plan!, executor, journal, worktreeProvider: null);
            refused = false;
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        if (!refused)
        {
            // Not refused: must be clamped. Check the effective max parallelism via reflection.
            var maxField = typeof(Scheduler).GetField(
                "_maxParallelism", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(maxField);
            int effectiveMax = (int)maxField!.GetValue(scheduler!)!;

            Assert.True(effectiveMax == 1,
                $"When worktreeProvider is null and plan.Config.MaxParallelism = 2, " +
                $"Scheduler._maxParallelism must be clamped to 1 (got {effectiveMax}). " +
                "Defect F7: currently neither refusal nor clamping happens — " +
                "the scheduler silently runs with shared-workspace concurrency.");
        }
        // If refused: the guard threw, which satisfies F7 — test passes.
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // F3 — RunReset.Fresh must prune stale guardrails/* branches
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect F3: <see cref="RunReset.Fresh"/> deletes runtime state files
    /// (<c>run.json</c>, <c>state.json</c>, <c>logs/</c>, etc.) but does NOT prune stale
    /// <c>guardrails/*</c> git branches or their associated worktrees. After a crash, stale
    /// segment branches from the crashed run survive a <c>guardrails reset --fresh</c>.
    /// RED until Fresh also prunes stale guardrails/* branches from the repo.
    /// </summary>
    [Fact]
    public void F3_Fresh_PrunesStaleWorktreesAndBranches()
    {
        using var repo = new TempGitRepo();

        // Create a stale segment branch (left behind by a prior crashed run).
        string staleBranch = "guardrails/plan/01-task-a/attempt-1";
        TempGitRepo.Git(repo.RepoPath, "branch", staleBranch);
        Assert.True(repo.BranchExists(staleBranch), "Pre-condition: stale branch must exist.");

        // Plan lives inside the repo; workspace ".." points at the repo root.
        string planDir = CreateLinearPlan(repo.RepoPath);

        // RunReset.Fresh must prune stale guardrails/* branches and worktrees.
        RunReset.Fresh(planDir);

        // Stale branch must be gone.
        // Current: Fresh only deletes state/ files, never touches git refs → branch survives → RED.
        Assert.False(repo.BranchExists(staleBranch),
            $"RunReset.Fresh must prune stale segment branch '{staleBranch}'. " +
            "Defect F3: Fresh currently does not touch git references at all.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // F4 — dirty user working tree at MergePlanBranchIntoUserBranch must halt
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 defect F4: <see cref="GitWorktreeProvider.MergePlanBranchIntoUserBranch"/> runs
    /// <c>git merge --ff-only</c> without first checking whether the working tree is dirty.
    /// When the plan branch only adds new files (no conflict with the user's uncommitted work),
    /// the FF merge succeeds silently, interleaving the user's WIP with the plan's output.
    /// RED until MergePlanBranchIntoUserBranch checks <c>git status --porcelain</c> and refuses
    /// to merge when the working tree is dirty.
    /// </summary>
    [Fact]
    public void F4_DirtyUserTree_AtMerge_HaltsToNeedsHuman()
    {
        using var repo = new TempGitRepo();

        // Create the plan branch with a commit that adds a NEW file (no conflict with README.md).
        string userBranch = repo.CurrentBranch(repo.RepoPath);
        string initialHead = repo.HeadSha(repo.RepoPath);

        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", "guardrails/plan");
        File.WriteAllText(Path.Combine(repo.RepoPath, "new-feature.cs"), "class NewFeature {}");
        TempGitRepo.Git(repo.RepoPath, "add", "new-feature.cs");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m",
            "Guardrails-Task: 01-task\nGuardrails-Run: test-run");
        TempGitRepo.Git(repo.RepoPath, "checkout", userBranch);

        // Dirty the working tree: modify README.md without staging.
        // The FF merge of guardrails/plan does NOT conflict with README.md (it only adds new-feature.cs),
        // so git allows it even though the working tree is dirty — that is the F4 defect.
        File.AppendAllText(Path.Combine(repo.RepoPath, "README.md"), "\n# user's in-progress work");

        var integ = new IntegrationHandle
        {
            IntegrationWorktreePath = repo.RepoPath,
            PlanBranchName = "guardrails/plan",
            OriginalBranch = userBranch,
            OriginalHeadSha = initialHead,
            RunId = "test-run"
        };

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        // Must NOT fast-forward when the working tree is dirty.
        // Current: FF succeeds (dirty README doesn't conflict with new-feature.cs) → FastForwarded → RED.
        Assert.True(result != MergeOnSuccessResult.FastForwarded,
            "MergePlanBranchIntoUserBranch must refuse (return non-FastForwarded) when the " +
            "working tree is dirty. Defect F4: no pre-merge dirty-tree check exists.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // F5 — unrecognised guardrail scope must yield GR2021 (regression gate for task 24)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression gate for plan 08 task 24: a guardrail sidecar with an unrecognised
    /// <c>scope</c> value must produce diagnostic
    /// <see cref="DiagnosticCodes.InvalidGuardrailScopeValue"/> (GR2021) at validation time,
    /// not silently degrade to local at runtime. This test is expected to PASS on current code
    /// (task 24 implemented GR2021); it guards against future regressions.
    /// </summary>
    [Fact]
    public void F5_UnrecognizedGuardrailScope_YieldsGr2021()
    {
        using var plan = new StatePlanBuilder(maxParallelism: 2)
            .AddTask("01-gate");

        // Overwrite the guardrail sidecar with an invalid scope value.
        string guardrailsDir = Path.Combine(plan.PlanDir, "tasks", "01-gate", "guardrails");
        string sidecarPath = Path.Combine(guardrailsDir, StatePlanBuilder.GuardrailFileName[..^4] + ".json");
        File.WriteAllText(sidecarPath,
            """{"scope": "bogus-invalid", "description": "regression gate for GR2021"}""");

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        IReadOnlyList<Diagnostic> validatorDiags = load.Plan is not null
            ? new PlanValidator().Validate(load.Plan)
            : [];
        bool hasGr2021 = load.Diagnostics.Concat(validatorDiags)
            .Any(d => d.Code == DiagnosticCodes.InvalidGuardrailScopeValue);

        Assert.True(hasGr2021,
            "A guardrail sidecar with scope:\"bogus-invalid\" must produce GR2021 " +
            "(InvalidGuardrailScopeValue) at validate time. " +
            "This is the regression gate for task-24's ValidateGuardrailScopeValues check.");
    }
}
