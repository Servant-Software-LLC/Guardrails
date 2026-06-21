using System.Diagnostics;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests encoding plan 08 §7 / Stage-2 BEFORE the resume reconciliation
/// and reset-retry surface exist. All tests reference not-yet-existing methods on
/// <see cref="GitWorktreeProvider"/>, so the project will NOT compile against current code —
/// that compile failure IS the red-bar signal. Do NOT implement anything here;
/// tests only, in this one file.
///
/// Scenarios (four tests):
/// <list type="bullet">
///   <item><b>Resume_AfterFF_BeforeJournal</b> — kill after the FF commit lands (B1 step 2)
///     but before the journal write (B1 step 3); <see cref="GitWorktreeProvider.ReconcileFromPlanBranch"/>
///     reads task succeeded PURELY from the plain commit trailer reachable from the plan-branch tip,
///     without consulting the journal. No re-run, no double-integrate.</item>
///   <item><b>Resume_AfterFragment_BeforeCommit</b> (B1 reverse window companion) — state.json
///     has the fragment (B1 step 1 done) but the git commit (B1 step 2) never landed on the plan
///     branch; trailer NOT reachable from plan-branch tip → task must re-run. The fragment
///     re-merge is idempotent (own namespace key).</item>
///   <item><b>Resume_IgnoresStaleSegmentRef_W1</b> — crash after segment commit but before FF;
///     <see cref="GitWorktreeProvider.PruneStaleRunBranches"/> deletes all
///     <c>guardrails/&lt;runId&gt;/*</c> refs BEFORE any trailer read; the stale segment ref
///     is gone; <see cref="GitWorktreeProvider.ReconcileFromPlanBranch"/> finds no trailer on
///     the plan-branch tip → task re-runs.</item>
///   <item><b>Retry_PreservesUpstreamCommits_TaskBase_NeqPreHead</b> —
///     <see cref="GitWorktreeProvider.ResetForRetry"/> resets the segment to
///     <c>handle.TaskBase</c> (the upstream task's recorded commit sha), NOT to the plan-branch
///     integration <c>preHead</c> (the original plan-branch HEAD before the upstream ran);
///     the upstream's committed file survives; the failing task's uncommitted WIP is removed by
///     <c>git clean -fd</c>; git-ignored build caches survive the clean (Decision 7).</item>
/// </list>
/// </summary>
public sealed class ResumeAndResetRetryTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — Windows-safe temp repo + worktree root (strips read-only bits before delete).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-rrr-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# resume-retry-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Returns the HEAD sha of the given working directory's current branch.</summary>
        public string HeadSha(string workingDir) =>
            Git(workingDir, "rev-parse", "HEAD").Trim();

        /// <summary>True when <paramref name="relativePath"/> exists under <paramref name="workingDir"/>.</summary>
        public bool FileExistsIn(string workingDir, string relativePath) =>
            File.Exists(Path.Combine(workingDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>True when <paramref name="branchName"/> is a known ref in the repo.</summary>
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

        /// <summary>
        /// Runs a git command in <paramref name="workingDir"/>, throwing on non-zero exit.
        /// Returns the trimmed stdout.
        /// </summary>
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
    // CommitFile helper
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="relPath"/> (using '/' separators)
    /// under <paramref name="workingDir"/>, stages all changes, commits with
    /// <paramref name="message"/>, and returns the resulting HEAD sha.
    /// </summary>
    private static string CommitFile(string workingDir, string relPath, string content, string message)
    {
        string fullPath = Path.Combine(workingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        TempGitRepo.Git(workingDir, "add", ".");
        TempGitRepo.Git(workingDir, "commit", "-m", message);
        return TempGitRepo.Git(workingDir, "rev-parse", "HEAD").Trim();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 1 — resume-after-FF-before-journal
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §7 Stage-2 gate (crash window: after FF commit lands, before journal write):
    /// B1's success order is (1) state-fragment merge → (2) git integration commit →
    /// (3) journal <c>Succeeded</c> + consume <c>mergeSequence</c>. A crash between steps 2
    /// and 3 leaves the plan branch carrying the task's <c>Guardrails-Task:</c> trailer on a
    /// plain FF'd commit, but the journal has NO record of success.
    ///
    /// <see cref="GitWorktreeProvider.ReconcileFromPlanBranch"/> walks the plan-branch
    /// first-parent history, reads the trailer, and returns the task as settled — WITHOUT
    /// consulting the journal. Resume must not re-run the task and must not produce a second
    /// integration commit (no double-integrate). <c>ReconcileFromPlanBranch</c> is read-only.
    ///
    /// The <c>ReconcileFromPlanBranch</c> method does not yet exist on
    /// <see cref="GitWorktreeProvider"/> — calling it is the compile-error red-bar signal.
    /// </summary>
    [Fact]
    public void Resume_AfterFF_BeforeJournal_ReadsTrailerNotJournal()
    {
        using var repo = new TempGitRepo();
        string runId = "rrr-ff01";

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("my-plan", runId, CancellationToken.None);

        // ── Simulate B1 step 2: segment commit with Guardrails-Task trailer FF'd to plan branch ──
        // This is the state after the git integration commit but before the journal write (step 3).
        // We use the same trailer format as GitWorktreeProvider.Integrate: one line per key.
        string segBranch = $"guardrails/{runId}/01-task-a/attempt-1";
        string segPath = Path.Combine(repo.WorktreeRoot, runId, "01-task-a", "attempt-1");
        Directory.CreateDirectory(segPath);
        TempGitRepo.Git(repo.RepoPath, "worktree", "add", "-b", segBranch, segPath, integ.PlanBranchName);

        CommitFile(segPath, "src/TaskA.cs", "class TaskA {}",
            $"Guardrails-Task: 01-task-a\nGuardrails-Run: {runId}");

        // FF the integration worktree / plan branch to the segment commit (B1 step 2)
        TempGitRepo.Git(integ.IntegrationWorktreePath, "merge", "--ff-only", segBranch);
        string planTipAfterFF = repo.HeadSha(integ.IntegrationWorktreePath);

        // B1 step 3 (journal write) was NOT done — crash window simulated.
        // The journal has no record of 01-task-a succeeding; the plan-branch tip has the trailer.

        // ── COMPILE ERROR on current code: 'GitWorktreeProvider' has no 'ReconcileFromPlanBranch'.
        // This method is introduced by the §7 resume reconciliation refactor (plan 08 Stage-2). ────
        IReadOnlySet<string> settled = provider.ReconcileFromPlanBranch(integ, runId);

        // ── Trailer IS reachable from the plan-branch tip → 01-task-a must be in the settled set ──
        Assert.True(settled.Contains("01-task-a"),
            "Crash window (after FF, before journal): the Guardrails-Task trailer is reachable " +
            "from the plan-branch tip via first-parent walk, so resume-by-trailer must return " +
            "this task as settled without consulting the journal.");

        // ── ReconcileFromPlanBranch is READ-ONLY — must not produce a second integration commit ───
        // If the implementation accidentally committed again, the plan-branch tip would advance.
        string planTipAfterReconcile = repo.HeadSha(integ.IntegrationWorktreePath);
        Assert.True(planTipAfterFF == planTipAfterReconcile,
            "ReconcileFromPlanBranch must be a read-only trailer scan — it must NOT commit to " +
            "the plan branch (no double-integrate).");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 2 — resume-after-fragment-before-commit (B1 reverse window companion)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §7 companion (B1 reverse crash window: after fragment write, before git commit):
    /// B1 step 1 (fragment merge into state.json) completed but B1 step 2 (the git integration
    /// commit) never landed on the plan branch. The plan-branch tip carries NO <c>Guardrails-Task:</c>
    /// trailer for the task. Resume must treat the task as NOT settled and re-run it.
    ///
    /// This proves the state-fragment → git-commit ordering is crash-safe in BOTH directions:
    /// forward (test 1: commit without journal → resume-by-trailer recovers) and reverse
    /// (this test: fragment without commit → no false-certify, task re-runs). The fragment
    /// re-merge on the resumed run is idempotent — the task writes to its own namespace key
    /// and the second merge simply overwrites with the same value.
    ///
    /// The <c>ReconcileFromPlanBranch</c> compile error is the same red-bar signal as test 1.
    /// </summary>
    [Fact]
    public void Resume_AfterFragment_BeforeCommit_NotSettled()
    {
        using var repo = new TempGitRepo();
        string runId = "rrr-frag";

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("my-plan", runId, CancellationToken.None);

        // ── Simulate B1 step 1 complete (fragment in state.json), step 2 NOT done ──────────────
        // We write the fragment directly to state.json (as the executor's B1 step 1 would do),
        // but we do NOT create any segment commit and do NOT FF the plan branch.
        // The plan-branch tip therefore carries NO Guardrails-Task trailer for 01-task-a.
        string planDir = Path.Combine(repo.RepoPath, "my-plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(
            Path.Combine(planDir, "state", "state.json"),
            """{ "01-task-a": { "done": true } }""");

        string planTipBeforeReconcile = repo.HeadSha(integ.IntegrationWorktreePath);

        // ── COMPILE ERROR on current code: 'GitWorktreeProvider' has no 'ReconcileFromPlanBranch'. ─
        IReadOnlySet<string> settled = provider.ReconcileFromPlanBranch(integ, runId);

        // ── No trailer on the plan-branch tip → task is NOT settled, must re-run ────────────────
        Assert.False(settled.Contains("01-task-a"),
            "B1 reverse window: state.json has the task's fragment but no commit reached the " +
            "plan branch. ReconcileFromPlanBranch must NOT certify the task as settled — a " +
            "fragment alone is not authoritative; only a plan-branch-reachable trailer-bearing " +
            "commit is. The task must re-run (fragment re-merge is idempotent).");

        // ── Plan-branch tip unchanged (reconcile is read-only) ────────────────────────────────
        Assert.True(planTipBeforeReconcile == repo.HeadSha(integ.IntegrationWorktreePath),
            "ReconcileFromPlanBranch must not modify the plan branch.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 3 — resume-ignores-stale-segment-ref (W-1)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §7 W-1 gate: a crash between the segment commit (on the segment branch) and its
    /// FF into the plan branch leaves a <c>guardrails/&lt;runId&gt;/01-task-a/attempt-1</c> branch
    /// pointing at a commit bearing <c>Guardrails-Task: 01-task-a</c> — but that commit is
    /// NOT reachable from the plan-branch tip.
    ///
    /// Two ordered protections:
    /// <list type="number">
    ///   <item><see cref="GitWorktreeProvider.PruneStaleRunBranches"/> deletes all
    ///     <c>guardrails/&lt;runId&gt;/*</c> refs BEFORE any resume logic reads trailers,
    ///     so a stale segment ref cannot even be consulted — not even by a buggy implementation
    ///     that walks beyond the plan-branch tip.</item>
    ///   <item><see cref="GitWorktreeProvider.ReconcileFromPlanBranch"/> walks ONLY from the
    ///     plan-branch tip — it never reads trailers on refs not reachable from the tip.</item>
    /// </list>
    ///
    /// Both methods are compile-error red-bar signals on current code.
    /// The test asserts the ORDERING invariant by calling prune FIRST and verifying the stale
    /// ref is gone BEFORE the reconcile call.
    /// </summary>
    [Fact]
    public void Resume_IgnoresStaleSegmentRef_W1()
    {
        using var repo = new TempGitRepo();
        string runId = "rrr-w1xx";

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("my-plan", runId, CancellationToken.None);

        // ── Set up: segment commit with Guardrails-Task trailer, NOT FF'd to the plan branch ────
        // This is the crash window: segment committed (the W-1 hazard exists) but the FF that
        // would have landed it on the plan branch never happened.
        string segBranch = $"guardrails/{runId}/01-task-a/attempt-1";
        string segPath = Path.Combine(repo.WorktreeRoot, runId, "01-task-a", "attempt-1");
        Directory.CreateDirectory(segPath);
        TempGitRepo.Git(repo.RepoPath, "worktree", "add", "-b", segBranch, segPath, integ.PlanBranchName);

        CommitFile(segPath, "src/TaskA.cs", "class TaskA {}",
            $"Guardrails-Task: 01-task-a\nGuardrails-Run: {runId}");

        // ── Confirm the W-1 hazard: the stale segment branch HAS the trailer ─────────────────
        string segLog = TempGitRepo.Git(segPath, "log", "--format=%B", "-n1").Trim();
        Assert.True(segLog.Contains("Guardrails-Task: 01-task-a", StringComparison.Ordinal),
            "Setup: the stale segment branch must carry the Guardrails-Task trailer (W-1 hazard).");

        // ── Confirm the plan-branch tip does NOT have the task's trailer (FF never happened) ───
        string planLog = TempGitRepo.Git(integ.IntegrationWorktreePath, "log", "--format=%B").Trim();
        Assert.False(planLog.Contains("Guardrails-Task: 01-task-a", StringComparison.Ordinal),
            "Setup: the Guardrails-Task trailer must NOT be reachable from the plan-branch tip.");

        // ── COMPILE ERROR on current code: 'GitWorktreeProvider' has no 'PruneStaleRunBranches'.
        // W-1 protection (a): prune BEFORE any trailer read — deletes guardrails/<runId>/* refs.
        // The naming convention (guardrails/<runId>/<segmentId>/attempt-N) means the glob matches
        // all per-run segment and per-attempt branches while leaving the plan branch untouched
        // (guardrails/<planName> does not match guardrails/<runId>/*). ─────────────────────────
        provider.PruneStaleRunBranches(runId, integ);

        // ── Stale segment branch must be gone BEFORE the reconcile call ───────────────────────
        // The ordering invariant: prune removes stale refs before ReconcileFromPlanBranch is
        // ever invoked, so even a buggy implementation that walked all refs would find nothing.
        Assert.False(repo.BranchExists(segBranch),
            $"W-1: PruneStaleRunBranches must delete the stale segment branch '{segBranch}' " +
            $"before ReconcileFromPlanBranch is called.");

        // ── COMPILE ERROR on current code: 'GitWorktreeProvider' has no 'ReconcileFromPlanBranch'.
        // W-1 protection (b): walk ONLY from the plan-branch tip. ──────────────────────────────
        IReadOnlySet<string> settled = provider.ReconcileFromPlanBranch(integ, runId);

        // ── Task NOT in settled set: stale ref gone AND not reachable from plan-branch tip ─────
        Assert.True(!settled.Any(),
            "W-1: after pruning the stale segment branch, ReconcileFromPlanBranch must return " +
            "an empty set. The task commit never reached the plan-branch tip, so the task must " +
            "re-run on resume — the stale ref is NOT authoritative.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 4 — retry-preserves-upstream-commits (taskBase ≠ preHead)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §7 / §1 Stage-2 gate: <see cref="GitWorktreeProvider.ResetForRetry"/> resets a
    /// reused segment worktree to <c>handle.TaskBase</c> (the upstream's recorded commit sha,
    /// set by <see cref="IWorktreeProvider.ReuseSegment"/> to the upstream's
    /// <see cref="WorktreeHandle.RecordedCommitSha"/>), NOT to the plan-branch integration
    /// <c>preHead</c> (the plan-branch HEAD before the upstream task ran). Conflating the two is
    /// the corruption bug (§1): resetting to <c>preHead</c> discards the upstream's committed
    /// work from the segment.
    ///
    /// The test also pins <b>Decision 7</b>: <c>git clean -fd</c> (keep git-ignored build caches)
    /// is used, NOT <c>-fdx</c> (which would delete them). A git-ignored <c>bin/</c> directory
    /// simulates a warm build cache; it must survive the clean so retries benefit from warm
    /// artifacts. The false-green concern is closed by guardrails re-running against real compiled
    /// bytes — the cache is an input, not the verdict.
    ///
    /// The <c>ResetForRetry</c> method does not yet exist on <see cref="GitWorktreeProvider"/>
    /// — calling it is the compile-error red-bar signal.
    /// </summary>
    [Fact]
    public void Retry_PreservesUpstreamCommits_TaskBase_NeqPreHead()
    {
        using var repo = new TempGitRepo();
        string runId = "rrr-ret1";

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("my-plan", runId, CancellationToken.None);

        // ── preHead: the plan-branch HEAD BEFORE any segment task commits ─────────────────────
        // A buggy ResetForRetry that resets to preHead instead of taskBase would discard A's work.
        string preHead = repo.HeadSha(integ.IntegrationWorktreePath);

        // ── Create the shared segment worktree (tasks A and B reuse the same tree) ─────────────
        string segBranch = $"guardrails/{runId}/01-task-a/attempt-1";
        string segPath = Path.Combine(repo.WorktreeRoot, runId, "01-task-a", "attempt-1");
        Directory.CreateDirectory(segPath);
        TempGitRepo.Git(repo.RepoPath, "worktree", "add", "-b", segBranch, segPath, integ.PlanBranchName);

        // ── Task A runs and commits: .gitignore (so bin/ is ignored) then A.cs ─────────────────
        // The .gitignore is committed before A.cs so the segment repo ignores bin/ from the start.
        CommitFile(segPath, ".gitignore", "bin/\n", "add .gitignore");
        string aCommitSha = CommitFile(segPath, "src/TaskA.cs", "class TaskA {}",
            $"Guardrails-Task: 01-task-a\nGuardrails-Run: {runId}");

        // ── taskBase for B = A's commit sha (distinct from preHead = plan-branch HEAD before A) ─
        // ReuseSegment sets TaskBase = upstreamSegment.RecordedCommitSha = aCommitSha.
        // preHead is the plan-branch HEAD BEFORE A ran; aCommitSha is AFTER A committed.
        // They are the same only if A was already FF'd into the plan branch — not the case here
        // (A's segment hasn't been integrated yet, so preHead is still the original HEAD).
        Assert.True(preHead != aCommitSha,
            "taskBase (A's commit sha) must be distinct from preHead (plan-branch HEAD before A) " +
            "for this test to cover the taskBase ≠ preHead invariant.");

        // ── Simulate a warm build cache in a git-ignored directory ────────────────────────────
        // Decision 7: git clean -fd (not -fdx) keeps git-ignored files (build caches).
        string binDir = Path.Combine(segPath, "bin");
        Directory.CreateDirectory(binDir);
        string cacheFile = Path.Combine(binDir, "output.dll");
        File.WriteAllText(cacheFile, "fake-build-cache");

        // ── Task B's action writes B.cs but FAILS before Integrate is called ──────────────────
        // B.cs is uncommitted working-tree state. A real action/guardrail failure leaves the
        // file on disk but does not commit it — the segment branch tip is still aCommitSha.
        string taskBcsPath = Path.Combine(segPath, "src", "TaskB.cs");
        File.WriteAllText(taskBcsPath, "class TaskB {}");
        Assert.True(File.Exists(taskBcsPath), "Setup: B.cs must exist before ResetForRetry.");

        // B's WorktreeHandle: TaskBase = aCommitSha (set by ReuseSegment from A's RecordedCommitSha).
        // PlanBranchHead = preHead (the plan-branch HEAD this segment was forked from, BEFORE A ran).
        // The reset-retry must target TaskBase, NOT PlanBranchHead.
        var bHandle = new WorktreeHandle
        {
            WorktreePath = segPath,
            SegmentBranchName = segBranch,
            TaskBase = aCommitSha,   // ← A's recorded commit sha — the correct reset target
            RecordedCommitSha = aCommitSha,
            PlanBranchHead = preHead, // ← plan-branch preHead ≠ taskBase — the WRONG reset target
            TaskId = "02-task-b"
        };

        // ── COMPILE ERROR on current code: 'GitWorktreeProvider' has no 'ResetForRetry'. ───────
        // Introduced by the §7 / §1 reset-retry refactor. Executes:
        //   git reset --hard <handle.TaskBase>   (discards only this task's WIP; not preHead)
        //   git clean -fd                         (removes untracked non-ignored files; Decision 7)
        provider.ResetForRetry(bHandle);

        // ── After reset: A.cs must survive (it is in the committed aCommitSha = taskBase) ──────
        // If ResetForRetry had mistakenly targeted preHead instead of taskBase, A.cs would be
        // absent — the corruption bug. A.cs being present proves the reset targeted taskBase.
        Assert.True(repo.FileExistsIn(segPath, "src/TaskA.cs"),
            "ResetForRetry must target taskBase (A's commit), NOT preHead. " +
            "A's committed file must survive — a reset to preHead would discard it (corruption bug).");

        // ── After clean: B.cs is gone (uncommitted working-tree WIP above taskBase) ────────────
        // git reset --hard only resets tracked files; B.cs was never committed so it remains in
        // the working tree after the reset. git clean -fd then removes it (untracked, non-ignored).
        Assert.False(repo.FileExistsIn(segPath, "src/TaskB.cs"),
            "B's uncommitted WIP (src/TaskB.cs) must be removed by git clean -fd after the reset.");

        // ── git clean -fd (Decision 7): git-ignored build cache survives ─────────────────────
        // bin/output.dll is git-ignored (listed in .gitignore committed by A). git clean -fd
        // removes untracked non-ignored files but NOT ignored directories/files. If -fdx were
        // used instead, bin/ would be deleted — the cache would be lost and every retry would
        // pay a full cold rebuild.
        Assert.True(File.Exists(cacheFile),
            "git clean -fd (Decision 7) must preserve git-ignored files. " +
            "The build cache (bin/output.dll) must survive the retry clean.");
    }
}
