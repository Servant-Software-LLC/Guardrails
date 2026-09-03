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
/// Core scenarios (SSOT §5.3 / plan 08 §5), re-baselined for the #340 default flip (OFF → ON):
/// <list type="bullet">
///   <item><b>DefaultOn_OmittedKey_DeliversAndNotFlaggedUndelivered</b> — <c>mergeOnSuccess</c> absent
///     (now default TRUE): the merge-back IS attempted and delivers; <c>MergeOnSuccessOutcome</c> non-null;
///     not flagged undelivered.</item>
///   <item><b>MergeOnSuccessFalse_OptOut_NotDeliveredAndFlaggedUndelivered</b> — explicit
///     <c>mergeOnSuccess: false</c>: no merge attempted; the run IS flagged undelivered (the #344 warning
///     fires only for the opt-out).</item>
///   <item><b>MergeOnSuccess_FF_AdvancesUserBranchToTip</b> — <c>mergeOnSuccess: true</c>, no
///     concurrent user advance: FF-merge; no merge commit; user HEAD at plan tip.</item>
///   <item><b>MergeOnSuccess_ConflictingAdvance_NeedsHuman_PlanBranchIntact</b> — <c>mergeOnSuccess:
///     true</c>, user branch advanced mid-run with a conflicting commit: AI-merge withheld;
///     <c>Conflict</c> outcome; plan branch intact; user branch untouched by the harness.</item>
///   <item><b>MergeOnSuccess_SkippedWhenRunFails</b> — a task fails: merge not attempted;
///     <c>MergeOnSuccessOutcome</c> is null.</item>
/// </list>
/// <para>
/// Issue #340 flip coverage: the <see cref="RunReport.WhollyGreenButUndelivered"/> flag is TRUE for the
/// <b>opt-out</b> cases that strand work on a separate plan branch (<b>MergeOnSuccessFalse_OptOut</b>, and —
/// #345 review 1c — <b>RunOnCurrentBranch_OptOut_StrandsWork_FlagsUndelivered</b>, since runOnCurrentBranch is
/// an unwired stub); FALSE for a delivered run (<b>DefaultOn_OmittedKey</b>, <b>MergeOnSuccess_FF</b>,
/// <b>RunOnCurrentBranch_DefaultOn_DeliversToCheckout</b>), a non-green run (<b>SkippedWhenRunFails</b>), and
/// SERIAL mode (<b>SerialMode_GreenRun</b> = no provider / no plan branch — the only genuine suppression).
/// CLI precedence + idempotency: <b>Cli_MergeOnSuccessFlag_OverridesConfigFalse</b>,
/// <b>Cli_NoMergeOnSuccessFlag_SuppressesDefaultDelivery</b>, <b>Cli_BothMergeFlags_IsUsageError</b>,
/// <b>Resume_AlreadyDelivered_ReDeliversIdempotently</b>.
/// </para>
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

        /// <summary>Forward the inner provider's detail (the #448 blocking-path list / a hook's stderr).</summary>
        public string? LastMergeOnSuccessDetail => _inner.LastMergeOnSuccessDetail;

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
    // DirtyTrackedFileBeforeMergeDecorator (issue #448) — wraps a real IWorktreeProvider and, inside
    // MergePlanBranchIntoUserBranch, writes to a TRACKED file in the user's checkout WITHOUT committing,
    // immediately before delegating. This models the real incident faithfully: the RUN ITSELF dirties a
    // tracked file mid-run (the waved run regenerating its own per-wave diagram.md/.html at a wave
    // boundary) and then meets its own dirty-tree gate at delivery time. Optionally `git add`s the
    // change so the STAGED-vs-unstaged half of the gate can be driven too.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class DirtyTrackedFileBeforeMergeDecorator : IWorktreeProvider
    {
        private readonly IWorktreeProvider _inner;
        private readonly string _repoPath;
        private readonly string _dirtyRelPath;
        private readonly string _dirtyContent;
        private readonly bool _stage;

        /// <summary>The plan branch name captured at merge time, so a test can assert it survived the halt.</summary>
        public string? PlanBranchName { get; private set; }

        public DirtyTrackedFileBeforeMergeDecorator(
            IWorktreeProvider inner, string repoPath, string dirtyRelPath, string dirtyContent, bool stage = false)
        {
            _inner = inner;
            _repoPath = repoPath;
            _dirtyRelPath = dirtyRelPath;
            _dirtyContent = dirtyContent;
            _stage = stage;
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

        /// <summary>The inner provider's detail (the #448 blocking-path list) must reach the Scheduler.</summary>
        public string? LastMergeOnSuccessDetail => _inner.LastMergeOnSuccessDetail;

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
        {
            PlanBranchName = integ.PlanBranchName;

            string fullPath = Path.Combine(
                _repoPath, _dirtyRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, _dirtyContent);
            if (_stage)
            {
                TempGitRepo.Git(_repoPath, "add", _dirtyRelPath);
            }

            return _inner.MergePlanBranchIntoUserBranch(integ, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // SwitchUserBranchBeforeMergeDecorator (issue #588) — wraps a real IWorktreeProvider and, inside
    // MergePlanBranchIntoUserBranch, CREATES AND CHECKS OUT a new branch in the user's repo immediately
    // before delegating. This is the measured incident: a branch cut from HEAD while the run was in
    // flight, which silently redirected a bare `git merge` away from the branch the run had pinned.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SwitchUserBranchBeforeMergeDecorator : IWorktreeProvider
    {
        private readonly IWorktreeProvider _inner;
        private readonly string _repoPath;
        private readonly string _newBranch;

        public SwitchUserBranchBeforeMergeDecorator(IWorktreeProvider inner, string repoPath, string newBranch)
        {
            _inner = inner;
            _repoPath = repoPath;
            _newBranch = newBranch;
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

        /// <summary>The inner provider's detail (the #588 pair of branch names) must reach the Scheduler.</summary>
        public string? LastMergeOnSuccessDetail => _inner.LastMergeOnSuccessDetail;

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
        {
            TempGitRepo.Git(_repoPath, "checkout", "-b", _newBranch);
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
    /// <param name="switchToBranchMidRun">
    /// Issue #588: when set, the task's own action <c>git checkout -b</c>s this branch in
    /// <paramref name="repoPath"/> — the user's checkout moving WHILE the run is in flight, driven through
    /// the real CLI pipeline (no test decorator can reach inside the CLI's own provider construction).
    /// </param>
    private static string CreatePlanInRepo(
        string repoPath,
        bool? mergeOnSuccess,
        string taskFile = "src/app.cs",
        string taskFileContent = "class App {}",
        bool guardrailFails = false,
        bool runOnCurrentBranch = false,
        string? switchToBranchMidRun = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        // null → OMIT the key entirely (exercise the #340 default-ON); true/false → set it explicitly.
        string mergeLine = mergeOnSuccess is { } m
            ? $"\n  \"mergeOnSuccess\": {(m ? "true" : "false")},"
            : "";
        string runOnCurrentLine = runOnCurrentBranch ? "\n  \"runOnCurrentBranch\": true," : "";

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",{{mergeLine}}{{runOnCurrentLine}}
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGitTask(planDir, "01-task", taskFile, taskFileContent, guardrailFails,
            switchToBranchMidRun is null ? null : (repoPath, switchToBranchMidRun));
        return planDir;
    }

    private static void WriteGitTask(
        string planDir,
        string taskId,
        string taskFile,
        string taskFileContent,
        bool guardrailFails,
        (string RepoPath, string Branch)? switchBranchMidRun = null)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{"description": "mos-test {{taskId}}", "writeScope": ["{{taskFile}}"], "dependsOn": []}""");

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";

        if (OperatingSystem.IsWindows())
        {
            // Windows: backslash separators for the file path in the PS script. The optional #588 line
            // swallows both streams ($null = & … 2>&1) so git's "Switched to a new branch" chatter on
            // stderr cannot be mistaken for a failing action.
            string psPath = taskFile.Replace("/", "\\");
            string psSwitch = switchBranchMidRun is { } ps
                ? $"$null = & git -C '{ps.RepoPath}' checkout -b '{ps.Branch}' 2>&1\n"
                : "";
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\n" +
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\{psPath}\" -Force -Value '{taskFileContent}' | Out-Null\n" +
                psSwitch +
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

            string shSwitch = switchBranchMidRun is { } sh
                ? $"git -C '{sh.RepoPath}' checkout -b '{sh.Branch}' >/dev/null 2>&1\n"
                : "";

            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                mkdirLine +
                $"printf '%s' '{taskFileContent}' > \"$GUARDRAILS_WORKSPACE/{taskFile}\"\n" +
                shSwitch +
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
        IWorktreeProvider? worktreeProvider,
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
    // Test 1 — default ON (#340): an OMITTED mergeOnSuccess key now DELIVERS by default
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #340 default-ON delivery: when <c>mergeOnSuccess</c> is absent from <c>guardrails.json</c> it now
    /// defaults to <b>true</b> ("green means delivered"), so a wholly-green worktree-mode run DELIVERS the
    /// plan branch into the user's original branch at run end. The provider's merge-back IS invoked
    /// (outcome non-null), and because delivery ran the run is NOT flagged
    /// <see cref="RunReport.WhollyGreenButUndelivered"/>. Re-baselined from the old
    /// <c>DefaultOff_LeavesUserBranchAtOriginalHead</c> — the default flipped OFF→ON in #340.
    /// </summary>
    [Fact]
    public async Task DefaultOn_OmittedKey_DeliversAndNotFlaggedUndelivered()
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-mos-on-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planDir);
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

            // No mergeOnSuccess key → now defaults to TRUE (#340). Delivery fires by default.
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

            WriteTrivialGreenTask(planDir, "01-green-task");

            var tracking = new TrackingFakeProvider();
            var (report, _) = await RunWithProviderAsync(planDir, tracking, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded, "DefaultOn: expected all tasks to succeed.");

            // Default ON: the merge-back WAS attempted exactly once and succeeded (the fake FF's).
            Assert.Equal(1, tracking.MergePlanBranchIntoUserBranchCallCount);
            Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);
            // The report names the branch delivery landed on (the fake's OriginalBranch = "main"), so the
            // CLI's one-time delivered-by-default notice can name it.
            Assert.Equal("main", report.DeliveredToBranch);

            // Delivery actually RAN ⇒ never the undelivered-work case (the #344 warning must NOT fire).
            Assert.False(report.WhollyGreenButUndelivered);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 1b (#340) — explicit OPT-OUT: mergeOnSuccess:false leaves the work on the plan branch
    // and REVIVES the loud green-but-undelivered warning (the #344 backstop).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #340 opt-out: an EXPLICIT <c>"mergeOnSuccess": false</c> suppresses the now-default delivery — no
    /// merge-back is attempted (outcome null), and the wholly-green worktree-mode run is flagged
    /// <see cref="RunReport.WhollyGreenButUndelivered"/> so the CLI prints the loud warning. This is the
    /// case the old <c>DefaultOff_…</c> test covered implicitly; with the default flipped it must now be
    /// requested explicitly. Proves the #344 warning still composes — it fires precisely when the user
    /// opted OUT.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccessFalse_OptOut_NotDeliveredAndFlaggedUndelivered()
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-mos-optout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planDir);
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

            // Explicit opt-out — leave the verified work on the plan branch for manual review.
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "mergeOnSuccess": false,
                  "defaultRetries": 0,
                  "maxParallelism": 2
                }
                """);

            WriteTrivialGreenTask(planDir, "01-green-task");

            var tracking = new TrackingFakeProvider();
            var (report, _) = await RunWithProviderAsync(planDir, tracking, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded, "OptOut: expected all tasks to succeed.");

            // Opt-out: no merge-back was attempted, so the outcome is null and no branch is named.
            Assert.Null(report.MergeOnSuccessOutcome);
            Assert.Equal(0, tracking.MergePlanBranchIntoUserBranchCallCount);
            Assert.Null(report.DeliveredToBranch);

            // The incident case: wholly-green worktree-mode run whose work was NOT delivered ⇒ the loud
            // #340/#344 warning MUST fire.
            Assert.True(report.WhollyGreenButUndelivered);
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

        Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);

        // Issue #340: delivery actually RAN (mergeOnSuccess on) ⇒ never the undelivered-work case.
        Assert.False(report.WhollyGreenButUndelivered);

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

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Issue #448 — the dirty-tree gate is an INTERSECTION, not "any dirt anywhere".
    //
    // The incident: a wholly-green (14/14) WAVED run regenerated its own TRACKED per-wave
    // diagram.md/.html mid-run, then refused its own mergeOnSuccess delivery on that self-inflicted
    // dirt — even though the dirty paths (docs/plans/**/diagram.*) were provably DISJOINT from the
    // merge's path set (src/, tests/, one doc) and git merged them by hand with zero conflicts.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Commit a tracked file into the repo so a later uncommitted edit to it counts as TRACKED dirt.</summary>
    private static void CommitTrackedFile(TempGitRepo repo, string relPath, string content)
    {
        string full = Path.Combine(repo.RepoPath, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        TempGitRepo.Git(repo.RepoPath, "add", relPath);
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", $"track {relPath}");
    }

    private static string ReadRepoFile(TempGitRepo repo, string relPath) =>
        File.ReadAllText(Path.Combine(repo.RepoPath, relPath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Write an uncommitted change to an already-tracked file in the user's checkout.</summary>
    private static void DirtyTrackedFile(TempGitRepo repo, string relPath, string content) =>
        File.WriteAllText(Path.Combine(repo.RepoPath, relPath.Replace('/', Path.DirectorySeparatorChar)), content);

    // A tracked path in the shape the incident produced — a per-wave diagram the run regenerates itself.
    private const string DiagramRelPath = "docs/plans/salvage/wave-03-provision/diagram.md";
    private const string RegeneratedDiagram = "diagram: REGENERATED BY THE RUN\n";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #448 (i) — THE REGRESSION: dirt DISJOINT from the merge's paths must now DELIVER.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #448, the regression the fix exists for: a wholly-green run whose own mid-run write dirtied a
    /// TRACKED file that is DISJOINT from everything the merge would update now DELIVERS (fast-forward)
    /// instead of refusing itself with <see cref="MergeOnSuccessResult.DirtyWorkingTree"/>. Modelled on the
    /// real incident — the dirty file is a per-wave <c>docs/plans/**/diagram.md</c> written by the run,
    /// while the merge touches only <c>src/app.cs</c>. Also asserts the delivery preserved that uncommitted
    /// WIP byte-for-byte and left it dirty, exactly as a manual <c>git merge</c> would.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_DirtDisjointFromMergePaths_DeliversAndPreservesWip()
    {
        using var repo = new TempGitRepo();
        CommitTrackedFile(repo, DiagramRelPath, "diagram: stale\n");
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // The plan's only task writes src/app.cs — DISJOINT from the diagram path. mergeOnSuccess default ON.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        // The decorator dirties the tracked diagram at merge time — i.e. the RUN created the dirt.
        var decorator = new DirtyTrackedFileBeforeMergeDecorator(
            new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            repo.RepoPath, DiagramRelPath, RegeneratedDiagram);

        var (report, _) = await RunWithProviderAsync(planDir, decorator, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "#448 disjoint-dirt: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // THE FIX: pre-#448 this returned DirtyWorkingTree and delivered nothing.
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);
        Assert.Equal(originalBranch, report.DeliveredToBranch);
        Assert.Null(report.MergeOnSuccessDetail);
        Assert.False(report.WhollyGreenButUndelivered);

        // Delivery is REAL: the user's branch advanced and the deliverable landed in their checkout.
        Assert.NotEqual(initialHead, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "#448: the narrowed gate must let the delivery land the plan's file in the user's checkout.");

        // The uncommitted WIP survived untouched and is still dirty — git never rewrites a path it
        // does not merge, which is exactly why refusing on it was wrong.
        Assert.Equal(RegeneratedDiagram, ReadRepoFile(repo, DiagramRelPath));
        Assert.Contains("diagram.md",
            TempGitRepo.Git(repo.RepoPath, "status", "--porcelain", "--untracked-files=no"),
            StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #448 (ii) — dirt INTERSECTING the merge's paths still refuses, and NAMES the path.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #448, the half that must NOT change: an uncommitted change to a tracked file the merge WOULD
    /// update still refuses with <see cref="MergeOnSuccessResult.DirtyWorkingTree"/> and the user's branch
    /// is untouched. Part B on top: the halt now NAMES the offending path via
    /// <see cref="RunReport.MergeOnSuccessDetail"/> — and names ONLY it. A second, DISJOINT dirty file is
    /// present throughout; its absence from the detail is the proof the gate really narrowed rather than
    /// just relabelling the old any-dirt-anywhere refusal.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_DirtIntersectsMergePaths_RefusesAndNamesOnlyTheBlockingPath()
    {
        using var repo = new TempGitRepo();
        const string appRelPath = "src/app.cs";
        CommitTrackedFile(repo, appRelPath, "class App { /* original */ }\n");
        CommitTrackedFile(repo, DiagramRelPath, "diagram: stale\n");
        string originalBranch = repo.CurrentBranch();

        // The task REWRITES the tracked src/app.cs ⇒ the merge's path set contains it.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: appRelPath, taskFileContent: "class App { /* from the plan */ }");

        // Disjoint dirt present from the start (the incident's shape) …
        DirtyTrackedFile(repo, DiagramRelPath, RegeneratedDiagram);
        string headBeforeDelivery = repo.HeadSha();

        // … plus INTERSECTING dirt created at merge time.
        const string appWip = "class App { /* my uncommitted WIP */ }\n";
        var decorator = new DirtyTrackedFileBeforeMergeDecorator(
            new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            repo.RepoPath, appRelPath, appWip);

        var (report, _) = await RunWithProviderAsync(planDir, decorator, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "#448 intersecting-dirt: the tasks all pass; only the end-of-run delivery halts. " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.Equal(MergeOnSuccessResult.DirtyWorkingTree, report.MergeOnSuccessOutcome);
        Assert.Null(report.DeliveredToBranch);

        // Part B: the blocking path is NAMED …
        Assert.NotNull(report.MergeOnSuccessDetail);
        Assert.Contains(appRelPath, report.MergeOnSuccessDetail!, StringComparison.Ordinal);
        // … and ONLY it — the disjoint dirty file must not be blamed (the narrowing, proven).
        Assert.DoesNotContain("diagram.md", report.MergeOnSuccessDetail!, StringComparison.Ordinal);

        // The user's branch is untouched and their WIP is intact; the work stays on the plan branch.
        Assert.Equal(headBeforeDelivery, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
        Assert.Equal(appWip, ReadRepoFile(repo, appRelPath));
        Assert.NotNull(decorator.PlanBranchName);
        Assert.True(repo.BranchExists(decorator.PlanBranchName!),
            "#448: a refused delivery must leave the plan branch intact.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #448 — merge SHAPE matters: a real (non-FF) merge demands a CLEAN INDEX, so disjoint dirt is
    // safe there only while it is UNSTAGED. Two tests pin both sides of that measured git behaviour.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #448, non-FF + UNSTAGED disjoint dirt: the user's branch advanced mid-run (so delivery is a
    /// real merge, not a fast-forward) while a disjoint tracked file carries unstaged changes. git merges
    /// that cleanly, so delivery must SUCCEED with <see cref="MergeOnSuccessResult.Merged"/> — and the
    /// unstaged WIP must survive, uncommitted, outside the merge commit.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_NonFf_UnstagedDisjointDirt_DeliversViaRealMerge()
    {
        using var repo = new TempGitRepo();
        const string otherRelPath = "src/other.cs";
        CommitTrackedFile(repo, DiagramRelPath, "diagram: stale\n");
        CommitTrackedFile(repo, otherRelPath, "class Other {}\n");
        string originalBranch = repo.CurrentBranch();

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        // Inner: dirty the disjoint diagram (UNSTAGED). Outer: advance the user's branch on a file the
        // plan never touches ⇒ a clean, non-fast-forward merge.
        var dirtying = new DirtyTrackedFileBeforeMergeDecorator(
            new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            repo.RepoPath, DiagramRelPath, RegeneratedDiagram, stage: false);
        var decorator = new AdvanceUserBranchBeforeMergeDecorator(
            dirtying, repo.RepoPath, otherRelPath, "class Other { /* user advanced */ }\n");

        var (report, _) = await RunWithProviderAsync(planDir, decorator, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "#448 non-FF unstaged: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.Equal(MergeOnSuccessResult.Merged, report.MergeOnSuccessOutcome);
        Assert.Equal(originalBranch, report.DeliveredToBranch);
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")));

        // A real merge commit exists, and the unstaged WIP was NOT swept into it.
        Assert.NotEmpty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
        Assert.Equal(RegeneratedDiagram, ReadRepoFile(repo, DiagramRelPath));
        Assert.Contains("diagram.md",
            TempGitRepo.Git(repo.RepoPath, "status", "--porcelain", "--untracked-files=no"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #448, non-FF + STAGED disjoint dirt: git refuses a real merge whenever the INDEX differs from
    /// HEAD, even on a path the merge never touches (measured — a fast-forward tolerates the same staged
    /// change). The gate must therefore still refuse here, and refuse HONESTLY: the outcome is
    /// <see cref="MergeOnSuccessResult.DirtyWorkingTree"/> naming the staged path, NOT the
    /// <see cref="MergeOnSuccessResult.Conflict"/> a naive path-intersection-only narrowing would have
    /// produced by letting <c>git merge</c> fail through to the conflict branch.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_NonFf_StagedDisjointDirt_RefusesAsDirtyNotConflict()
    {
        using var repo = new TempGitRepo();
        const string otherRelPath = "src/other.cs";
        CommitTrackedFile(repo, DiagramRelPath, "diagram: stale\n");
        CommitTrackedFile(repo, otherRelPath, "class Other {}\n");
        string originalBranch = repo.CurrentBranch();

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        // Identical to the test above except the disjoint dirt is STAGED.
        var dirtying = new DirtyTrackedFileBeforeMergeDecorator(
            new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            repo.RepoPath, DiagramRelPath, RegeneratedDiagram, stage: true);
        var decorator = new AdvanceUserBranchBeforeMergeDecorator(
            dirtying, repo.RepoPath, otherRelPath, "class Other { /* user advanced */ }\n");

        var (report, _) = await RunWithProviderAsync(planDir, decorator, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "#448 non-FF staged: the tasks all pass; only the end-of-run delivery halts. " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.Equal(MergeOnSuccessResult.DirtyWorkingTree, report.MergeOnSuccessOutcome);
        Assert.NotNull(report.MergeOnSuccessDetail);
        Assert.Contains("diagram.md", report.MergeOnSuccessDetail!, StringComparison.Ordinal);

        // Nothing was delivered and the user's branch sits at its own advance commit.
        Assert.Null(report.DeliveredToBranch);
        Assert.NotNull(decorator.UserBranchHeadAfterAdvance);
        Assert.Equal(decorator.UserBranchHeadAfterAdvance, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #448 part B through the REAL CLI — the user must read the blocking path off the console.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #448 part B, end to end: <c>guardrails run</c> on a wholly-green plan whose delivery is
    /// refused prints the BLOCKING PATH, so the user is never sent to <c>git status</c> to discover what
    /// stopped a green run from delivering. Exit stays <see cref="ExitCodes.TaskFailed"/> (the work is
    /// durable on the plan branch but the user must act, SSOT §5.3).
    /// </summary>
    [Fact]
    public async Task Cli_DirtyIntersectingPath_HaltsAndNamesTheBlockingPath()
    {
        using var repo = new TempGitRepo();
        const string appRelPath = "src/app.cs";
        CommitTrackedFile(repo, appRelPath, "class App { /* original */ }\n");
        string headBeforeRun = repo.HeadSha();

        // The task rewrites the tracked src/app.cs; the user has uncommitted changes to that same file.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: appRelPath, taskFileContent: "class App { /* from the plan */ }");
        DirtyTrackedFile(repo, appRelPath, "class App { /* my uncommitted WIP */ }\n");

        var io = new StringConsoleIo();
        int exitCode = await InvokeCliAsync(io, "run", planDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exitCode);
        Assert.Contains("uncommitted changes", io.OutText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(appRelPath, io.OutText, StringComparison.Ordinal);
        // Nothing was delivered — the user's branch is untouched.
        Assert.Equal(headBeforeRun, repo.HeadSha());
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
                """{"description": "task that always fails guardrail", "writeScope": [], "dependsOn": []}""");
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

            // No merge was attempted because the run was not wholly green.
            Assert.Null(report.MergeOnSuccessOutcome);

            // The provider's merge method must NOT have been called when the run failed.
            Assert.Equal(0, tracking.MergePlanBranchIntoUserBranchCallCount);

            // Issue #340: a run that is not wholly green is never flagged undelivered (no false warning).
            Assert.False(report.WhollyGreenButUndelivered);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 4b (#340) — SERIAL mode (no worktree provider): a wholly-green run is NEVER flagged
    // undelivered. There is no separate plan branch — the work is already in the shared workspace /
    // the user's checkout, so warning about "undelivered work on guardrails/<plan>" would be a lie.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #340 honesty gate (serial mode): with NO worktree provider (serial shared-workspace run),
    /// a wholly-green run leaves <see cref="RunReport.WhollyGreenButUndelivered"/> false — there is no
    /// plan branch holding undelivered work, so the CLI must NOT print the loud warning.
    /// </summary>
    [Fact]
    public async Task SerialMode_GreenRun_NotFlaggedUndelivered()
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-mos-serial-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planDir);
            Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

            // maxParallelism: 1 → serial; no worktreeProvider passed to RunWithProviderAsync below.
            // mergeOnSuccess is OMITTED — it now defaults ON (#340), but serial mode has NO worktree
            // provider / plan branch, so the delivery gate (provider != null && integ != null) short-circuits:
            // #340 mechanism-confirm — serial mode attempts NO merge-back even with delivery default-on.
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 1
                }
                """);

            WriteTrivialGreenTask(planDir, "01-green-task");

            var (report, _) = await RunWithProviderAsync(planDir, worktreeProvider: null, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded, "SerialMode: expected all tasks to succeed.");
            // No provider ⇒ no plan branch ⇒ no delivery attempted ⇒ nothing is undelivered.
            Assert.Null(report.MergeOnSuccessOutcome);
            Assert.False(report.WhollyGreenButUndelivered);
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 4c (#340 / #345 review 1b) — runOnCurrentBranch is an UNWIRED STUB, so a real-git worktree
    // run there behaves like an ordinary worktree run: a SEPARATE guardrails/<plan> branch is forked,
    // and default-ON delivery is a REAL fast-forward onto the user's checkout (NOT a self-merge/no-op).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #345 review, finding 1b (default-ON case): with <c>runOnCurrentBranch: true</c> and
    /// <c>mergeOnSuccess</c> OMITTED (default ON), a wholly-green real-git worktree run DELIVERS — because
    /// runOnCurrentBranch is an unwired stub, the harness still forks a separate <c>guardrails/plan</c> branch
    /// and the default-ON merge-back fast-forwards it onto the user's branch. Asserts delivery actually
    /// happened (outcome FastForwarded, <see cref="RunReport.DeliveredToBranch"/> set, HEAD moved, the
    /// deliverable file lands in the user's checkout) and that the run is NOT flagged undelivered.
    /// </summary>
    [Fact]
    public async Task RunOnCurrentBranch_DefaultOn_DeliversToCheckout_NotFlaggedUndelivered()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // runOnCurrentBranch: true; mergeOnSuccess OMITTED → default ON. Real git worktree provider.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null, runOnCurrentBranch: true,
            taskFile: "src/app.cs", taskFileContent: "class App {}");
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(planDir, provider, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "RunOnCurrentBranch default-on: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // Delivery RAN (the stub does not skip it): FF onto the user's branch, HEAD moved.
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report.MergeOnSuccessOutcome);
        Assert.Equal(originalBranch, report.DeliveredToBranch);
        Assert.NotEqual(initialHead, repo.HeadSha());
        // The deliverable is now in the user's MAIN checkout (delivery is real, not a no-op).
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "#345 1b: default-ON delivery under runOnCurrentBranch must land the file in the user's checkout.");
        Assert.Equal(originalBranch, repo.CurrentBranch());
        // Delivery ran ⇒ never the undelivered-work case.
        Assert.False(report.WhollyGreenButUndelivered);
    }

    /// <summary>
    /// #345 review, finding 1b + 1c (opt-out case — closes the silent-stranding hole): with
    /// <c>runOnCurrentBranch: true</c> AND <c>mergeOnSuccess: false</c> (opt-out), a wholly-green real-git
    /// worktree run does NOT deliver — and because runOnCurrentBranch is an unwired stub the work is stranded
    /// on the SEPARATE <c>guardrails/plan</c> branch with nothing on the user's checkout. The Scheduler MUST
    /// flag <see cref="RunReport.WhollyGreenButUndelivered"/> (the warning now fires — previously suppressed
    /// by the false <c>!RunOnCurrentBranch</c> premise, the exact #340 incident uncovered).
    /// </summary>
    [Fact]
    public async Task RunOnCurrentBranch_OptOut_StrandsWork_FlagsUndelivered()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();

        // runOnCurrentBranch: true; mergeOnSuccess EXPLICIT false (opt-out). Real git worktree provider.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: false, runOnCurrentBranch: true,
            taskFile: "src/app.cs", taskFileContent: "class App {}");
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithProviderAsync(planDir, provider, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "RunOnCurrentBranch opt-out: expected all tasks to succeed; " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // Opt-out ⇒ no delivery; the work is stranded on the separate plan branch, user HEAD unchanged.
        Assert.Null(report.MergeOnSuccessOutcome);
        Assert.Null(report.DeliveredToBranch);
        Assert.Equal(initialHead, repo.HeadSha());
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "#345 1c: opt-out must leave the deliverable on the plan branch, NOT in the user's checkout.");

        // The load-bearing fix: the warning MUST fire — a separate plan branch DOES hold undelivered work.
        Assert.True(report.WhollyGreenButUndelivered,
            "#345 1c: runOnCurrentBranch + opt-out strands work on a separate plan branch ⇒ MUST flag undelivered.");
    }

    /// <summary>Write a trivially-green task (exit-0 action + exit-0 guardrail, no git writes), OS-picked flavour.</summary>
    private static void WriteTrivialGreenTask(string planDir, string taskId)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{"description": "trivial green task {{taskId}}", "writeScope": [], "dependsOn": []}""");
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
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 5 (F11a) — the CLI --merge-on-success flag turns on user-branch delivery even when
    // guardrails.json leaves mergeOnSuccess at its default (false). Drives the REAL `run` command
    // (SSOT §2/§5.3: "CLI --merge-on-success overrides").
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F11a: <c>guardrails run --merge-on-success</c> forces end-of-run delivery on even against a config
    /// that EXPLICITLY opted out. The plan's <c>guardrails.json</c> sets <c>mergeOnSuccess: false</c>, so
    /// without the flag the user's branch would stay at its original HEAD; WITH the flag (CLI precedence:
    /// flag beats config, #340) a wholly-green run fast-forwards the user's branch to the plan tip — proving
    /// the flag (not the config) drove the delivery. Mirrors <see cref="MergeOnSuccess_FF_AdvancesUserBranchToTip"/>
    /// but through the CLI.
    /// </summary>
    [Fact]
    public async Task Cli_MergeOnSuccessFlag_OverridesConfigFalse_DeliversToUserBranch()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // Config explicitly opts OUT (mergeOnSuccess: false) — only the CLI flag turns delivery back on.
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 5b (#340) — the CLI --no-merge-on-success flag SUPPRESSES the now-default delivery, even
    // when guardrails.json omits the key (default ON). Drives the REAL `run` command.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #340 opt-out flag through the CLI: with <c>mergeOnSuccess</c> OMITTED (default ON) a wholly-green run
    /// would normally deliver; <c>--no-merge-on-success</c> suppresses it, so the user's branch stays at its
    /// original HEAD. The run is still green (exit 0 — the loud green-but-undelivered warning is a safety
    /// notice, not a failure). Proves CLI precedence: the flag beats the true default.
    /// </summary>
    [Fact]
    public async Task Cli_NoMergeOnSuccessFlag_SuppressesDefaultDelivery()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        // mergeOnSuccess OMITTED → default ON. The flag must turn it back off.
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        int exitCode = await InvokeCliAsync("run", planDir, "--no-merge-on-success", "--no-ui", "--no-log-server");

        // Green-but-undelivered is still exit 0 (the warning is a safety notice, not a failure).
        Assert.Equal(ExitCodes.Success, exitCode);

        // No delivery: the user's branch is UNCHANGED at its original HEAD (work stayed on the plan branch).
        Assert.Equal(initialHead, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 5c (#340) — passing BOTH --merge-on-success and --no-merge-on-success is a usage error.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #340 contradictory-flags guard: <c>--merge-on-success</c> and <c>--no-merge-on-success</c> together
    /// are a usage error — the CLI exits <see cref="ExitCodes.HarnessError"/> with an explanatory message
    /// and runs NOTHING (the user's branch is untouched).
    /// </summary>
    [Fact]
    public async Task Cli_BothMergeFlags_IsUsageError()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        var io = new StringConsoleIo();
        int exitCode = await InvokeCliAsync(io,
            "run", planDir, "--merge-on-success", "--no-merge-on-success", "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.HarnessError, exitCode);
        Assert.Contains("contradictory", io.OutText, StringComparison.OrdinalIgnoreCase);
        // Nothing ran — the user's branch is untouched (no plan branch delivery).
        Assert.Equal(initialHead, repo.HeadSha());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 5d (#340) — delivery is IDEMPOTENT on resume: a resumed run that already delivered
    // re-drains green and re-fires delivery, which git reports "already up to date" — no double-merge,
    // no error, HEAD unchanged. (Design mechanism-confirm.)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #340 resume idempotency: a first wholly-green run delivers (default ON) via FF. A second run RESUMES
    /// from the persisted journal — every task is already succeeded, so the DAG re-drains green and the
    /// end-of-run delivery re-fires. Because the user's branch is already at the plan tip, the ff-only merge
    /// is "already up to date": the outcome is again <see cref="MergeOnSuccessResult.FastForwarded"/> (no
    /// throw), HEAD is unchanged from the first delivery, and no merge commit is introduced.
    /// </summary>
    [Fact]
    public async Task Resume_AlreadyDelivered_ReDeliversIdempotently()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        // mergeOnSuccess OMITTED → default ON (#340).
        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        // ── Run 1: fresh provider, delivers via FF ────────────────────────────────────────────
        var (report1, _) = await RunWithProviderAsync(
            planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            TestContext.Current.CancellationToken);
        Assert.True(report1.AllSucceeded,
            "Resume run 1 must succeed; " + string.Join(", ", report1.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report1.MergeOnSuccessOutcome);
        string headAfterFirstDelivery = repo.HeadSha();
        Assert.NotEqual(initialHead, headAfterFirstDelivery); // delivered

        // ── Run 2: RESUME (journal persisted) — re-drains green, re-fires delivery idempotently ──
        var (report2, _) = await RunWithProviderAsync(
            planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            TestContext.Current.CancellationToken);
        Assert.True(report2.AllSucceeded, "Resume run 2 must stay green (all tasks already succeeded).");

        // Idempotent re-delivery: "already up to date" FF, never a double-merge or error.
        Assert.Equal(MergeOnSuccessResult.FastForwarded, report2.MergeOnSuccessOutcome);
        Assert.False(report2.WhollyGreenButUndelivered);
        Assert.Equal(headAfterFirstDelivery, repo.HeadSha()); // HEAD unchanged — no double-merge
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Issue #588 — the delivery TARGET is pinned at run start; the merge must not land elsewhere.
    //
    // MEASURED INCIDENT: a run started on 'master'; a `design/34-run-event-stream-and-attach` branch was
    // cut from HEAD and checked out WHILE the run was in flight; the end-of-run
    // `git merge --ff-only <planBranch>` — a bare merge into whatever HEAD then was — produced
    // "Merge branch 'guardrails/33-…' into design/34-…", while the run printed "delivered to master"
    // from the value pinned at start (IntegrationHandle.OriginalBranch). Master contained zero
    // deliverables, and NOTHING in the output was self-inconsistent: only git revealed it.
    //
    // THE FIX COMPARES AND REFUSES. It never checks the user's branch back out — mutating a working tree
    // the user moved deliberately is a worse failure than declining — so a moved HEAD joins HookRejected
    // (#149/#150) and DirtyWorkingTree (#448) as "delivery withheld for a nameable reason", carrying its
    // detail through the same LastMergeOnSuccessDetail channel and leaving the verified work on the plan
    // branch: the SAFE failure direction.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The branch name from the incident, cut from HEAD mid-run.</summary>
    private const string BranchCutMidRun = "design/34-run-event-stream-and-attach";

    /// <summary>Commit a deliverable onto the plan branch through the integration worktree.</summary>
    private static void CommitOnPlanBranch(IntegrationHandle integ, string relPath, string content)
    {
        string full = Path.Combine(
            integ.IntegrationWorktreePath, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        TempGitRepo.Git(integ.IntegrationWorktreePath, "add", "-A");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "commit", "--no-verify", "-m", "plan work");
    }

    /// <summary>
    /// Issue #588 CONTROL: HEAD is still on the branch the run pinned, so delivery behaves exactly as it
    /// did before — a fast-forward onto the user's branch, no detail, the deliverable in their checkout.
    /// Without this the refusals below would still pass if the gate refused everything.
    /// </summary>
    [Fact]
    public void MergePlanBranchIntoUserBranch_HeadStillOnTheStartingBranch_DeliversUnchanged()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        string startingBranch = repo.CurrentBranch();
        string headBeforeDelivery = repo.HeadSha();

        IntegrationHandle integ = provider.CreateIntegration("588-stay", "run-588a", CancellationToken.None);
        CommitOnPlanBranch(integ, "src/app.cs", "class App {}");

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.FastForwarded, result);
        Assert.Null(provider.LastMergeOnSuccessDetail);
        Assert.NotEqual(headBeforeDelivery, repo.HeadSha());
        Assert.Equal(startingBranch, repo.CurrentBranch());
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "#588 control: an unmoved HEAD must still deliver the plan's file into the user's checkout.");
    }

    /// <summary>
    /// Issue #588, THE DEFECT: HEAD is on a branch cut from it mid-run. The merge must NOT run — not into
    /// the new branch (the incident), and not into the pinned one either, which would mean checking the
    /// user's branch back out over work they moved to deliberately. Nothing merges anywhere, the checkout
    /// is untouched, both branches sit where they were, and the detail NAMES BOTH so the operator can see
    /// the mismatch that stopped the delivery.
    /// </summary>
    [Fact]
    public void MergePlanBranchIntoUserBranch_HeadCutToAnotherBranch_RefusesAndMergesNothing()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        string startingBranch = repo.CurrentBranch();

        IntegrationHandle integ = provider.CreateIntegration("588-moved", "run-588b", CancellationToken.None);
        CommitOnPlanBranch(integ, "src/app.cs", "class App {}");

        // The incident: a branch cut from HEAD and checked out while the run is in flight.
        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", BranchCutMidRun);
        string headBeforeDelivery = repo.HeadSha();

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.BranchMoved, result);

        // Both branch names travel to the CLI — the whole point is that they DIFFER.
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(startingBranch, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Contains(BranchCutMidRun, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);

        // NOTHING was merged, into EITHER branch, and no branch was checked out on the user's behalf.
        Assert.Equal(BranchCutMidRun, repo.CurrentBranch());
        Assert.Equal(headBeforeDelivery, repo.HeadSha());
        Assert.Equal(headBeforeDelivery, TempGitRepo.Git(repo.RepoPath, "rev-parse", startingBranch).Trim());
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")),
            "#588: a refused delivery must leave the plan's file OUT of the user's checkout.");
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
        Assert.Empty(TempGitRepo.Git(repo.RepoPath, "status", "--porcelain").Trim());

        // The verified work is still on the plan branch, for a manual merge wherever the user wants it.
        Assert.True(repo.BranchExists("guardrails/588-moved"));
    }

    /// <summary>
    /// Issue #588, detached HEAD: <c>rev-parse --abbrev-ref HEAD</c> prints the literal <c>HEAD</c>, which
    /// is neither the pinned branch nor a branch at all — a delivery there would strand commits on no
    /// branch. It takes the SAME refusal path rather than crashing or merging.
    /// </summary>
    [Fact]
    public void MergePlanBranchIntoUserBranch_DetachedHead_TakesTheSameRefusal()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        string startingBranch = repo.CurrentBranch();

        IntegrationHandle integ = provider.CreateIntegration("588-detached", "run-588c", CancellationToken.None);
        CommitOnPlanBranch(integ, "src/app.cs", "class App {}");

        TempGitRepo.Git(repo.RepoPath, "checkout", "--detach");
        string headBeforeDelivery = repo.HeadSha();

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.BranchMoved, result);
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(startingBranch, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Contains("detached", provider.LastMergeOnSuccessDetail!, StringComparison.OrdinalIgnoreCase);

        // Still detached — nothing was merged and nothing was checked out for the user.
        Assert.Equal("HEAD", repo.CurrentBranch());
        Assert.Equal(headBeforeDelivery, repo.HeadSha());
        Assert.Equal(headBeforeDelivery, TempGitRepo.Git(repo.RepoPath, "rev-parse", startingBranch).Trim());
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")));
        Assert.True(repo.BranchExists("guardrails/588-detached"));
    }

    /// <summary>
    /// Issue #588 through the SCHEDULER — the report contract. A wholly-green run whose checkout moved
    /// mid-run must stamp <see cref="MergeOnSuccessResult.BranchMoved"/> and leave
    /// <see cref="RunReport.DeliveredToBranch"/> <b>null</b>. That null is the headline: the CLI's
    /// "delivered to X" line is driven off it, and the pre-fix report filled it from the branch pinned at
    /// run start — printing a truthful-looking delivery to a branch the work never reached.
    /// </summary>
    [Fact]
    public async Task MergeOnSuccess_CheckoutMovedDuringRun_ReportsBranchMoved_AndNamesNoDeliveryTarget()
    {
        using var repo = new TempGitRepo();
        string startingBranch = repo.CurrentBranch();
        string headBeforeRun = repo.HeadSha();

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}");

        var decorator = new SwitchUserBranchBeforeMergeDecorator(
            new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), repo.RepoPath, BranchCutMidRun);

        var (report, _) = await RunWithProviderAsync(planDir, decorator, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "#588: the tasks all pass; only the end-of-run delivery refuses. " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.Equal(MergeOnSuccessResult.BranchMoved, report.MergeOnSuccessOutcome);

        // THE HEADLINE: no delivery happened, so the report names no delivery target.
        Assert.Null(report.DeliveredToBranch);

        Assert.NotNull(report.MergeOnSuccessDetail);
        Assert.Contains(startingBranch, report.MergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Contains(BranchCutMidRun, report.MergeOnSuccessDetail!, StringComparison.Ordinal);

        // Not the #340 opt-out warning: delivery was ON, it RAN, and it refused.
        Assert.False(report.WhollyGreenButUndelivered);

        // The user's checkout never moved and never received the work.
        Assert.Equal(headBeforeRun, repo.HeadSha());
        Assert.Equal(BranchCutMidRun, repo.CurrentBranch());
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")));
    }

    /// <summary>
    /// Issue #588 through the REAL CLI — the operator surface. The task itself cuts a new branch in the
    /// user's repo mid-run (no decorator can reach inside the CLI's own provider construction), so this is
    /// the incident end to end. The halt must name BOTH branches and the plan branch the verified work is
    /// sitting on, must NOT print the "delivered to &lt;branch&gt;" line, and exits
    /// <see cref="ExitCodes.TaskFailed"/> like every other withheld delivery (SSOT §5.3).
    /// </summary>
    [Fact]
    public async Task Cli_CheckoutMovedDuringRun_HaltsNamingBothBranches_AndClaimsNoDelivery()
    {
        using var repo = new TempGitRepo();
        string startingBranch = repo.CurrentBranch();
        string headBeforeRun = repo.HeadSha();

        string planDir = CreatePlanInRepo(
            repo.RepoPath, mergeOnSuccess: null,
            taskFile: "src/app.cs", taskFileContent: "class App {}",
            switchToBranchMidRun: BranchCutMidRun);

        var io = new StringConsoleIo();
        int exitCode = await InvokeCliAsync(io, "run", planDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exitCode);

        // The task really did move the checkout — otherwise this test proves nothing.
        Assert.Equal(BranchCutMidRun, repo.CurrentBranch());

        // Both branches, named, in the operator's own words.
        Assert.Contains(
            $"run started on '{startingBranch}'; HEAD is now '{BranchCutMidRun}'",
            io.OutText, StringComparison.Ordinal);
        // … plus the plan branch the verified work is sitting on, and the offer to merge it anywhere.
        Assert.Contains("guardrails/plan", io.OutText, StringComparison.Ordinal);
        Assert.Contains("wherever you want it", io.OutText, StringComparison.Ordinal);

        // The false claim this issue was filed for must be GONE.
        Assert.DoesNotContain($"delivered to {startingBranch}", io.OutText, StringComparison.Ordinal);

        // Nothing was delivered: neither branch advanced and the deliverable is not in the checkout.
        Assert.Equal(headBeforeRun, repo.HeadSha());
        Assert.Equal(headBeforeRun, TempGitRepo.Git(repo.RepoPath, "rev-parse", startingBranch).Trim());
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "app.cs")));
    }

    /// <summary>
    /// Drive the real <c>run</c> command pipeline. Output goes to a discarded
    /// <see cref="StringConsoleIo"/> so nothing touches the process-global console (parallel-safe).
    /// </summary>
    private static Task<int> InvokeCliAsync(params string[] args) =>
        InvokeCliAsync(new StringConsoleIo(), args);

    /// <summary>Overload that drives the <c>run</c> command through a caller-supplied <see cref="StringConsoleIo"/> so a test can assert the console output.</summary>
    private static async Task<int> InvokeCliAsync(StringConsoleIo io, params string[] args)
    {
        var root = new RootCommand("merge-on-success cli test root");
        root.Add(RunCommand.Create(io));
        return await root.Parse(args).InvokeAsync();
    }
}
