using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests encoding plan 08 §3 / §5.3 / Stage-2 BEFORE the settle
/// refactor exists. All four tests reference not-yet-existing settle plumbing so the project
/// will NOT compile against current code — that compile failure IS the red-bar signal.
/// Do NOT implement the settle here; tests only, in this one file.
///
/// Four scenarios:
/// <list type="bullet">
///   <item><b>MergeLock_IsNetNew</b> — the global serialize-merges <see cref="SemaphoreSlim"/>(1,1) is
///     a net-new field on <see cref="Scheduler"/>, distinct from every <c>object _gate</c> used by
///     <see cref="Scheduler"/>, <see cref="StateManager"/>, and <see cref="RunJournal"/>; and
///     <c>WorkspaceLock</c> is gone (triad teardown).</item>
///   <item><b>FF_Integration_Is_Free_And_Trailer_On_FF_Commit</b> — a linear chain settles via
///     <c>git merge --ff-only</c> with NO re-verify at integration and each commit carrying
///     <c>Guardrails-Task:</c> / <c>Guardrails-Run:</c> trailers.</item>
///   <item><b>NonFF_Union_ReVerifies_B1_FourEffect</b> — two siblings; the second's integration is
///     non-FF and merged bytes fail re-verify; B1 four-effect rollback: journaled needs-human,
///     NO fragment in state.json, <c>mergeSequence</c> NOT consumed, user branch untouched.</item>
///   <item><b>Fragment_Written_Before_Commit</b> (exact name) — the state fragment is written to
///     state.json BEFORE the git integration commit; a commit-before-fragment implementation is
///     REJECTED by this test.</item>
/// </list>
/// </summary>
public sealed class MergeLockAndSettleTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — proven-safe teardown (strips read-only before delete, re-creates dirs after
    // git rm, Windows-portable). Used by tests 1–3 which need a real git repo.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-mls-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# settle-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() =>
            Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha(string workingDir) =>
            Git(workingDir, "rev-parse", "HEAD").Trim();

        /// <summary>Returns true when sha has ≥ 2 parent lines in cat-file output.</summary>
        public bool IsMergeCommit(string workingDir, string sha)
        {
            string raw = Git(workingDir, "cat-file", "-p", sha);
            return raw.Split('\n').Count(l => l.StartsWith("parent ")) >= 2;
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="relPath"/> (with '/' separators),
        /// stages, and commits. Recreates parent dirs first (git prunes empty dirs on Windows).
        /// </summary>
        public string CommitFile(string workingDir, string relPath, string content, string message)
        {
            string fullPath = Path.Combine(workingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            Git(workingDir, "add", relPath);
            Git(workingDir, "commit", "-m", message);
            return HeadSha(workingDir);
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
            // Windows-safe: .git/objects loose files are read-only. Strip all bits before delete.
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
    // SpyReVerifier — records calls and returns a configurable pass/fail result.
    // FF integration must NOT call re-verify; non-FF MUST call it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SpyReVerifier : IReVerifier
    {
        public int CallCount { get; private set; }

        /// <summary>
        /// When true, re-verify returns <c>Passed = true</c> (merged bytes pass).
        /// When false, returns forced failure (merged bytes fail to build).
        /// The FF path must never call this — all calls are non-FF settle attempts.
        /// </summary>
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
                        Reason = "spy: forced failure — merged bytes fail to build (B1 gate test)"
                    }]
                });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CapturingFakeProvider — a no-git IWorktreeProvider that runs actions in a real directory
    // and captures state.json content at the exact moment Integrate() is called.
    // Used by Fragment_Written_Before_Commit to pin the B1 fragment-before-commit order:
    // state.json MUST already contain the fragment when Integrate (the git commit step) runs.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class CapturingFakeProvider : IWorktreeProvider
    {
        private readonly string _workDir;    // real path so actions can run
        private readonly string _stateJson;  // state.json to snapshot at Integrate time
        private readonly List<string> _snaps = new();

        /// <summary>
        /// Snapshots of state.json captured at each <see cref="Integrate"/> call (one per task).
        /// Per B1 fixed order the fragment must already be present at snapshot time — confirming
        /// (1) state-fragment merge before (2) git integration commit.
        /// A commit-before-fragment implementation would leave this snapshot without the fragment.
        /// </summary>
        public IReadOnlyList<string> SnapshotsAtCommit => _snaps;

        public CapturingFakeProvider(string workDir, string stateJson)
        {
            _workDir = workDir;
            _stateJson = stateJson;
        }

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            new()
            {
                IntegrationWorktreePath = _workDir,
                PlanBranchName = $"gr-plan/{planName}",
                OriginalBranch = "main",
                OriginalHeadSha = "0000000000000000000000000000000000000000",
                RunId = runId
            };

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            new()
            {
                WorktreePath = _workDir,   // real path — actions run here
                SegmentBranchName = $"gr-seg/{taskId}",
                TaskBase = "0000000000000000000000000000000000000000",
                RecordedCommitSha = "0000000000000000000000000000000000000000",
                PlanBranchHead = "0000000000000000000000000000000000000000"
            };

        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
        {
            // This is the git integration commit step (B1 step 2).
            // Capture state.json NOW — if B1 step 1 (fragment merge) happened first,
            // the fragment is already here. A commit-before-fragment impl leaves it absent.
            _snaps.Add(File.Exists(_stateJson) ? File.ReadAllText(_stateJson) : "{}");
            return IntegrationResult.FastForward;
        }

        public WorktreeHandle ReuseSegment(WorktreeHandle upstream, string taskId, int attempt) =>
            new()
            {
                WorktreePath = _workDir,
                SegmentBranchName = $"gr-seg/{taskId}",
                TaskBase = upstream.RecordedCommitSha,
                RecordedCommitSha = upstream.RecordedCommitSha,
                PlanBranchHead = upstream.PlanBranchHead
            };

        public WorktreeHandle ForkFromTip(string producerSha, string taskId, int attempt) =>
            new()
            {
                WorktreePath = _workDir,
                SegmentBranchName = $"gr-fork/{taskId}",
                TaskBase = producerSha,
                RecordedCommitSha = producerSha,
                PlanBranchHead = producerSha
            };

        public void Discard(WorktreeHandle handle) { }

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) { }

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            MergeOnSuccessResult.FastForwarded;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Returns true when state.json has a top-level key for <paramref name="taskId"/>.</summary>
    private static bool HasFragment(string planDir, string taskId)
    {
        string stateJson = Path.Combine(planDir, "state", "state.json");
        if (!File.Exists(stateJson)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(stateJson));
        return doc.RootElement.TryGetProperty(taskId, out _);
    }

    /// <summary>
    /// Creates a plan directory INSIDE <paramref name="repoPath"/> with two sibling tasks
    /// (no dependsOn), each producing a state fragment.
    /// <c>maxParallelism: 2</c> enables worktree mode so both tasks run in segments.
    /// </summary>
    private static string CreateSiblingPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        // workspace: ".." = repo root; segment worktrees are checked out from the repo
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

    /// <summary>Creates a linear chain A → B inside <paramref name="repoPath"/>.</summary>
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
              "maxParallelism": 2,
              "mergeOnSuccess": true
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        WriteTaskInRepo(planDir, "01-task-a", []);
        WriteTaskInRepo(planDir, "02-task-b", ["01-task-a"]);
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
              "description": "settle test {{taskId}}",
              "dependsOn": {{dependsJson}}
            }
            """);

        // Action: write a fragment to GUARDRAILS_STATE_OUT and create a file for git to commit.
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

    /// <summary>
    /// Shared harness for tests 2 and 3. Loads <paramref name="planDir"/>, wires the
    /// <paramref name="worktreeProvider"/> and <paramref name="reVerifier"/> into the
    /// Scheduler, and runs. The <c>reVerifier:</c> named parameter is the compile coupling —
    /// it does not exist on the current <see cref="Scheduler"/> constructor.
    /// </summary>
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
            _ => throw new InvalidOperationException("No prompt runners in settle tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry);

        // ── COMPILE ERROR on current code: 'Scheduler' has no 'reVerifier' parameter.
        // This parameter is introduced by the M4 settle refactor (plan 08 §3). ──────────────────
        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: worktreeProvider,
            reVerifier: reVerifier);

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 1 — merge-lock is net-new
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 feasibility-fix-3: the global serialize-merges lock is a NET-NEW
    /// <see cref="SemaphoreSlim"/>(1,1) on <see cref="Scheduler"/> (field <c>_integrationLock</c>),
    /// distinct from the <c>object _gate</c> fields in <see cref="Scheduler"/>,
    /// <see cref="StateManager"/>, and <see cref="RunJournal"/>. The old
    /// <c>WorkspaceLock</c> type (triad teardown) must be gone.
    /// Reflection-only; no git repo required.
    /// </summary>
    [Fact]
    public void MergeLock_IsNetNew_DistinctFromStateAndJournalGates()
    {
        // ── 1. Scheduler._integrationLock is a SemaphoreSlim (net-new serialize-merges lock) ─
        FieldInfo? integLock = typeof(Scheduler).GetField(
            "_integrationLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(integLock);
        Assert.Equal(typeof(SemaphoreSlim), integLock!.FieldType);

        // ── 2. Scheduler._gate is still object (existing readiness / channel guard) ───────────
        FieldInfo? schedulerGate = typeof(Scheduler).GetField(
            "_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(schedulerGate);
        Assert.Equal(typeof(object), schedulerGate!.FieldType);

        // ── 3. The two Scheduler fields have different types — they are DISTINCT locks ─────────
        Assert.NotEqual(integLock.FieldType, schedulerGate.FieldType);

        // ── 4. StateManager._gate is object (not promoted to SemaphoreSlim) ────────────────────
        FieldInfo? stateMgrGate = typeof(StateManager).GetField(
            "_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stateMgrGate);
        Assert.Equal(typeof(object), stateMgrGate!.FieldType);

        // ── 5. RunJournal._gate is object (not promoted to SemaphoreSlim) ────────────────────
        FieldInfo? journalGate = typeof(RunJournal).GetField(
            "_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(journalGate);
        Assert.Equal(typeof(object), journalGate!.FieldType);

        // ── 6. WorkspaceLock is gone (triad teardown, plan 08 M2) ────────────────────────────
        Type? workspaceLockType = typeof(Scheduler).Assembly
            .GetType("Guardrails.Core.Execution.WorkspaceLock");
        Assert.Null(workspaceLockType);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 2 — FF-integration is free + trailer on FF commit
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §3 Stage-2 gate: a linear chain (A → B) settles both tasks via
    /// <c>git merge --ff-only</c>. The re-verifier MUST NOT be called at integration time
    /// (FF forms no new union — re-verify is free), and each FF'd commit MUST carry the
    /// <c>Guardrails-Task:</c> / <c>Guardrails-Run:</c> trailers so resume-by-trailer works.
    ///
    /// The <c>reVerifier:</c> parameter on <see cref="RunWithProviderAsync"/> causes a compile
    /// error on current code — this IS the red-bar compile signal.
    /// </summary>
    [Fact]
    public async Task FF_Integration_Is_Free_And_Trailer_On_FF_Commit()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha(repo.RepoPath);

        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(
            planDir, provider, spyReVerifier,
            TestContext.Current.CancellationToken);

        // ── All tasks must succeed in a clean linear FF settle ────────────────────────────────
        Assert.True(report.AllSucceeded,
            "FF settle: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // ── FF is free: the re-verifier must NOT have been called ─────────────────────────────
        Assert.Equal(0, spyReVerifier.CallCount);

        // ── After successful run the user branch has the task commits (FF'd plan branch) ───────
        string finalHead = repo.HeadSha(repo.RepoPath);
        Assert.NotEqual(initialHead, finalHead);  // user branch advanced

        // No merge commits in the post-run history (FF'd commits are plain commits)
        string mergesOutput = TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H");
        Assert.Empty(mergesOutput.Trim());

        // Both task commits carry the required trailers
        string fullLog = TempGitRepo.Git(repo.RepoPath, "log", "--format=%B%n---END---");
        Assert.Contains("Guardrails-Task:", fullLog, StringComparison.Ordinal);
        Assert.Contains("Guardrails-Run:", fullLog, StringComparison.Ordinal);

        // ── User branch stays on the same named branch (not detached HEAD) ────────────────────
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 3 — non-FF union re-verifies (B1 four-effect)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §3 Stage-2 gate (B1 four-effect): two sibling tasks race to the plan branch.
    /// The first wins the serialize-merges lock and settles via FF (no re-verify). The second
    /// cannot FF (the plan branch has advanced) — it forms a non-FF union, triggering re-verify.
    /// <see cref="SpyReVerifier"/> always returns <c>Passed = false</c> (merged bytes fail to
    /// build). The harness must perform the complete B1 four-effect rollback:
    /// <list type="number">
    ///   <item>Re-verify WAS called (non-FF triggered it).</item>
    ///   <item>No merge commit on the plan branch (<c>git reset --hard preHead</c> ran).</item>
    ///   <item>The failing task is journaled <c>needs-human</c> (not <c>Succeeded</c>).</item>
    ///   <item>NO fragment for the failing task in <c>state.json</c> (fragment rolled back).</item>
    ///   <item><c>mergeSequence</c> NOT consumed for the failing task (counter unchanged).</item>
    ///   <item>User branch untouched (the plan branch rollback must not bleed to user branch).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task NonFF_Union_ReVerifies_B1_FourEffect()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateSiblingPlan(repo.RepoPath);
        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha(repo.RepoPath);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        // AlwaysPass = false: every re-verify call returns failure.
        // FF integration never calls re-verify, so only the non-FF settle triggers the spy.
        var spyReVerifier = new SpyReVerifier { AlwaysPass = false };

        // Record NextMergeSequence BEFORE the run — the failing task must NOT consume one.
        PlanLoadResult preLoad = new PlanLoader().Load(planDir);
        RunJournal preJournal = RunJournal.LoadOrCreate(preLoad.Plan!);
        long mergeSeqBefore = preJournal.Document.NextMergeSequence;

        var (report, _) = await RunWithProviderAsync(
            planDir, provider, spyReVerifier,
            TestContext.Current.CancellationToken);

        // ── Effect 0: re-verify was called (non-FF triggered it) ─────────────────────────────
        Assert.True(spyReVerifier.CallCount > 0,
            "Non-FF integration must invoke the re-verifier — SpyReVerifier was never called.");

        // ── Exactly one task is NeedsHuman (the second to settle), one is Succeeded ───────────
        TaskResult nhTask = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        TaskResult okTask  = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);
        string nhId = nhTask.TaskId;
        string okId = okTask.TaskId;

        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));

        // ── Effect 1: journal records needs-human (not Succeeded) for the failing settle ───────
        Assert.Equal(JournalTaskStatus.NeedsHuman, doc.Tasks[nhId].Status);
        Assert.Equal(JournalTaskStatus.Succeeded,  doc.Tasks[okId].Status);

        // ── Effect 2: NO fragment in state.json for the task whose settle was rolled back ───────
        // B1 rollback: the fragment write is reversed — the needs-human task's key must be absent.
        Assert.False(HasFragment(planDir, nhId),
            $"B1 rollback: fragment for '{nhId}' must NOT be in state.json after a rolled-back settle.");
        Assert.True(HasFragment(planDir, okId),
            $"The successfully settled task '{okId}' must have its fragment in state.json.");

        // ── Effect 3: mergeSequence NOT consumed for the rolled-back task ───────────────────────
        Assert.Null(doc.Tasks[nhId].MergeSequence);
        // Only the FF'd task consumed one sequence number; failed settle consumed none.
        Assert.Equal(mergeSeqBefore + 1, doc.NextMergeSequence);

        // ── Effect 4: user branch untouched ─────────────────────────────────────────────────────
        // Plan-branch rollback must not bleed into the user branch.
        // When the run is not fully successful, MergePlanBranchIntoUserBranch is not called.
        Assert.Equal(initialHead, repo.HeadSha(repo.RepoPath));
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 4 — settle ordering: fragment before commit
    // METHOD NAME IS EXACT — the scenarios-present guardrail greps for this name.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §3 B1 fixed-order gate: pins that the state fragment is written to state.json
    /// BEFORE the git integration commit — the load-bearing ordering decision that prevents
    /// B1 split-brain.
    ///
    /// Fixed success order: (1) state-fragment merge → (2) git integration commit →
    /// (3) journal Succeeded + consume <c>mergeSequence</c>.
    ///
    /// A <b>commit-before-fragment</b> implementation is REJECTED: it creates a window where a
    /// crash after the git commit but before the fragment write leaves the plan branch ahead of
    /// <c>state.json</c> — the harness cannot reconstruct the state from the git commit alone,
    /// causing silent data loss (B1 split-brain). A <b>journal-before-commit</b> implementation
    /// is equally REJECTED: the journal's <c>mergeSequence</c> claim becomes stale if the git
    /// commit never lands.
    ///
    /// <see cref="CapturingFakeProvider"/> captures <c>state.json</c> at the moment
    /// <see cref="IWorktreeProvider.Integrate"/> is called (the git commit step). The assertion
    /// verifies the fragment was ALREADY present — confirming (1) before (2).
    /// </summary>
    [Fact]
    public async Task Fragment_Written_Before_Commit()
    {
        using var plan = new StatePlanBuilder(maxParallelism: 2)
            .AddTask("01-producer", actionBody: ProducerFragmentAction("01-producer"));

        var capturing = new CapturingFakeProvider(plan.PlanDir, plan.StateJsonPath);

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in fragment-ordering test."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: capturing);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "Fragment-ordering test plan must succeed end-to-end.");

        // ── Integrate() was called exactly once (one task, one settle) ───────────────────────
        Assert.Single(capturing.SnapshotsAtCommit);
        string stateAtCommitTime = capturing.SnapshotsAtCommit[0];

        // ── B1 fragment-before-commit assertion ──────────────────────────────────────────────
        // The fragment for '01-producer' must be in state.json at the moment Integrate() is
        // called — i.e. state-fragment merge (step 1) happened BEFORE the git integration
        // commit (step 2). A commit-before-fragment implementation would produce an empty /
        // missing-fragment snapshot here, failing this assertion (the REJECTED order).
        Assert.Contains("\"01-producer\"", stateAtCommitTime, StringComparison.Ordinal);

        // ── mergeSequence was consumed (B1 step 3 completed after the commit) ─────────────────
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.Succeeded, doc.Tasks["01-producer"].Status);
        Assert.NotNull(doc.Tasks["01-producer"].MergeSequence);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Script helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OS-appropriate action-script body that writes a state fragment for
    /// <paramref name="taskId"/> to <c>GUARDRAILS_STATE_OUT</c>.
    /// </summary>
    private static string ProducerFragmentAction(string taskId)
    {
        // Single-quotes around the JSON value: PS/bash both treat the content literally,
        // so the inner double-quotes in the JSON do not need additional escaping.
        string json = "{\"" + taskId + "\": {\"done\": true}}";
        return StatePlanBuilder.UsePowerShell
            ? $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{json}'"
            : $"printf '%s' '{json}' > \"$GUARDRAILS_STATE_OUT\"";
    }
}
