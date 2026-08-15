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
/// <para>
/// Issue #452 — and the inconsistent state duly arrived, on macOS only: git prints the SYMLINK-RESOLVED
/// path of a worktree while the harness's root came from <see cref="Path.GetTempPath"/> unresolved
/// (<c>/private/var/folders/…</c> vs <c>/var/folders/…</c>), so nothing was ever recognised as under the
/// root, nothing was unregistered, and the "prune only when something was unregistered" batching never
/// fired. #450 did not introduce that mismatch — the unconditional prune it replaced had been sweeping
/// the dangling record away by accident. The assertions below therefore compare whole CANONICALISED
/// paths; the substring form they replaced could be satisfied by a shared prefix.
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
        Assert.Contains(Registered(repo), p => RealPath.SamePath(p, segment));

        // The precondition the sweep's unregister step turns on, asserted directly (issue #452): git
        // reports the SYMLINK-RESOLVED real path of the worktree, while `root` descends from
        // Path.GetTempPath() and is not resolved — on macOS those are "/private/var/folders/…" and
        // "/var/folders/…", two spellings of one directory. If the harness cannot see the registered
        // worktree as living under its own root, it unregisters nothing and the prune below never runs.
        Assert.Contains(Registered(repo), p => RealPath.IsUnder(p, root));

        AgeTree(root, DateTime.UtcNow - TimeSpan.FromDays(3)); // an abandoned run's leak

        try
        {
            WorktreeReclaim.SweepRoots(
                [parent], repo.RepoPath, currentRealRoot: null, DateTime.UtcNow - TimeSpan.FromHours(24),
                TextWriter.Null);

            Assert.False(Directory.Exists(root));
            // The registration is gone too — a deleted tree with a live record is the inconsistent state
            // batching could have produced. Compared as whole CANONICALISED paths, not as a substring of
            // the porcelain listing: a substring test is satisfied by any prefix coincidence (on macOS
            // "/private/var/…/segment" contains "/var/…/segment"), which made the matching Contains
            // assertion pass for the wrong reason and the failure message unreadable.
            Assert.DoesNotContain(Registered(repo), p => RealPath.SamePath(p, segment));
            Assert.Empty(WorktreeAdminRecords(repo.RepoPath));
        }
        finally
        {
            SafeDelete.DeleteDirectory(parent);
        }
    }

    [Fact]
    public void ForeignRoot_NeedsNoPrune_AndIsStillDeleted_WithoutSwallowingAPrefixSharingNeighbour()
    {
        // The #450 cheap path: fixture debris this repo never registered. Nothing to unregister ⇒ the
        // caller is told no prune is warranted ⇒ zero git processes for the entire root. The directory
        // still goes, which is the only thing that ever mattered for those roots.
        //
        // This is also the OTHER direction of the issue #452 fix, and the regression that fix could
        // plausibly introduce. Canonicalising both sides has to make a genuinely-registered worktree
        // MATCH its root without making a genuinely-foreign one start matching — so the neighbour below
        // is a live, registered worktree whose root's full name has the swept root's full name as a
        // string PREFIX ("…-live" vs ""). A containment test that canonicalised but then compared by
        // StartsWith without a directory boundary would unregister and DELETE it. It must survive
        // untouched, on every OS: on macOS both roots resolve through the same /var → /private/var link,
        // so resolution alone cannot be what tells them apart.
        using var repo = new TempSkillsRepo();
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "gr-foreign-" + id);
        string neighbourRoot = Path.Combine(Path.GetTempPath(), "gr-foreign-" + id + "-live");
        string neighbourSegment = Path.Combine(neighbourRoot, "segment");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.cs"), "// leaked integration fixture");
        repo.Git("worktree", "add", "-b", "guardrails/neighbour", neighbourSegment);

        try
        {
            bool prunePending = GitWorktreeProvider.RemoveWorktreeRoot(
                repo.RepoPath, root, GitWorktreeProvider.RegisteredWorktreePaths(repo.RepoPath));

            Assert.False(prunePending);
            Assert.False(Directory.Exists(root));

            // The neighbour is untouched — still on disk AND still registered.
            Assert.True(Directory.Exists(neighbourSegment));
            Assert.Contains(Registered(repo), p => RealPath.SamePath(p, neighbourSegment));
        }
        finally
        {
            SafeDelete.DeleteDirectory(root);
            try { repo.Git("worktree", "remove", "--force", neighbourSegment); }
            catch (InvalidOperationException) { /* best-effort cleanup */ }
            SafeDelete.DeleteDirectory(neighbourRoot);
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
            Assert.Contains(Registered(repo), p => RealPath.SamePath(p, segment));

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
    /// The repo's registered worktree paths, as the harness itself parses them — so these assertions
    /// compare whole paths through <see cref="RealPath"/> (separator-, case- and symlink-normalised)
    /// rather than searching the raw porcelain text for a substring, which a shared prefix can satisfy
    /// by accident and which would make a <c>DoesNotContain</c> assertion quietly vacuous.
    /// </summary>
    private static IReadOnlyList<string> Registered(TempSkillsRepo repo) =>
        GitWorktreeProvider.RegisteredWorktreePaths(repo.RepoPath);

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
