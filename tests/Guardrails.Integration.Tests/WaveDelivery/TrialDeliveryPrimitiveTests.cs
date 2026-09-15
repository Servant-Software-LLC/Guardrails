using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Red-bar tests for the trial-delivery primitive (design 39 §1, review round 4
/// "d39-trial-delivery-primitive"): a delivering wave merges the plan branch onto a scratch ref
/// <c>refs/guardrails/trial/&lt;waveDir&gt;</c> WITH the user's git hooks, so the wave's exit gate can
/// re-check #588 (branch moved) and #448 (dirty tree) against the trial ref before the fast-forward
/// that lands it on the user's real branch.
/// <para>
/// Every row below calls <see cref="IWorktreeProvider.CreateTrialDelivery"/>,
/// <see cref="IWorktreeProvider.PromoteTrialDelivery"/> or
/// <see cref="IWorktreeProvider.DiscardTrialDelivery"/> on an <see cref="IWorktreeProvider"/>-typed
/// variable holding a REAL <see cref="GitWorktreeProvider"/> over a temp git repo — those three
/// members are default interface members that currently just <c>throw new NotImplementedException()</c>,
/// so every test here is red on arrival. Do NOT implement them here (task 31's job); this file only
/// pins the contract.
/// </para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class TrialDeliveryPrimitiveTests
{
    private const string WaveDir = "wave-01";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (MergeOnSuccessTests / GitHookIsolationTests), plus the two
    // hook-installation shapes this primitive must run: a plain .git/hooks/pre-commit, and a
    // husky-style RELATIVE core.hooksPath hook that is never committed.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-trial-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# trial delivery primitive test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public bool RefExists(string fullRefName)
        {
            var (_, exit) = TryGit(RepoPath, "show-ref", "--verify", "--quiet", fullRefName);
            return exit == 0;
        }

        /// <summary>
        /// Install a <c>pre-commit</c> hook (the shared common <c>.git/hooks</c>, so it fires in
        /// every worktree of this repo) that exits 1 after printing <paramref name="message"/> to
        /// stderr. When <paramref name="markerAbsolutePath"/> is given the hook also writes that file
        /// FIRST, so a test can prove the hook never ran by asserting the marker's absence.
        /// </summary>
        public void InstallFailingPreCommitHook(string message, string? markerAbsolutePath = null)
        {
            string hooksDir = Path.Combine(RepoPath, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            string hookPath = Path.Combine(hooksDir, "pre-commit");
            string markerLine = markerAbsolutePath is null
                ? ""
                : $"echo marker > \"{ToShPath(markerAbsolutePath)}\"\n";
            File.WriteAllText(hookPath, "#!/bin/sh\n" + markerLine + "echo '" + message + "' 1>&2\nexit 1\n");
            MakeExecutable(hookPath);
        }

        /// <summary>Install a passing (exit 0) <c>pre-commit</c> hook in the shared common hooks dir.</summary>
        public void InstallPassingPreCommitHook()
        {
            string hooksDir = Path.Combine(RepoPath, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            string hookPath = Path.Combine(hooksDir, "pre-commit");
            File.WriteAllText(hookPath, "#!/bin/sh\nexit 0\n");
            MakeExecutable(hookPath);
        }

        /// <summary>
        /// The husky layout: set this repo's LOCAL <c>core.hooksPath</c> to the RELATIVE path
        /// <c>.husky/_</c> and drop a failing, output-writing <c>pre-commit</c> hook there WITHOUT
        /// committing it. Git resolves a relative <c>core.hooksPath</c> against the CURRENT worktree,
        /// not against wherever <c>.git/config</c> lives — this fixture is what tells that resolution
        /// apart from the plain <c>.git/hooks</c> shape above.
        /// </summary>
        public void InstallFailingRelativeHooksPathHook(string message)
        {
            string huskyDir = Path.Combine(RepoPath, ".husky", "_");
            Directory.CreateDirectory(huskyDir);
            Git(RepoPath, "config", "core.hooksPath", ".husky/_");
            string hookPath = Path.Combine(huskyDir, "pre-commit");
            File.WriteAllText(hookPath, "#!/bin/sh\necho '" + message + "' 1>&2\nexit 1\n");
            MakeExecutable(hookPath);
        }

        private static string ToShPath(string path) => path.Replace('\\', '/');

        private static void MakeExecutable(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
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

        public static (string Stdout, int ExitCode) TryGit(string workingDir, params string[] args)
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
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (stdout, proc.ExitCode);
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Shared fixture helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Write and commit a file — used to build up both the plan branch and the user's branch.</summary>
    private static void WriteAndCommit(string workingDir, string relPath, string content, string message)
    {
        string full = Path.Combine(workingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        TempGitRepo.Git(workingDir, "add", "-A");
        TempGitRepo.Git(workingDir, "commit", "--no-verify", "-m", message);
    }

    private static void CommitOnPlanBranch(IntegrationHandle integ, string relPath, string content, string message) =>
        WriteAndCommit(integ.IntegrationWorktreePath, relPath, content, message);

    private static void CommitOnUserBranch(TempGitRepo repo, string relPath, string content, string message) =>
        WriteAndCommit(repo.RepoPath, relPath, content, message);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    /// <summary>The registered worktree paths for <paramref name="repoPath"/>, normalized and sorted, for before/after comparisons.</summary>
    private static IReadOnlyList<string> WorktreeListing(string repoPath) =>
        TempGitRepo.Git(repoPath, "worktree", "list", "--porcelain")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => NormalizePath(l["worktree ".Length..]))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // CreateTrialDelivery — case 4 (a merge commit is needed) keeps the user's git hooks.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The row that separates the maintainer's answer from the rejected <c>--no-verify</c> option:
    /// when the user's branch gained a commit mid-run, building the trial needs a real merge commit,
    /// and that merge commit runs the user's hooks. A failing <c>pre-commit</c> must reject it.
    /// Rejects: a trial merge made with <c>--no-verify</c>, or with <c>git commit-tree</c>, which
    /// runs no hook — both would leave this test green against a broken implementation.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ATrialMergeCommit_RunsTheUsersHooks_SoAHookCanRejectIt()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-hook", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        string userTip = repo.HeadSha();

        repo.InstallFailingPreCommitHook("ggshield: offline, refusing commit");
        IReadOnlyList<string> worktreesBefore = WorktreeListing(repo.RepoPath);

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.HookRejected, trial.Refusal);
        Assert.NotNull(trial.RefusalDetail);
        Assert.Contains("ggshield", trial.RefusalDetail!, StringComparison.Ordinal);
        Assert.Null(trial.Commit);
        Assert.False(repo.RefExists($"refs/guardrails/trial/{WaveDir}"));
        Assert.Equal(worktreesBefore, WorktreeListing(repo.RepoPath));
        Assert.Equal(userTip, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    /// <summary>
    /// The husky layout, measured in review (2026-09-13, Windows git 2.53): a RELATIVE
    /// <c>core.hooksPath</c> must still be honored for the trial merge commit. Rejects: committing
    /// the trial merge with git's own hook lookup inside the harness worktree — git resolves a
    /// relative <c>core.hooksPath</c> against THAT worktree, finds nothing there (the hook lives
    /// only in the user's repo, uncommitted), and lets the commit through unchecked. The plain
    /// <c>.git/hooks</c> fixture in the row above cannot see this bug.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ATrialMergeCommit_RunsHooksFromARelativeUntrackedHooksPath()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-husky", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        string userTip = repo.HeadSha();

        repo.InstallFailingRelativeHooksPathHook("husky: pre-commit failed");
        IReadOnlyList<string> worktreesBefore = WorktreeListing(repo.RepoPath);

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.HookRejected, trial.Refusal);
        Assert.NotNull(trial.RefusalDetail);
        Assert.Contains("husky", trial.RefusalDetail!, StringComparison.Ordinal);
        Assert.Null(trial.Commit);
        Assert.False(repo.RefExists($"refs/guardrails/trial/{WaveDir}"));
        Assert.Equal(worktreesBefore, WorktreeListing(repo.RepoPath));
        Assert.Equal(userTip, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // CreateTrialDelivery — the four cases, and the ancestry/first-parent invariants they pin.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The quiet case: the user's tip is a strict ancestor of the plan tip, so no merge commit is
    /// needed and the trial ref points straight at the plan tip. Rejects: always creating a merge
    /// commit — the installed failing hook would reject it, but a correct quiet-case build never
    /// runs it at all.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void TheQuietCase_CreatesNoMergeCommit_AndRunsNoHook()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-quiet", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        string planTip = TempGitRepo.Git(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();

        repo.InstallFailingPreCommitHook("must not run");

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.True(trial.UserTipWasAncestor);
        Assert.False(trial.AlreadyDelivered);
        Assert.Null(trial.Refusal);
        Assert.Equal(planTip, trial.Commit);
        Assert.Null(trial.WorktreePath);
        Assert.True(repo.RefExists($"refs/guardrails/trial/{WaveDir}"));
        Assert.Equal(planTip, TempGitRepo.Git(repo.RepoPath, "rev-parse", trial.TrialRef).Trim());
    }

    /// <summary>
    /// <see cref="TrialDelivery.UserTipWasAncestor"/> both ways: true when the user's branch never
    /// moved past the base the plan branch forked from, false once the user's branch gains its own
    /// commit and a merge is genuinely required. Rejects: a constant, and comparing tips for equality
    /// instead of ancestry (equal tips is the <see cref="TrialDelivery.AlreadyDelivered"/> case, a
    /// different row, not this one).
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void UserTipWasAncestor_IsCorrectBothWays()
    {
        using var repoTrue = new TempGitRepo();
        IWorktreeProvider providerTrue = new GitWorktreeProvider(repoTrue.RepoPath, repoTrue.WorktreeRoot);
        IntegrationHandle integTrue =
            providerTrue.CreateIntegration("trial-plan", "run-ancestor-true", CancellationToken.None);
        CommitOnPlanBranch(integTrue, "plan.txt", "plan work", "plan commit");

        TrialDelivery trialTrue = providerTrue.CreateTrialDelivery(integTrue, WaveDir, CancellationToken.None);
        Assert.True(trialTrue.UserTipWasAncestor);

        using var repoFalse = new TempGitRepo();
        IWorktreeProvider providerFalse = new GitWorktreeProvider(repoFalse.RepoPath, repoFalse.WorktreeRoot);
        IntegrationHandle integFalse =
            providerFalse.CreateIntegration("trial-plan", "run-ancestor-false", CancellationToken.None);
        CommitOnPlanBranch(integFalse, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repoFalse, "user.txt", "user work", "user advance");

        TrialDelivery trialFalse = providerFalse.CreateTrialDelivery(integFalse, WaveDir, CancellationToken.None);
        Assert.False(trialFalse.UserTipWasAncestor);
    }

    /// <summary>
    /// In the moved case, <c>&lt;Commit&gt;^1</c> is the user's tip and <c>&lt;Commit&gt;^2</c> is
    /// the plan tip, so after promotion the user's <c>--first-parent</c> history is their own line —
    /// exactly as today's run-end merge commit leaves it. Rejects: merging the user's tip INTO the
    /// plan branch's line (the parents swapped).
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void TheTrialMergeCommit_HasTheUsersTipAsFirstParent()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-parents", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        string planTip = TempGitRepo.Git(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        string userTip = repo.HeadSha();

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.NotNull(trial.Commit);
        string firstParent = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{trial.Commit}^1").Trim();
        string secondParent = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{trial.Commit}^2").Trim();
        Assert.Equal(userTip, firstParent);
        Assert.Equal(planTip, secondParent);
    }

    /// <summary>
    /// The moved case leaves a harness-owned worktree checked out at the trial commit — so the
    /// Scheduler can run the wave's exit gate there — until <see cref="IWorktreeProvider.DiscardTrialDelivery"/>
    /// removes it; the quiet case creates no worktree at all. Rejects: removing the worktree before the
    /// exit gate can run in it, and leaking one worktree per delivery.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void TheMovedCase_KeepsAWorktreeAtTheTrialCommit_UntilDiscarded()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-worktree", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.NotNull(trial.WorktreePath);
        Assert.True(Directory.Exists(trial.WorktreePath));
        Assert.Equal(trial.Commit, TempGitRepo.Git(trial.WorktreePath!, "rev-parse", "HEAD").Trim());
        Assert.NotEqual(NormalizePath(trial.WorktreePath!), NormalizePath(repo.RepoPath));
        Assert.NotEqual(NormalizePath(trial.WorktreePath!), NormalizePath(integ.IntegrationWorktreePath));

        using var quietRepo = new TempGitRepo();
        IWorktreeProvider quietProvider = new GitWorktreeProvider(quietRepo.RepoPath, quietRepo.WorktreeRoot);
        IntegrationHandle quietInteg =
            quietProvider.CreateIntegration("trial-plan", "run-worktree-quiet", CancellationToken.None);
        CommitOnPlanBranch(quietInteg, "plan.txt", "plan work", "plan commit");
        TrialDelivery quietTrial = quietProvider.CreateTrialDelivery(quietInteg, WaveDir, CancellationToken.None);
        Assert.Null(quietTrial.WorktreePath);

        provider.DiscardTrialDelivery(integ, WaveDir);
        Assert.False(Directory.Exists(trial.WorktreePath));
        Assert.DoesNotContain(
            WorktreeListing(repo.RepoPath),
            p => string.Equals(p, NormalizePath(trial.WorktreePath!), StringComparison.Ordinal));
    }

    /// <summary>
    /// A trial that conflicts is refused WITH the conflicting paths, newline-separated and
    /// ordinal-sorted — the format #448 uses for dirty paths. Committing <c>b.txt</c> before
    /// <c>a.txt</c> proves the ordering is a real sort, not creation order. Rejects: a
    /// <see cref="MergeOnSuccessResult.Conflict"/> with no detail — today's run-end conflict carries
    /// none, so a halt would read "delivery REFUSED (conflict):" with nothing after it.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ATrialThatConflicts_IsRefusedWithTheConflictingPaths()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();

        File.WriteAllText(Path.Combine(repo.RepoPath, "a.txt"), "base a\n");
        File.WriteAllText(Path.Combine(repo.RepoPath, "b.txt"), "base b\n");
        TempGitRepo.Git(repo.RepoPath, "add", "-A");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "baseline a.txt and b.txt");

        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-conflict", CancellationToken.None);

        CommitOnPlanBranch(integ, "a.txt", "plan a\n", "plan changes a");
        CommitOnPlanBranch(integ, "b.txt", "plan b\n", "plan changes b");

        // Commit b.txt BEFORE a.txt so ordinal order (a.txt, b.txt) differs from creation order.
        CommitOnUserBranch(repo, "b.txt", "user b\n", "user changes b");
        CommitOnUserBranch(repo, "a.txt", "user a\n", "user changes a");
        string userTip = repo.HeadSha();

        IReadOnlyList<string> worktreesBefore = WorktreeListing(repo.RepoPath);

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.Conflict, trial.Refusal);
        Assert.Equal("a.txt\nb.txt", trial.RefusalDetail);
        Assert.Null(trial.Commit);
        Assert.False(repo.RefExists($"refs/guardrails/trial/{WaveDir}"));
        Assert.Equal(worktreesBefore, WorktreeListing(repo.RepoPath));
        Assert.Equal(userTip, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    /// <summary>
    /// Building a trial never touches the user's checkout: with a PASSING hook, in the moved case,
    /// the user's HEAD sha, current branch, and working-tree status are identical before and after.
    /// Rejects: building the merge in the user's checkout and resetting afterwards.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void CreatingATrial_NeverTouchesTheUsersCheckout()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-untouched", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        repo.InstallPassingPreCommitHook();

        string headBefore = repo.HeadSha();
        string branchBefore = repo.CurrentBranch();
        string statusBefore = TempGitRepo.Git(repo.RepoPath, "status", "--porcelain");

        provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.Equal(headBefore, repo.HeadSha());
        Assert.Equal(branchBefore, repo.CurrentBranch());
        Assert.Equal(statusBefore, TempGitRepo.Git(repo.RepoPath, "status", "--porcelain"));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // CreateTrialDelivery — resuming after a promotion already landed must be idempotent, not a
    // second delivery and not a halt (the crash-after-promotion / resume shapes).
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A resume right after a quiet-case promotion landed: the user's tip now EQUALS the plan tip.
    /// Rebuilding the trial must recognize the work as already delivered rather than taking the
    /// quiet case again (which would re-announce a delivery that already landed, and after a
    /// switched checkout would wrongly refuse it as <see cref="MergeOnSuccessResult.BranchMoved"/>).
    /// The installed hook also writes a marker file, proving no hook ran on the rebuild.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ATrialRebuiltAfterAQuietPromotionLanded_IsAlreadyDelivered()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ =
            provider.CreateIntegration("trial-plan", "run-quiet-resume", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        string planTip = TempGitRepo.Git(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();

        TrialDelivery firstTrial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);
        MergeOnSuccessResult firstPromote = provider.PromoteTrialDelivery(integ, firstTrial, CancellationToken.None);
        Assert.Equal(MergeOnSuccessResult.FastForwarded, firstPromote);
        Assert.Equal(planTip, repo.HeadSha());

        provider.DiscardTrialDelivery(integ, WaveDir);

        string markerPath = Path.Combine(repo.RepoPath, "hook-ran.marker");
        repo.InstallFailingPreCommitHook("must not run", markerPath);

        TrialDelivery secondTrial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.True(secondTrial.AlreadyDelivered);
        Assert.True(secondTrial.UserTipWasAncestor);
        Assert.Null(secondTrial.Refusal);
        Assert.Equal(planTip, secondTrial.Commit);
        Assert.Equal(planTip, repo.HeadSha());
        Assert.Null(secondTrial.WorktreePath);
        Assert.False(File.Exists(markerPath));

        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", "some-other-branch");
        string otherBranchHeadBefore = repo.HeadSha();
        string originalBranchHeadBefore = TempGitRepo.Git(repo.RepoPath, "rev-parse", originalBranch).Trim();

        MergeOnSuccessResult secondPromote = provider.PromoteTrialDelivery(integ, secondTrial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.FastForwarded, secondPromote);
        Assert.Equal("some-other-branch", repo.CurrentBranch());
        Assert.Equal(otherBranchHeadBefore, repo.HeadSha());
        Assert.Equal(originalBranchHeadBefore, TempGitRepo.Git(repo.RepoPath, "rev-parse", originalBranch).Trim());
    }

    /// <summary>
    /// A resume after a crash that followed a MERGE-COMMIT promotion: the plan tip is now a strict
    /// ancestor of the user's tip (the promoted merge commit). Rebuilding the trial must recognize
    /// the work as already delivered rather than rebuilding the merge — <c>git merge</c> would report
    /// "Already up to date" and <c>git commit</c> would fail with "nothing to commit", and read as a
    /// hook rejection that failure would halt every resume. Measured in review.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ATrialRebuiltAfterItsPromotionLanded_IsAlreadyDelivered()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ =
            provider.CreateIntegration("trial-plan", "run-merge-resume", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");

        TrialDelivery firstTrial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);
        MergeOnSuccessResult firstPromote = provider.PromoteTrialDelivery(integ, firstTrial, CancellationToken.None);
        Assert.Equal(MergeOnSuccessResult.FastForwarded, firstPromote);
        string mergeCommit = repo.HeadSha();
        Assert.Equal(mergeCommit, firstTrial.Commit);

        provider.DiscardTrialDelivery(integ, WaveDir);

        string markerPath = Path.Combine(repo.RepoPath, "hook-ran.marker");
        repo.InstallFailingPreCommitHook("must not run", markerPath);

        TrialDelivery secondTrial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        Assert.True(secondTrial.AlreadyDelivered);
        Assert.False(secondTrial.UserTipWasAncestor);
        Assert.Null(secondTrial.Refusal);
        Assert.Equal(mergeCommit, secondTrial.Commit);
        Assert.Null(secondTrial.WorktreePath);
        Assert.False(File.Exists(markerPath));
        Assert.Equal(mergeCommit, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());

        MergeOnSuccessResult secondPromote = provider.PromoteTrialDelivery(integ, secondTrial, CancellationToken.None);
        Assert.Equal(MergeOnSuccessResult.FastForwarded, secondPromote);
        Assert.Equal(mergeCommit, repo.HeadSha());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // PromoteTrialDelivery — the #588/#448 re-checks and the fast-forward-only promotion.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Issue #588 re-checked at promotion time: if the checkout moved to a different branch after
    /// the trial was built, promotion refuses with <see cref="MergeOnSuccessResult.BranchMoved"/>
    /// naming both branches, and neither branch moves.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Promotion_RefusesWhenTheCheckoutMovedToAnotherBranch()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-588", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        string userTip = repo.HeadSha();

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        TempGitRepo.Git(repo.RepoPath, "checkout", "-b", "moved-elsewhere");
        string movedHead = repo.HeadSha();

        MergeOnSuccessResult result = provider.PromoteTrialDelivery(integ, trial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.BranchMoved, result);
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(originalBranch, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Contains("moved-elsewhere", provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Equal("moved-elsewhere", repo.CurrentBranch());
        Assert.Equal(movedHead, repo.HeadSha());
        Assert.Equal(userTip, TempGitRepo.Git(repo.RepoPath, "rev-parse", originalBranch).Trim());
    }

    /// <summary>
    /// Issue #448 re-checked at promotion time, narrowed to a real intersection: dirt in a file the
    /// fast-forward would overwrite refuses with <see cref="MergeOnSuccessResult.DirtyWorkingTree"/>
    /// and names that path; dirt in a file the trial does NOT change is not named and does not
    /// change the outcome — the same narrowing #448 applies to the end-of-run delivery.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Promotion_RefusesDirtTheFastForwardWouldOverwrite()
    {
        using var repo = new TempGitRepo();
        const string changedRelPath = "src/app.txt";
        const string disjointRelPath = "src/other.txt";

        CommitOnUserBranch(repo, changedRelPath, "base app\n", "baseline app.txt");
        CommitOnUserBranch(repo, disjointRelPath, "base other\n", "baseline other.txt");
        string userTip = repo.HeadSha();

        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-448-ff", CancellationToken.None);

        CommitOnPlanBranch(integ, changedRelPath, "plan app\n", "plan changes app");

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);

        File.WriteAllText(
            Path.Combine(repo.RepoPath, changedRelPath.Replace('/', Path.DirectorySeparatorChar)),
            "my uncommitted app WIP\n");
        File.WriteAllText(
            Path.Combine(repo.RepoPath, disjointRelPath.Replace('/', Path.DirectorySeparatorChar)),
            "my uncommitted other WIP\n");

        MergeOnSuccessResult result = provider.PromoteTrialDelivery(integ, trial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.DirtyWorkingTree, result);
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(changedRelPath, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.DoesNotContain(disjointRelPath, provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Equal(userTip, repo.HeadSha());
    }

    /// <summary>
    /// The user keeps working while the gate runs: after the trial was built, the user's branch
    /// advances with a new commit. Promotion refuses with <see cref="MergeOnSuccessResult.BranchMoved"/>
    /// and the exact wording the operator needs to resume ("advance ⇒ resume; the next trial includes
    /// their new commits"). Rejects: falling back to a real merge when <c>--ff-only</c> fails, as
    /// <see cref="IWorktreeProvider.MergePlanBranchIntoUserBranch"/> does, which would land a tree no
    /// gate saw.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Promotion_RefusesWhenTheUsersBranchAdvancedAfterTheTrial()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-advanced", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);
        string userTipAtTrial = trial.UserTip;

        CommitOnUserBranch(repo, "user.txt", "user keeps working", "user advances after trial");
        string newUserTip = repo.HeadSha();

        MergeOnSuccessResult result = provider.PromoteTrialDelivery(integ, trial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.BranchMoved, result);
        string expectedDetail =
            $"'{originalBranch}' moved from {userTipAtTrial[..10]} to {newUserTip[..10]} after the trial was built";
        Assert.Equal(expectedDetail, provider.LastMergeOnSuccessDetail);
        Assert.Equal(newUserTip, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    /// <summary>
    /// A rewind (<c>git reset --hard HEAD~1</c>) after the trial was built must also refuse — never
    /// rely on <c>--ff-only</c> failing to notice a moved branch, because after a rewind the
    /// fast-forward to the trial commit SUCCEEDS and would silently re-land the commit the user
    /// dropped. Both the dropped tip and the new (rewound) tip appear in the detail so the operator
    /// can tell an advance from a rewind.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Promotion_RefusesWhenTheUsersBranchWasRewoundAfterTheTrial()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-rewound", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");
        string droppedCommit = repo.HeadSha();

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);
        Assert.Equal(droppedCommit, trial.UserTip);

        TempGitRepo.Git(repo.RepoPath, "reset", "--hard", "HEAD~1");
        string rewoundTip = repo.HeadSha();

        MergeOnSuccessResult result = provider.PromoteTrialDelivery(integ, trial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.BranchMoved, result);
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(droppedCommit[..10], provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Contains(rewoundTip[..10], provider.LastMergeOnSuccessDetail!, StringComparison.Ordinal);
        Assert.Equal(rewoundTip, repo.HeadSha());

        var (_, exit) = TempGitRepo.TryGit(repo.RepoPath, "merge-base", "--is-ancestor", droppedCommit, rewoundTip);
        Assert.NotEqual(0, exit);
    }

    /// <summary>
    /// The clean promotion path: fast-forward the user's branch to the trial commit — the same sha
    /// the gate already saw, so no further commit is made and the tree that landed is provably the
    /// tree that was checked.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Promotion_FastForwardsTheUsersBranchToTheTrialCommit()
    {
        using var repo = new TempGitRepo();
        IWorktreeProvider provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("trial-plan", "run-ff-promote", CancellationToken.None);

        CommitOnPlanBranch(integ, "plan.txt", "plan work", "plan commit");
        CommitOnUserBranch(repo, "user.txt", "user work", "user advance");

        TrialDelivery trial = provider.CreateTrialDelivery(integ, WaveDir, CancellationToken.None);
        Assert.NotNull(trial.Commit);

        MergeOnSuccessResult result = provider.PromoteTrialDelivery(integ, trial, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.FastForwarded, result);
        Assert.Equal(trial.Commit, repo.HeadSha());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // DiscardTrialDelivery.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// After a trial in the quiet case, the moved case, and even when nothing was ever built,
    /// <see cref="IWorktreeProvider.DiscardTrialDelivery"/> leaves no trial ref and never throws —
    /// including on a second, redundant call.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Discard_RemovesTheTrialRef()
    {
        using (var quietRepo = new TempGitRepo())
        {
            IWorktreeProvider quietProvider = new GitWorktreeProvider(quietRepo.RepoPath, quietRepo.WorktreeRoot);
            IntegrationHandle quietInteg =
                quietProvider.CreateIntegration("trial-plan", "run-discard-quiet", CancellationToken.None);
            CommitOnPlanBranch(quietInteg, "plan.txt", "plan work", "plan commit");
            quietProvider.CreateTrialDelivery(quietInteg, WaveDir, CancellationToken.None);

            quietProvider.DiscardTrialDelivery(quietInteg, WaveDir);
            Assert.False(quietRepo.RefExists($"refs/guardrails/trial/{WaveDir}"));
            quietProvider.DiscardTrialDelivery(quietInteg, WaveDir);
        }

        using (var movedRepo = new TempGitRepo())
        {
            IWorktreeProvider movedProvider = new GitWorktreeProvider(movedRepo.RepoPath, movedRepo.WorktreeRoot);
            IntegrationHandle movedInteg =
                movedProvider.CreateIntegration("trial-plan", "run-discard-moved", CancellationToken.None);
            CommitOnPlanBranch(movedInteg, "plan.txt", "plan work", "plan commit");
            CommitOnUserBranch(movedRepo, "user.txt", "user work", "user advance");
            movedProvider.CreateTrialDelivery(movedInteg, WaveDir, CancellationToken.None);

            movedProvider.DiscardTrialDelivery(movedInteg, WaveDir);
            Assert.False(movedRepo.RefExists($"refs/guardrails/trial/{WaveDir}"));
            movedProvider.DiscardTrialDelivery(movedInteg, WaveDir);
        }

        using (var freshRepo = new TempGitRepo())
        {
            IWorktreeProvider freshProvider = new GitWorktreeProvider(freshRepo.RepoPath, freshRepo.WorktreeRoot);
            IntegrationHandle freshInteg =
                freshProvider.CreateIntegration("trial-plan", "run-discard-none", CancellationToken.None);
            freshProvider.DiscardTrialDelivery(freshInteg, WaveDir);
        }
    }
}
