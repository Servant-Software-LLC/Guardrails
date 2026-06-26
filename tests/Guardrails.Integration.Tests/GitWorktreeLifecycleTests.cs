using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar tests encoding plan 08 §1 + M2 git worktree lifecycle invariants against
/// <see cref="GitWorktreeProvider"/> before it exists. The project will NOT compile against
/// current code — that compilation failure IS the expected signal.
/// Do NOT implement the provider here; tests only.
/// </summary>
public sealed class GitWorktreeLifecycleTests
{
    /// <summary>
    /// Throwaway single-use git repo in a temp directory. Provides a real repo root and a
    /// separate worktree root directory; cleaned up on dispose.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wt-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# Test repo");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Returns the abbreviated ref name of HEAD in the repo root (e.g. <c>main</c> or <c>master</c>).</summary>
        public string CurrentBranch() =>
            Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        /// <summary>Returns the full commit sha of HEAD in <paramref name="workingDir"/>.</summary>
        public string HeadSha(string workingDir) =>
            Git(workingDir, "rev-parse", "HEAD").Trim();

        /// <summary>Returns true when <paramref name="branch"/> exists as a local branch in the repo root.</summary>
        public bool BranchExists(string branch)
        {
            try { Git(RepoPath, "rev-parse", "--verify", $"refs/heads/{branch}"); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> is a linked git worktree — i.e. it contains a
        /// <c>.git</c> FILE (not a directory) pointing back to the shared object store.
        /// </summary>
        public static bool IsLinkedWorktree(string path) =>
            File.Exists(Path.Combine(path, ".git")) &&
            !Directory.Exists(Path.Combine(path, ".git"));

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="filename"/> inside
        /// <paramref name="workingDir"/>, stages it, commits it, and returns the new HEAD sha.
        /// </summary>
        public string CommitFile(string workingDir, string filename, string content, string message)
        {
            File.WriteAllText(Path.Combine(workingDir, filename), content);
            Git(workingDir, "add", filename);
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
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            // Windows-safe teardown (issue #109): git marks loose objects under .git/objects
            // read-only, so a plain Directory.Delete(recursive) throws UnauthorizedAccessException
            // (NOT IOException) on Windows. SafeDelete strips read-only attributes before deleting.
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort cleanup of a temp dir */ }
        }
    }

    /// <summary>
    /// At run start the provider creates a plan branch <c>guardrails/&lt;plan-name&gt;</c> off the
    /// user's current HEAD and a harness-owned integration worktree on it; the user's original
    /// branch and working tree are left untouched.
    /// </summary>
    [Fact]
    public void CreateIntegration_CreatesPlanBranch_AndIntegrationWorktree_OriginalBranchUntouched()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        string originalHead = repo.HeadSha(repo.RepoPath);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("my-plan", "run-001", CancellationToken.None);

        // Plan branch must exist and be named guardrails/<plan-name>
        Assert.True(repo.BranchExists("guardrails/my-plan"),
            "Expected plan branch 'guardrails/my-plan' to be created off the user's HEAD");
        Assert.Equal("guardrails/my-plan", integ.PlanBranchName);

        // Integration worktree directory must exist and be a real linked git worktree
        Assert.True(Directory.Exists(integ.IntegrationWorktreePath),
            "Integration worktree directory must exist on disk");
        Assert.True(TempGitRepo.IsLinkedWorktree(integ.IntegrationWorktreePath),
            "Integration worktree must be a linked git worktree (.git file, not a bare directory)");

        // Handle must record the original branch and HEAD so they can be restored on completion/failure
        Assert.Equal(originalBranch, integ.OriginalBranch);
        Assert.Equal(originalHead, integ.OriginalHeadSha);

        // The user's original branch and HEAD in the main repo must be untouched after CreateIntegration
        Assert.Equal(originalBranch, repo.CurrentBranch());
        Assert.Equal(originalHead, repo.HeadSha(repo.RepoPath));
    }

    /// <summary>
    /// Defense in depth (issue #160): an empty/whitespace plan name would build the invalid branch
    /// name <c>guardrails/</c> and let git reject it with a raw exit-128. CreateIntegration must
    /// instead throw a CLEAR, diagnosed <see cref="InvalidOperationException"/> naming the problem,
    /// before it ever shells out to git.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateIntegration_EmptyPlanName_ThrowsClearDiagnostic_NotRawGit128(string planName)
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.CreateIntegration(planName, "run-160", CancellationToken.None));

        Assert.Contains("could not derive a plan name", ex.Message, StringComparison.Ordinal);
        // No half-created plan branch is left behind — the guard fires before any git call.
        Assert.False(repo.BranchExists("guardrails/"),
            "The invalid 'guardrails/' branch must never be created.");
    }

    /// <summary>
    /// A linear chain of tasks reuses ONE segment worktree passed along the chain.
    /// The worktree path is identical for every linear hop — this is the reuse lever.
    /// </summary>
    [Fact]
    public void LinearChain_ReuseSegment_ReturnsSameWorktreePath()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("chain-plan", "run-002", CancellationToken.None);

        // Root task: fresh segment worktree forked off the plan-branch HEAD
        WorktreeHandle h1 = provider.CreateSegment("01-root", 1, integ, CancellationToken.None);

        // Each successive linear hop reuses the same physical worktree
        WorktreeHandle h2 = provider.ReuseSegment(h1, "02-middle", 1);
        WorktreeHandle h3 = provider.ReuseSegment(h2, "03-tail", 1);

        // All three handles carry the same worktree path — one worktree for the whole chain
        Assert.Equal(h1.WorktreePath, h2.WorktreePath);
        Assert.Equal(h2.WorktreePath, h3.WorktreePath);

        // The shared path must be a real linked worktree
        Assert.True(Directory.Exists(h1.WorktreePath),
            "Segment worktree directory must exist after CreateSegment");
        Assert.True(TempGitRepo.IsLinkedWorktree(h1.WorktreePath),
            "Segment worktree must be a linked git worktree (.git pointer file)");
    }

    /// <summary>
    /// Fan-out fork-the-rest sibling, dequeued AFTER the inherit-one successor has advanced the
    /// shared segment branch, forks off the producer's RECORDED commit sha — not the inheritor's
    /// advanced tip. This is the W-2 gate.
    /// </summary>
    [Fact]
    public void FanOut_ForkFromTip_UsesProducerRecordedSha_NotInheritorAdvancedTip()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("fan-plan", "run-003", CancellationToken.None);

        // Producer task: gets a segment worktree and commits its work
        WorktreeHandle producerHandle = provider.CreateSegment("01-producer", 1, integ, CancellationToken.None);
        string producerRecordedSha = repo.CommitFile(
            producerHandle.WorktreePath, "producer.txt", "producer output", "producer commit");

        // Inherit-one successor reuses the SAME worktree and advances the segment branch tip.
        // The fork-the-rest sibling is not yet dequeued at this point.
        WorktreeHandle inheritHandle = provider.ReuseSegment(producerHandle, "02-inherit-one", 1);
        string inheritorAdvancedSha = repo.CommitFile(
            inheritHandle.WorktreePath, "inherit.txt", "inheritor output", "inheritor commit");

        // Sanity: the inheritor's commit advanced the branch beyond the producer's commit
        Assert.NotEqual(producerRecordedSha, inheritorAdvancedSha);

        // Fork-the-rest is now dequeued — AFTER the inheritor has advanced the branch.
        // W-2: it must fork from the producer's RECORDED sha, not the live tip of the segment
        // branch (which the inheritor has already advanced).
        WorktreeHandle forkHandle = provider.ForkFromTip(
            producerRecordedSha, "03-fork-the-rest", 1);

        // The forked worktree's HEAD equals the producer's recorded sha — NOT the inheritor's tip
        string forkHead = repo.HeadSha(forkHandle.WorktreePath);
        Assert.Equal(producerRecordedSha, forkHead);
        Assert.NotEqual(inheritorAdvancedSha, forkHead);

        // The fork is a distinct worktree — it must not reuse the shared segment worktree
        Assert.NotEqual(producerHandle.WorktreePath, forkHandle.WorktreePath);
        Assert.True(TempGitRepo.IsLinkedWorktree(forkHandle.WorktreePath),
            "Forked worktree must be a real linked git worktree");
    }

    /// <summary>
    /// Discard removes a freed segment worktree from disk; the integration worktree is
    /// reattached to the plan branch and must NOT be pruned by a Discard call.
    /// </summary>
    [Fact]
    public void Discard_RemovesSegmentWorktree_IntegrationWorktreeNotPruned()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("discard-plan", "run-004", CancellationToken.None);

        WorktreeHandle segHandle = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);
        string segPath = segHandle.WorktreePath;
        string integPath = integ.IntegrationWorktreePath;

        Assert.True(Directory.Exists(segPath), "Segment worktree should exist before Discard");
        Assert.True(Directory.Exists(integPath), "Integration worktree should exist before Discard");

        provider.Discard(segHandle);

        // The freed segment worktree must be removed from disk
        Assert.False(Directory.Exists(segPath),
            "Segment worktree must be removed by Discard");

        // The integration worktree is reattached to the plan branch — Discard must NOT touch it
        Assert.True(Directory.Exists(integPath),
            "Integration worktree must NOT be removed by Discard — it is reattached, not pruned");
    }
}
