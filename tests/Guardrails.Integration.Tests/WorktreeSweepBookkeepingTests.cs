using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #450 — the startup GC's git bookkeeping, against a REAL repository.
/// <para>
/// The sweep used to spawn up to three git processes PER ROOT (<c>worktree list</c>,
/// <c>worktree remove</c>, <c>worktree prune</c>), which is why a ~1000-root backlog of foreign fixture
/// debris — roots this repo had never registered, where every one of those spawns was a no-op — took
/// minutes. The listing is now fetched once per sweep and the prune runs once at the end, and a root with
/// nothing registered under it costs no git at all. Batching bookkeeping is exactly the kind of
/// optimisation that can leave git's records inconsistent, so these prove the records afterwards rather
/// than the process count.
/// </para>
/// </summary>
public sealed class WorktreeSweepBookkeepingTests
{
    [Fact]
    public void Sweep_ReclaimsAStaleRegisteredWorktree_AndLeavesNoGitRecordBehind()
    {
        using var repo = new TempSkillsRepo();
        string parent = Path.Combine(Path.GetTempPath(), "gr-sweep-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "deadbeef");
        string segment = Path.Combine(root, "segment");

        repo.Git("worktree", "add", "-b", "guardrails/abandoned", segment);
        Assert.Contains(
            Slashed(segment), Slashed(repo.Git("worktree", "list", "--porcelain")),
            StringComparison.OrdinalIgnoreCase);

        AgeTree(root, DateTime.UtcNow - TimeSpan.FromDays(3)); // an abandoned run's leak

        try
        {
            WorktreeReclaim.SweepRoots(
                [parent], repo.RepoPath, currentRealRoot: null, DateTime.UtcNow - TimeSpan.FromHours(24),
                TextWriter.Null);

            Assert.False(Directory.Exists(root));
            // The registration is gone too — a deleted tree with a live record is the inconsistent state
            // batching could have produced. (Asserted through the same normalisation that was just proven
            // to MATCH above, so this cannot pass merely by mis-spelling the path.)
            Assert.DoesNotContain(
                Slashed(segment), Slashed(repo.Git("worktree", "list", "--porcelain")),
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(WorktreeAdminRecords(repo.RepoPath));
        }
        finally
        {
            SafeDelete.DeleteDirectory(parent);
        }
    }

    [Fact]
    public void ForeignRoot_NeedsNoPrune_AndIsStillDeleted()
    {
        // The #450 cheap path: fixture debris this repo never registered. Nothing to unregister ⇒ the
        // caller is told no prune is warranted ⇒ zero git processes for the entire root. The directory
        // still goes, which is the only thing that ever mattered for those roots.
        using var repo = new TempSkillsRepo();
        string root = Path.Combine(Path.GetTempPath(), "gr-foreign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.cs"), "// leaked integration fixture");

        try
        {
            bool prunePending = GitWorktreeProvider.RemoveWorktreeRoot(
                repo.RepoPath, root, GitWorktreeProvider.RegisteredWorktreePaths(repo.RepoPath));

            Assert.False(prunePending);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SafeDelete.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RegisteredWorktreePaths_ListsLinkedWorktrees_AndIsEmptyOutsideARepo()
    {
        using var repo = new TempSkillsRepo();
        string segment = Path.Combine(Path.GetTempPath(), "gr-listed-" + Guid.NewGuid().ToString("N"));
        repo.Git("worktree", "add", "-b", "guardrails/listed", segment);

        try
        {
            IReadOnlyList<string> listed = GitWorktreeProvider.RegisteredWorktreePaths(repo.RepoPath);
            Assert.Contains(listed, p => p.Contains(Path.GetFileName(segment), StringComparison.OrdinalIgnoreCase));

            // Best-effort by contract: no repo, no throw, no listing — the sweep then degrades to a
            // directory-only reclaim exactly as it did before.
            string notARepo = Path.Combine(Path.GetTempPath(), "gr-norepo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(notARepo);
            try { Assert.Empty(GitWorktreeProvider.RegisteredWorktreePaths(notARepo)); }
            finally { SafeDelete.DeleteDirectory(notARepo); }
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", segment);
            SafeDelete.DeleteDirectory(segment);
        }
    }

    /// <summary>
    /// Forward-slash form. <c>git worktree list --porcelain</c> prints POSIX separators even on Windows, so
    /// comparing raw paths would silently never match (and make a DoesNotContain assertion vacuous).
    /// </summary>
    private static string Slashed(string text) => text.Replace('\\', '/');

    /// <summary>The repo's per-worktree admin directories (<c>.git/worktrees/*</c>) — empty once pruned.</summary>
    private static string[] WorktreeAdminRecords(string repoPath)
    {
        string dir = Path.Combine(repoPath, ".git", "worktrees");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
    }

    /// <summary>Stamp a whole tree (descendants first, root last) so the GC sees it as abandoned.</summary>
    private static void AgeTree(string root, DateTime mtimeUtc)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (Directory.Exists(entry)) { Directory.SetLastWriteTimeUtc(entry, mtimeUtc); }
                else { File.SetLastWriteTimeUtc(entry, mtimeUtc); }
            }
            catch { /* best-effort */ }
        }

        Directory.SetLastWriteTimeUtc(root, mtimeUtc);
    }
}
