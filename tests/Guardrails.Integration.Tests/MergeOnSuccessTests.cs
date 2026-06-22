using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests encoding plan 08 §5 / Stage-2 end-of-run delivery (mergeOnSuccess)
/// BEFORE <c>--merge-on-success</c> is implemented. Every test references
/// <see cref="RunReport.MergeOnSuccessOutcome"/> (new property, does not exist) and/or
/// <see cref="MergeOnSuccessResult.Conflict"/> (new enum value, does not exist) — so the project
/// WILL NOT compile against current code. That compile failure IS the red-bar signal.
/// Do NOT implement the hook here; tests only, in this one file.
///
/// Four scenarios (SSOT §5.3 / plan 08 §5):
/// <list type="bullet">
///   <item><b>DefaultOff_LeavesUserBranchAtOriginalHead</b> — <c>mergeOnSuccess</c> absent (default
///     false): no merge attempted; <c>MergeOnSuccessOutcome</c> is null; provider not called.</item>
///   <item><b>MergeOnSuccess_FF_AdvancesUserBranchToTip</b> — <c>mergeOnSuccess: true</c>, no
///     concurrent user advance: FF-merge; no merge commit; user HEAD at plan tip.</item>
///   <item><b>MergeOnSuccess_ConflictingAdvance_NeedsHuman_PlanBranchIntact</b> — <c>mergeOnSuccess:
///     true</c>, user branch advanced mid-run with a conflicting commit: AI-merge withheld;
///     <c>Conflict</c> outcome; plan branch intact; user branch untouched by the harness.</item>
///   <item><b>MergeOnSuccess_SkippedWhenRunFails</b> — <c>mergeOnSuccess: true</c> but a task
///     fails: merge not attempted; <c>MergeOnSuccessOutcome</c> is null.</item>
/// </list>
/// </summary>
public sealed class MergeOnSuccessTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — proven-safe teardown (strips read-only before delete, re-creates dirs after
    // git rm, Windows-portable). Used by tests 2 and 3 which need a real git repo.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-mos-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# merge-on-success test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() =>
            Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() =>
            Git(RepoPath, "rev-parse", "HEAD").Trim();

        /// <summary>True when <paramref name="branchName"/> exists in the repo.</summary>
        public bool BranchExists(string branchName)
        {
            try { Git(RepoPath, "rev-parse", "--verify", branchName); return true; }
            catch (InvalidOperationException) { return false; }
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
    // AlwaysPassReVerifier — stub for the IReVerifier seam (plan-branch union re-verify).
    // The single-task FF plan in tests 2 and 3 never invokes re-verify; this stub satisfies
    // the Scheduler constructor's reVerifier: parameter without real guardrail processes.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TrackingFakeProvider — wraps FakeWorktreeProvider and counts calls to
    // MergePlanBranchIntoUserBranch. Used by tests 1 and 4 (no real git repo needed) to
    // confirm the merge step was not attempted.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TrackingFakeProvider : IWorktreeProvider
    {
        private readonly FakeWorktreeProvider _inner = new();

        public int MergePlanBranchIntoUserBranchCallCount { get; private set; }

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            _inner.CreateIntegration(planName, runId, ct);

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            _inner.CreateSegment(taskId, attempt, integ, ct);

        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
            _inner.ReuseSegment(upstreamSegment, taskId, attempt);

        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
            _inner.ForkFromTip(producerRecordedSha, taskId, attempt);

        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
            _inner.Integrate(segment, integ, ct);

        public void Discard(WorktreeHandle handle) => _inner.Discard(handle);

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
            _inner.PruneOrphans(liveTaskIds, integ);

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
        {
            MergePlanBranchIntoUserBranchCallCount++;
            return _inner.MergePlanBranchIntoUserBranch(integ, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // AdvanceUserBranchBeforeMergeDecorator — wraps a real IWorktreeProvider and, inside
    // MergePlanBranchIntoUserBranch, commits a conflicting change to the user's original branch
    // BEFORE delegating to the inner provider. This simulates a concurrent user advance that
    // causes a conflict at the merge-on-success boundary (SSOT §5.3 conflict path).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class AdvanceUserBranchBeforeMergeDecorator : IWorktreeProvider
    {
        private readonly IWorktreeProvider _inner;
        private readonly string _repoPath;
        private readonly string _conflictRelPath;
        private readonly string _conflictContent;

        /// <summary>
        /// The HEAD sha the user's branch is on AFTER the simulated concurrent advance commit.
        /// The test asserts <c>repo.HeadSha() == UserBranchHeadAfterAdvance</c> after the run
        /// to confirm the harness did not overwrite or advance the user's branch.
        /// </summary>
        public string? UserBranchHeadAfterAdvance { get; private set; }

        /// <summary>
        /// The plan branch name from the <see cref="IntegrationHandle"/> at merge time,
        /// captured so the test can assert the branch still exists after the conflict.
        /// </summary>
        public string? PlanBranchName { get; private set; }

        public AdvanceUserBranchBeforeMergeDecorator(
            IWorktreeProvider inner,
            string repoPath,
            string conflictRelPath,
            string conflictContent)
        {
            _inner = inner;
            _repoPath = repoPath;
            _conflictRelPath = conflictRelPath;
            _conflictContent = conflictContent;
        }

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            _inner.CreateIntegration(planName, runId, ct);

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            _inner.CreateSegment(taskId, attempt, integ, ct);

        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
            _inner.ReuseSegment(upstreamSegment, taskId, attempt);

        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
            _inner.ForkFromTip(producerRecordedSha, taskId, attempt);

        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
            _inner.Integrate(segment, integ, ct);

        public void Discard(WorktreeHandle handle) => _inner.Discard(handle);

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
            _inner.PruneOrphans(liveTaskIds, integ);

        public void RollbackMerge(IntegrationHandle integ, CancellationToken ct) =>
            _inner.RollbackMerge(integ, ct);

        /// <summary>
        /// Commits a conflicting change to the user's branch on <paramref name="_repoPath"/>
        /// (the <c>src/shared.txt</c> file that the plan task also wrote) and then delegates to
        /// the inner <see cref="IWorktreeProvider.MergePlanBranchIntoUserBranch"/>. Because both
        /// the plan branch and the user's branch added <see cref="_conflictRelPath"/> from the
        /// same base commit with different content, the merge produces a real git conflict,
        /// forcing the future implementation to return <see cref="MergeOnSuccessResult.Conflict"/>.
        /// </summary>
        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
        {
            PlanBranchName = integ.PlanBranchName;

            // Simulate the user committing a conflicting file to their branch mid-run,
            // immediately before the end-of-run merge step executes.
            string fullPath = Path.Combine(
                _repoPath, _conflictRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, _conflictContent);
            TempGitRepo.Git(_repoPath, "add", _conflictRelPath);
            TempGitRepo.Git(_repoPath, "commit", "-m", "user: concurrent conflicting advance");
            UserBranchHeadAfterAdvance = TempGitRepo.Git(_repoPath, "rev-parse", "HEAD").Trim();

            return _inner.MergePlanBranchIntoUserBranch(integ, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan helpers for git-based tests (2 and 3): a single-task plan inside repoPath at
    // <repoPath>/plan/ with workspace: ".." and maxParallelism: 2 (activates worktree mode).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a single-task plan inside <paramref name="repoPath"/> at
    /// <c>&lt;repoPath&gt;/plan/</c> with <c>workspace: ".."</c> pointing to the repo root.
    /// <c>maxParallelism: 2</c> activates worktree mode so the task runs in a real git segment.
    /// The task action writes <paramref name="taskFile"/> (relative to the segment worktree root)
    /// with <paramref name="taskFileContent"/> and emits a state fragment.
    /// </summary>
    private static string CreatePlanInRepo(
        string repoPath,
        bool mergeOnSuccess,
        string taskFile = "src/app.cs",
        string taskFileContent = "class App {}",
        bool guardrailFails = false)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "mergeOnSuccess": {{(mergeOnSuccess ? "true" : "false")}},
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGitTask(planDir, "01-task", taskFile, taskFileContent, guardrailFails);
        return planDir;
    }

    private static void WriteGitTask(
        string planDir,
        string taskId,
        string taskFile,
        string taskFileContent,
        bool guardrailFails)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{"description": "mos-test {{taskId}}", "dependsOn": []}""");

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";

        if (OperatingSystem.IsWindows())
        {
            // Windows: backslash separators for the file path in the PS script.
            string psPath = taskFile.Replace("/", "\\");
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\n" +
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\{psPath}\" -Force -Value '{taskFileContent}' | Out-Null\n" +
                "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                guardrailFails ? "Write-Output 'deliberate guardrail failure'; exit 1\n" : "exit 0\n");
        }
        else
        {
            // Unix: forward-slash separators already correct for bash.
            string taskParentDir = taskFile.Contains('/')
                ? taskFile[..taskFile.LastIndexOf('/')]
                : "";
            string mkdirLine = string.IsNullOrEmpty(taskParentDir)
                ? ""
                : $"mkdir -p \"$GUARDRAILS_WORKSPACE/{taskParentDir}\"\n";

            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                mkdirLine +
                $"printf '%s' '{taskFileContent}' > \"$GUARDRAILS_WORKSPACE/{taskFile}\"\n" +
                "exit 0\n");
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath,
                guardrailFails
                    ? "#!/usr/bin/env bash\necho 'deliberate guardrail failure'; exit 1\n"
                    : "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Run helper — shared across all four tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(RunReport report, RunJournal journal)> RunWithProviderAsync(
        string planDir,
        IWorktreeProvider worktreeProvider,
        CancellationToken ct = default)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in merge-on-success tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: worktreeProvider,
            reVerifier: new AlwaysPassReVerifier());

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 1 — default OFF: no merge attempted when mergeOnSuccess is absent
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §5.3 default-OFF gate: when <c>mergeOnSuccess</c> is absent from
    /// <c>guardrails.json</c> (defaults to false), a wholly-green run leaves the plan branch
    /// intact for the user to review — no end-of-run merge is attempted.
    ///
    /// <c>report.MergeOnSuccessOutcome</c> does not exist on the current <see cref="RunReport"/>
    /// — referencing it here IS the compile-fail red-bar signal. After task 22 implements the
    /// feature this property will be null when no merge is attempted (default OFF).
    /// </summary>
    [Fact]
    public async Task DefaultOff_LeavesUserBranchAtOriginalHead()
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-mos-off-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planDir);
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

            // No mergeOnSuccess key → defaults to false.
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 2
                }
                """);

            string taskDir = Path.Combine(planDir, "tasks", "01-green-task");
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """{"description": "default-off test task", "dependsOn": []}""");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\n");
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
            }
            else
            {
                string ap = Path.Combine(taskDir, "action.sh");
                File.WriteAllText(ap, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(ap,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                string gp = Path.Combine(taskDir, "guardrails", "01-check.sh");
                File.WriteAllText(gp, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(gp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            var tracking = new TrackingFakeProvider();
            var (report, _) = await RunWithProviderAsync(planDir, tracking, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded, "DefaultOff: expected all tasks to succeed.");

            // ── COMPILE FAIL on current code: RunReport has no MergeOnSuccessOutcome property ──
            // Default OFF: no merge was attempted, so the outcome is null.
            Assert.Null(report.MergeOnSuccessOutcome);

            // The provider's merge method must NOT have been called at all.
            Assert.Equal(0, tracking.MergePlanBranchIntoUserBranchCallCount);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 2 — mergeOnSuccess: true + FF: user branch advances to plan tip, no merge commit
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §5.3 fast-forward delivery: with <c>mergeOnSuccess: true</c> and no concurrent
    /// advance on the user's branch, a wholly-green run delivers the plan branch
    /// (<c>guardrails/plan</c>) into the user's original branch via <c>git merge --ff-only</c>.
    ///
    /// Key assertions (SSOT §5.3 run-end delivery):
    /// <list type="bullet">
    ///   <item><c>report.MergeOnSuccessOutcome == MergeOnSuccessResult.FastForwarded</c>
    ///     (COMPILE FAIL: <c>MergeOnSuccessOutcome</c> does not exist on current
    ///     <see cref="RunReport"/>).</item>
    ///   <item>No merge commit in git history — FF preserves linear history.</item>
    ///   <item>User's branch HEAD has advanced beyond the initial commit.</item>
    ///   <item>User stays on the same named branch (no detached HEAD).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_FF_AdvancesUserBranchToTip()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // mergeOnSuccess: true; task writes src/app.cs in the segment worktree.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: true,
            taskFile: "src/app.cs", taskFileContent: "class App {}");
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(
            planDir, provider, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "FF test: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // ── COMPILE FAIL on current code: RunReport has no MergeOnSuccessOutcome property ──
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);

        // ── User's branch has advanced beyond the initial HEAD ───────────────────────────────
        string finalHead = repo.HeadSha();
        Assert.NotEqual(initialHead, finalHead);

        // ── No merge commit (FF produces plain commits, not merge commits) ───────────────────
        string mergesOutput = TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H");
        Assert.Empty(mergesOutput.Trim());

        // ── User stays on the same named branch (not detached HEAD) ─────────────────────────
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 3 — conflict: AI-merge withheld, plan branch intact, user branch untouched
    // METHOD NAME IS EXACT — the scenarios-present guardrail greps for this name.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §5.3 AI-merge-withheld gate (SSOT §5.3 conflict path): with
    /// <c>mergeOnSuccess: true</c>, when the user's branch advances mid-run with a commit
    /// that conflicts with the plan's work, the harness must NOT AI-resolve the conflict — it
    /// halts to needs-human with the plan branch intact and the user's branch untouched.
    ///
    /// <see cref="AdvanceUserBranchBeforeMergeDecorator"/> injects a conflicting commit on
    /// <c>src/shared.txt</c> (the same file the plan task wrote) at merge time, creating a
    /// real git conflict. Both sides added the file from the same base commit with different
    /// content — the classic "both added" conflict.
    ///
    /// Key assertions:
    /// <list type="bullet">
    ///   <item>All plan TASKS succeed — the conflict is in the end-of-run merge step, not a task.
    ///   </item>
    ///   <item><c>report.MergeOnSuccessOutcome == MergeOnSuccessResult.Conflict</c>
    ///     (COMPILE FAIL: <c>Conflict</c> does not exist in current
    ///     <see cref="MergeOnSuccessResult"/> enum).</item>
    ///   <item>Plan branch (<c>guardrails/plan</c>) is still intact — not deleted or corrupted.
    ///   </item>
    ///   <item>User's branch HEAD equals the concurrent-advance sha — the harness never
    ///     force-overwrote or merged into it (no force-overwrite, no AI-resolve).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_ConflictingAdvance_NeedsHuman_PlanBranchIntact()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();

        // Plan task writes src/shared.txt = "plan: result" in the segment worktree.
        // The decorator will also commit src/shared.txt = "user: concurrent content" to the
        // user's original branch just before the merge step — creating a "both added" conflict.
        const string conflictRelPath = "src/shared.txt";
        const string planContent = "plan: result";
        const string userContent = "user: concurrent conflicting content";

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: true,
            taskFile: conflictRelPath, taskFileContent: planContent);

        var innerProvider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var decorator = new AdvanceUserBranchBeforeMergeDecorator(
            innerProvider, repo.RepoPath, conflictRelPath, userContent);

        var (report, _) = await RunWithProviderAsync(
            planDir, decorator, TestContext.Current.CancellationToken);

        // ── All plan tasks must succeed — the conflict is in the end-of-run merge step ───────
        Assert.True(report.AllSucceeded,
            "Conflict test: all plan tasks must succeed; the merge-on-success step (not a task) fails. " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // ── COMPILE FAIL: MergeOnSuccessResult.Conflict does not exist in current enum ───────
        // AI-merge is withheld at the merge-on-success boundary (SSOT §5.3) — the harness
        // halts to needs-human without auto-resolving the conflict.
        Assert.Equal(MergeOnSuccessResult.Conflict, report.MergeOnSuccessOutcome);

        // ── Plan branch must be INTACT after the conflict ────────────────────────────────────
        Assert.NotNull(decorator.PlanBranchName);
        Assert.True(repo.BranchExists(decorator.PlanBranchName!),
            $"Plan branch '{decorator.PlanBranchName}' must remain intact after a merge-on-success conflict.");

        // ── User's branch must be UNTOUCHED by the harness ───────────────────────────────────
        // The harness detected a conflict, aborted the merge, and left the user's branch at the
        // concurrent-advance sha (the sha the decorator created). It must not have moved past it.
        Assert.NotNull(decorator.UserBranchHeadAfterAdvance);
        Assert.Equal(decorator.UserBranchHeadAfterAdvance, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 4 — skipped when run fails: mergeOnSuccess: true but a task fails → no merge
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §5.3 green-run gate: even with <c>mergeOnSuccess: true</c> configured, when
    /// at least one task fails the run is not wholly green, so the harness must NOT attempt
    /// the end-of-run merge into the user's branch — the plan branch stays intact for the user
    /// to examine and fix.
    ///
    /// <c>report.MergeOnSuccessOutcome</c> does not exist on the current <see cref="RunReport"/>
    /// — referencing it here IS the compile-fail red-bar signal.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_SkippedWhenRunFails()
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-mos-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planDir);
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

            // mergeOnSuccess: true, but the task's guardrail always fails.
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "mergeOnSuccess": true,
                  "defaultRetries": 0,
                  "maxParallelism": 2
                }
                """);

            string taskDir = Path.Combine(planDir, "tasks", "01-failing-task");
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """{"description": "task that always fails guardrail", "dependsOn": []}""");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\n");
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    "Write-Output 'deliberate guardrail failure'; exit 1\n");
            }
            else
            {
                string ap = Path.Combine(taskDir, "action.sh");
                File.WriteAllText(ap, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(ap,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                string gp = Path.Combine(taskDir, "guardrails", "01-check.sh");
                File.WriteAllText(gp, "#!/usr/bin/env bash\necho 'deliberate guardrail failure'; exit 1\n");
                File.SetUnixFileMode(gp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            var tracking = new TrackingFakeProvider();
            var (report, _) = await RunWithProviderAsync(planDir, tracking, TestContext.Current.CancellationToken);

            Assert.False(report.AllSucceeded,
                "SkippedWhenRunFails: expected at least one task to fail.");

            // ── COMPILE FAIL on current code: RunReport has no MergeOnSuccessOutcome property ──
            // No merge was attempted because the run was not wholly green.
            Assert.Null(report.MergeOnSuccessOutcome);

            // The provider's merge method must NOT have been called when the run failed.
            Assert.Equal(0, tracking.MergePlanBranchIntoUserBranchCallCount);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 5 (F11a) — the CLI --merge-on-success flag turns on user-branch delivery even when
    // guardrails.json leaves mergeOnSuccess at its default (false). Drives the REAL `run` command
    // (SSOT §2/§5.3: "CLI --merge-on-success overrides").
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F11a: <c>guardrails run --merge-on-success</c> forces end-of-run delivery on. The plan's
    /// <c>guardrails.json</c> has <c>mergeOnSuccess: false</c> (absent default), so without the flag
    /// the user's branch would stay at its original HEAD; WITH the flag a wholly-green run
    /// fast-forwards the user's branch to the plan tip — proving the flag (not the config) drove the
    /// delivery. Mirrors <see cref="MergeOnSuccess_FF_AdvancesUserBranchToTip"/> but through the CLI.
    /// </summary>
    [Fact]
    public async Task Cli_MergeOnSuccessFlag_DeliversToUserBranch_WhenConfigDefaultOff()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // mergeOnSuccess defaults to false in the JSON — only the CLI flag turns delivery on.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: false,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        int exitCode = await InvokeCliAsync("run", planDir, "--merge-on-success", "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exitCode);

        // The user's branch advanced to the plan tip via fast-forward (delivery happened) ...
        Assert.NotEqual(initialHead, repo.HeadSha());
        // ... with no merge commit (FF preserves linear history) ...
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
        // ... and the user stays on their original named branch (not detached).
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    /// <summary>
    /// Drive the real <c>run</c> command pipeline. Output goes to a discarded
    /// <see cref="StringConsoleIo"/> so nothing touches the process-global console (parallel-safe).
    /// </summary>
    private static async Task<int> InvokeCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("merge-on-success cli test root");
        root.Add(RunCommand.Create(io));
        return await root.Parse(args).InvokeAsync();
    }
}
