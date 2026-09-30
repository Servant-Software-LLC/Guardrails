using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.TestSupport;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #826: a harness teardown never deletes anything OUTSIDE the worktree it tears down. Measured on
/// Windows (git 2.53): <c>git worktree remove --force</c> on a worktree holding a directory junction deleted
/// every file in the junction's target. The links here are the shapes #816's write-scope strip cannot see — one
/// under an ignored <c>node_modules/</c> (<c>npm link</c> / pnpm), one created without changing a tracked path, a
/// file link, a dangling link — and one link INSIDE a link target, which the walk must never reach. Every
/// assertion is on the outside files; <see cref="RawGitRemove_OnTheSameShape_DeletesThroughTheJunction_OnWindows"/>
/// proves the fixture reproduces the bug the teardown guards against.
/// </summary>
public sealed class TeardownLinkSafetyTests : IDisposable
{
    private const int OutsideFiles = 21; // TestLinks.OutsideFolder: 20 files + sub/deep.txt

    private readonly string _base = Path.Combine(Path.GetTempPath(), "gr826-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _repo;
    private readonly string _root;

    public TeardownLinkSafetyTests()
    {
        _repo = Path.Combine(_base, "repo");
        _root = Path.Combine(_base, "worktrees");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_root);
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.email", "test@guardrails.local");
        Git(_repo, "config", "user.name", "Guardrails Test");
        Git(_repo, "config", "core.hooksPath", Path.Combine(_base, "no-hooks"));
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# test\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "node_modules/\n");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "a.cs"), "class A {}\n"); // a TRACKED directory to replace with a link
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose() => TestLinks.Cleanup(_base);

    /// <summary>The outside folders a populated worktree links to.</summary>
    private sealed record Outside(string Linked, string NodeModules, string Further);

    /// <summary>
    /// Plant, inside <paramref name="worktree"/>: (a) a directory link to an outside folder, created without
    /// touching any tracked path; (b) one under the ignored <c>node_modules/</c>; (d) a dangling link. And (e)
    /// inside (a)'s TARGET, a link to a further outside folder — it belongs to the target's owner, never to us.
    /// </summary>
    private Outside Populate(string worktree)
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        var outside = new Outside(
            TestLinks.OutsideFolder(_base, "outside-linked-" + tag),
            TestLinks.OutsideFolder(_base, "outside-node-" + tag),
            TestLinks.OutsideFolder(_base, "outside-further-" + tag));
        TestLinks.DirectoryLink(Path.Combine(outside.Linked, "inner-link"), outside.Further);  // (e)

        TestLinks.DirectoryLink(Path.Combine(worktree, "tools", "linked-dir"), outside.Linked);      // (a)
        TestLinks.DirectoryLink(Path.Combine(worktree, "node_modules", "my-pkg"), outside.NodeModules); // (b)
        TestLinks.DanglingLink(Path.Combine(worktree, "dangling"), _base);                              // (d)
        return outside;
    }

    private static void AssertIntact(Outside outside)
    {
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside.Linked));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside.NodeModules));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside.Further));
        Assert.True(LinkSafeTree.IsLink(Path.Combine(outside.Linked, "inner-link")),
            "(e) a link inside a link TARGET must never be walked to, let alone removed");
        Assert.True(File.GetAttributes(Path.Combine(outside.Linked, "file-0.txt")).HasFlag(FileAttributes.ReadOnly),
            "an outside file's attributes must never be touched");
    }

    private (GitWorktreeProvider Provider, IntegrationHandle Integ, WorktreeHandle Segment) Segment(string runId = "run-826")
    {
        var provider = new GitWorktreeProvider(_repo, _root);
        IntegrationHandle integ = provider.CreateIntegration("links-plan", runId, CancellationToken.None);
        WorktreeHandle segment = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);
        return (provider, integ, segment);
    }

    [Fact]
    public void Discard_ASegmentHoldingLinks_RemovesTheWorktree_AndEveryOutsideFileSurvives()
    {
        (GitWorktreeProvider provider, _, WorktreeHandle segment) = Segment();
        Outside outside = Populate(segment.WorktreePath);

        provider.Discard(segment);

        Assert.False(Directory.Exists(segment.WorktreePath), "the segment worktree must be gone");
        Assert.DoesNotContain(RegisteredWorktrees(), p => SamePath(p, segment.WorktreePath));
        AssertIntact(outside);
    }

    [Fact]
    public void Discard_AFileSymlinkToAnOutsideFile_RemovesTheLinkOnly()
    {
        (GitWorktreeProvider provider, _, WorktreeHandle segment) = Segment();
        string outsideFile = Path.Combine(_base, "outside-file.txt");
        File.WriteAllText(outsideFile, "outside\n");
        Assert.SkipUnless(
            TestLinks.TryFileSymlink(Path.Combine(segment.WorktreePath, "src", "file-link.txt"), outsideFile),
            TestLinks.FileSymlinkSkipReason);

        provider.Discard(segment);

        Assert.False(Directory.Exists(segment.WorktreePath));
        Assert.Equal("outside\n", File.ReadAllText(outsideFile));
    }

    /// <summary>
    /// The fixture proof (the standing mutation check): the SAME worktree shape handed straight to
    /// <c>git worktree remove --force</c> — what every teardown did before #826 — deletes through the junctions
    /// on Windows. If this ever stops failing the outside files, the tests above stop proving anything.
    /// </summary>
    [Fact]
    public void RawGitRemove_OnTheSameShape_DeletesThroughTheJunction_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git follows a directory JUNCTION on Windows; Unix git unlinks symlinks");
        (_, _, WorktreeHandle segment) = Segment();
        Outside outside = Populate(segment.WorktreePath);

        // git may still exit non-zero ("Directory not empty" over an outside entry it could not unlink) — AFTER it
        // has already deleted through the links. The damage is what is asserted.
        try { Git(_repo, "worktree", "remove", "--force", segment.WorktreePath); }
        catch (InvalidOperationException) { /* see above */ }

        Assert.True(TestLinks.FileCount(outside.Linked) < OutsideFiles, "raw git must have deleted through (a)");
        Assert.True(TestLinks.FileCount(outside.NodeModules) < OutsideFiles, "raw git must have deleted through (b)");
    }

    [Fact]
    public void PruneStaleRunBranches_ASegmentHoldingLinks_OutsideSurvives()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ, WorktreeHandle segment) = Segment();
        Outside outside = Populate(segment.WorktreePath);

        provider.PruneStaleRunBranches("run-826", integ);

        Assert.False(Directory.Exists(segment.WorktreePath));
        AssertIntact(outside);
    }

    [Fact]
    public void PruneStaleSegmentBranches_ASegmentHoldingLinks_OutsideSurvives()
    {
        (_, _, WorktreeHandle segment) = Segment();
        Outside outside = Populate(segment.WorktreePath);

        GitWorktreeProvider.PruneStaleSegmentBranches(_repo, _root);

        Assert.False(Directory.Exists(segment.WorktreePath));
        AssertIntact(outside);
    }

    [Fact]
    public void RemoveWorktreeRoot_RegisteredAndUnregisteredTreesHoldingLinks_OutsideSurvives()
    {
        (_, IntegrationHandle integ, WorktreeHandle segment) = Segment();
        Outside inSegment = Populate(segment.WorktreePath);
        Outside inIntegration = Populate(integ.IntegrationWorktreePath);
        string leftover = Path.Combine(_root, "crashed-run", "01-task", "attempt-1"); // never registered
        Outside inLeftover = Populate(leftover);

        GitWorktreeProvider.RemoveWorktreeRoot(_repo, _root);

        Assert.False(Directory.Exists(_root));
        AssertIntact(inSegment);
        AssertIntact(inIntegration);
        AssertIntact(inLeftover);
    }

    [Fact]
    public void TeardownPlanBranch_IntegrationHoldingLinks_AndAnIntegrationFolderReachableOnlyThroughALink_OutsideSurvives()
    {
        (_, IntegrationHandle integ, _) = Segment();
        Outside outside = Populate(integ.IntegrationWorktreePath);
        // An outside folder holding its own `_integration` directory, reachable from the worktree root only THROUGH a
        // link: the orphan sweep must not find it (an AllDirectories search walks into a junction on Windows).
        string foreign = Path.Combine(_base, "foreign-checkout");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(foreign, "_integration")).FullName, "keep.txt"), "k");
        TestLinks.DirectoryLink(Path.Combine(_root, "stray", "node_modules", "dep"), foreign);

        GitWorktreeProvider.TeardownPlanBranch(_repo, _root, "links-plan");

        Assert.False(Directory.Exists(integ.IntegrationWorktreePath));
        AssertIntact(outside);
        Assert.True(File.Exists(Path.Combine(foreign, "_integration", "keep.txt")),
            "an _integration folder found only through a link is not ours");
    }

    [Fact]
    public void RemoveDetachedWorktree_ARevalidateTreeHoldingLinks_OutsideSurvives()
    {
        (_, _, _) = Segment();
        string detached = GitWorktreeProvider.AddDetachedWorktreeAtBranchTip(_repo, _root, "guardrails/links-plan");
        Outside outside = Populate(detached);

        GitWorktreeProvider.RemoveDetachedWorktree(_repo, detached);

        Assert.False(Directory.Exists(detached));
        AssertIntact(outside);
    }

    [Fact]
    public void ResetSegment_GitCleanOverAnUntrackedLink_RemovesTheLinkOnly()
    {
        (_, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-clean");
        TestLinks.DirectoryLink(Path.Combine(segment.WorktreePath, "untracked-dir", "link"), outside);

        GitWorktreeProvider.ResetSegment(segment.WorktreePath, segment.TaskBase);

        Assert.False(Directory.Exists(Path.Combine(segment.WorktreePath, "untracked-dir")));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
    }

    /// <summary>
    /// #826 review BLOCKER: an attempt replaced the TRACKED <c>src/</c> with a junction to an outside folder. Before
    /// the fix the retry reset's <c>reset --hard</c> wrote <c>a.cs</c> INTO the outside folder and <c>clean -fd</c>
    /// deleted its files as untracked content under <c>src/</c> (21 → 1). Now the link is removed first and the
    /// segment is reset inside itself.
    /// </summary>
    [Fact]
    public void ResetSegment_AJunctionReplacingATrackedDirectory_IsRemoved_NothingWrittenOrDeletedThroughIt()
    {
        (_, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-src");
        string src = Path.Combine(segment.WorktreePath, "src");
        Directory.Delete(src, recursive: true); // the attempt's doing: a tracked directory, replaced by a link
        TestLinks.DirectoryLink(src, outside);

        GitWorktreeProvider.ResetSegment(segment.WorktreePath, segment.TaskBase);

        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.False(File.Exists(Path.Combine(outside, "a.cs")), "reset --hard must never write a tracked file through a link");
        Assert.False(LinkSafeTree.IsLink(src), "the link is removed from the segment");
        Assert.Equal("class A {}\n", File.ReadAllText(Path.Combine(src, "a.cs")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void ResetSegment_ALinkThatCannotBeRemoved_RunsNeitherResetNorClean_AndSaysSo()
    {
        Assert.SkipWhen(TestLinks.IsUnixRoot, "root ignores the read-only parent that pins the link on Unix");
        (_, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-pinned-src");
        string src = Path.Combine(segment.WorktreePath, "src");
        Directory.Delete(src, recursive: true);
        TestLinks.DirectoryLink(src, outside);

        LinkRemovalException refusal;
        using (TestLinks.Pin(src))
        {
            refusal = Assert.Throws<LinkRemovalException>(() => GitWorktreeProvider.ResetSegment(segment.WorktreePath, segment.TaskBase));
        }

        Assert.Contains("refused to reset", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.False(File.Exists(Path.Combine(outside, "a.cs")));
    }

    /// <summary>
    /// The operator's own checkout (serial-mode supplied-drain rollback, a plan branch checked out by hand): their
    /// link is NEVER removed; a hard reset that would write through it is refused and only the index is reset.
    /// </summary>
    [Fact]
    public void ResetHardInOperatorTree_ALinkShadowingATrackedDirectory_IsLeftAlone_AndTheResetRefused()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside-operator");
        string src = Path.Combine(_repo, "src");
        Directory.Delete(src, recursive: true);
        TestLinks.DirectoryLink(src, outside);

        LinkRemovalException refusal = Assert.Throws<LinkRemovalException>(
            () => GitWorktreeProvider.ResetHardInOperatorTree(_repo, "HEAD"));

        Assert.Contains(src, refusal.Unremoved);
        Assert.True(LinkSafeTree.IsLink(src), "an operator's link is theirs — never removed");
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.False(File.Exists(Path.Combine(outside, "a.cs")));
    }

    /// <summary>
    /// #826 re-review BLOCKER: the TARGET has no <c>lib/</c>; HEAD tracks <c>lib/f3.txt</c>; the operator's <c>lib/</c>
    /// is a junction to an outside folder holding its own <c>f3.txt</c>. A hard reset to the target DELETES the paths
    /// tracked at HEAD — through the link. The check must see HEAD (and the index, which matches it here), not only
    /// the target. (Measured: a path at HEAD but NOT in the index is left alone by git — reset --hard works from the
    /// index — so the HEAD probe is belt-and-braces and the index probe is the one this case needs.)
    /// </summary>
    [Fact]
    public void ResetHardInOperatorTree_APathTrackedOnlyAtHead_UnderALink_IsNotDeletedThroughIt()
    {
        string target = Git(_repo, "rev-parse", "HEAD").Trim();                  // c1: no lib/
        Directory.CreateDirectory(Path.Combine(_repo, "lib"));
        File.WriteAllText(Path.Combine(_repo, "lib", "file-3.txt"), "tracked at HEAD\n");
        Git(_repo, "add", "lib");
        Git(_repo, "commit", "-q", "-m", "c2 adds lib/file-3.txt");               // HEAD: lib/file-3.txt
        string outside = TestLinks.OutsideFolder(_base, "outside-head-only");      // holds its own file-3.txt
        Directory.Delete(Path.Combine(_repo, "lib"), recursive: true);
        TestLinks.DirectoryLink(Path.Combine(_repo, "lib"), outside);

        LinkRemovalException refusal = Assert.Throws<LinkRemovalException>(
            () => GitWorktreeProvider.ResetHardInOperatorTree(_repo, target));

        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.True(File.Exists(Path.Combine(outside, "file-3.txt")), "the outside file-3.txt must survive");
        Assert.Contains(Path.Combine(_repo, "lib"), refusal.Unremoved);
        Assert.Equal(target, Git(_repo, "rev-parse", "HEAD").Trim()); // HEAD + index moved; working tree left alone
    }

    /// <summary>The same with the path only STAGED (the serial supply-drain rollback after a failed commit).</summary>
    [Fact]
    public void ResetHardInOperatorTree_APathStagedOnlyInTheIndex_UnderALink_IsNotDeletedThroughIt()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside-index-only");
        TestLinks.DirectoryLink(Path.Combine(_repo, "lib"), outside);
        // Stage a path under the link as git sees it (index-only; never committed). --add of a path under a
        // junction is refused by git itself on some versions, so write the index entry directly.
        string blob = Git(_repo, "hash-object", "-w", "README.md").Trim();
        Git(_repo, "update-index", "--add", "--cacheinfo", $"100644,{blob},lib/file-3.txt");

        LinkRemovalException refusal = Assert.Throws<LinkRemovalException>(
            () => GitWorktreeProvider.ResetHardInOperatorTree(_repo, "HEAD"));

        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.True(File.Exists(Path.Combine(outside, "file-3.txt")));
        Assert.Contains(Path.Combine(_repo, "lib"), refusal.Unremoved);
    }

    /// <summary>
    /// #826 re-review WEAK 1: the operator-tree refusal says what actually happened — HEAD and the index moved, the
    /// working tree did not, which link(s) shadow tracked paths, and when a hard reset becomes safe. It must NOT
    /// carry the "could not be removed … remove each link ENTRY" text meant for a harness link that resisted removal.
    /// </summary>
    [Fact]
    public void ResetHardInOperatorTree_Refusal_SaysWhatStateTheTreeIsIn()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside-message");
        string src = Path.Combine(_repo, "src");
        Directory.Delete(src, recursive: true);
        TestLinks.DirectoryLink(src, outside);

        string message = Assert.Throws<LinkRemovalException>(
            () => GitWorktreeProvider.ResetHardInOperatorTree(_repo, "HEAD")).Message;

        Assert.Contains("HEAD and the index are now at HEAD", message, StringComparison.Ordinal);
        Assert.Contains("working tree was left as it was", message, StringComparison.Ordinal);
        Assert.Contains("remain on disk", message, StringComparison.Ordinal);
        Assert.Contains(src, message, StringComparison.Ordinal);
        Assert.Contains("`git reset --hard HEAD` is safe to run", message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be removed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("by hand", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetHardInOperatorTree_ALinkWithNothingTrackedUnderIt_DoesNotBlockTheReset()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside-operator-ignored");
        TestLinks.DirectoryLink(Path.Combine(_repo, "node_modules", "pkg"), outside);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");

        GitWorktreeProvider.ResetHardInOperatorTree(_repo, "HEAD");

        Assert.Equal("# test\n", File.ReadAllText(Path.Combine(_repo, "README.md")).Replace("\r\n", "\n"));
        Assert.True(LinkSafeTree.IsLink(Path.Combine(_repo, "node_modules", "pkg")));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
    }

    /// <summary>#826 review WEAK 1: a link inside a NESTED repository's <c>.git/</c> is swept like any other.</summary>
    [Fact]
    public void Discard_ALinkInsideANestedRepositorysGitDirectory_OutsideSurvives()
    {
        (GitWorktreeProvider provider, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-nested-git");
        TestLinks.DirectoryLink(Path.Combine(segment.WorktreePath, "vendor", "lib", ".git", "objects-link"), outside);

        provider.Discard(segment);

        Assert.False(Directory.Exists(segment.WorktreePath));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
    }

    /// <summary>
    /// #826 review WEAK 2: teardown no longer runs <c>git worktree remove</c>; the link-safe delete + a prune must
    /// still drop git's registration, so the branch can be deleted and the path re-added.
    /// </summary>
    [Fact]
    public void TeardownWithoutGitWorktreeRemove_StillDropsTheRegistration_SoTheBranchCanBeDeletedAndThePathReused()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ, WorktreeHandle segment) = Segment();
        string branch = Git(segment.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        provider.Discard(segment);

        Assert.DoesNotContain(RegisteredWorktrees(), p => SamePath(p, segment.WorktreePath));
        Git(_repo, "branch", "-D", branch); // a branch still checked out in a registered worktree would refuse
        WorktreeHandle again = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);
        Assert.True(Directory.Exists(again.WorktreePath));
    }

    // ── a link that cannot be removed: refuse, leave everything, report loudly ────────────────────────────────

    [Fact]
    public void Discard_ALinkThatCannotBeRemoved_RefusesLoudly_LeavingTheWorktreeRegisteredAndTheOutsideIntact()
    {
        Assert.SkipWhen(TestLinks.IsUnixRoot, "root ignores the read-only parent that pins the link on Unix");
        (GitWorktreeProvider provider, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-pinned");
        string link = Path.Combine(segment.WorktreePath, "node_modules", "pinned");
        TestLinks.DirectoryLink(link, outside);

        LinkRemovalException refusal;
        using (TestLinks.Pin(link))
        {
            refusal = Assert.Throws<LinkRemovalException>(() => provider.Discard(segment));
        }

        Assert.Contains(link, refusal.Unremoved);
        Assert.True(File.Exists(Path.Combine(segment.WorktreePath, "README.md")), "a refused teardown deletes nothing");
        Assert.Contains(RegisteredWorktrees(), p => SamePath(p, segment.WorktreePath));
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
    }

    [Fact]
    public void CleanupCompletedRun_ALinkThatCannotBeRemoved_IsLoggedAsAWarning_NotSwallowed()
    {
        Assert.SkipWhen(TestLinks.IsUnixRoot, "root ignores the read-only parent that pins the link on Unix");
        (_, _, WorktreeHandle segment) = Segment();
        string outside = TestLinks.OutsideFolder(_base, "outside-reclaim");
        string link = Path.Combine(segment.WorktreePath, "node_modules", "pinned");
        TestLinks.DirectoryLink(link, outside);
        var log = new StringWriter();

        using (TestLinks.Pin(link))
        {
            WorktreeReclaim.CleanupCompletedRun(_repo, _root, junctionRoot: null, log);
        }

        Assert.Contains("WARNING", log.ToString(), StringComparison.Ordinal);
        Assert.Contains(link, log.ToString(), StringComparison.Ordinal);
        Assert.Equal(OutsideFiles, TestLinks.FileCount(outside));
        Assert.True(Directory.Exists(segment.WorktreePath));
    }

    [Fact]
    public void ConsoleObserver_ALinkRefusal_IsPrinted_OtherCleanupFailuresStayQuiet()
    {
        var output = new StringWriter();
        var observer = new ConsoleRunObserver(output);

        observer.CleanupFailed("01-task", new IOException("transient lock"));
        Assert.Equal("", output.ToString());

        observer.CleanupFailed("01-task", new LinkRemovalException("/wt/01-task", ["/wt/01-task/node_modules/pkg"]));
        string line = output.ToString();
        Assert.Contains("01-task", line, StringComparison.Ordinal);
        Assert.Contains("LEFT IN PLACE", line, StringComparison.Ordinal);
        Assert.Contains("/wt/01-task/node_modules/pkg", line, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private List<string> RegisteredWorktrees() =>
        Git(_repo, "worktree", "list", "--porcelain")
            .Split('\n')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => l["worktree ".Length..].Trim())
            .ToList();

    private static bool SamePath(string gitPath, string path) =>
        string.Equals(
            Path.GetFullPath(gitPath).TrimEnd('/', '\\'),
            Path.GetFullPath(path).TrimEnd('/', '\\'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
        || string.Equals(RealPath.Resolve(gitPath), RealPath.Resolve(path), StringComparison.OrdinalIgnoreCase);

    private static string Git(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process proc = Process.Start(psi)!;

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {proc.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
