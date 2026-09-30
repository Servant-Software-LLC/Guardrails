using System.Text.RegularExpressions;
using Guardrails.Core.Io;
using Guardrails.Core.Telemetry;
using Guardrails.TestSupport;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #826: the harness's ONE recursive delete (<see cref="SafeDelete"/>) and the walk under it
/// (<see cref="LinkSafeTree"/>) never delete, enumerate into, or modify anything reached THROUGH a link.
/// Each fixture builds an "outside" folder the harness does not own and links to it from inside the tree
/// being deleted; every assertion is on the OUTSIDE files.
/// </summary>
public sealed class LinkSafeDeleteTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "gr826-core-" + Guid.NewGuid().ToString("N")[..12]);

    public LinkSafeDeleteTests() => Directory.CreateDirectory(_base);

    public void Dispose() => TestLinks.Cleanup(_base);

    [Fact]
    public void SafeDelete_TreeHoldingADirectoryLink_DeletesTheTree_NeverTheTargetOrItsAttributes()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(tree, "nested")).FullName, "own.txt"), "x");
        TestLinks.DirectoryLink(Path.Combine(tree, "nested", "to-outside"), outside);
        int before = TestLinks.FileCount(outside);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree), "the tree itself must be gone");
        Assert.Equal(before, TestLinks.FileCount(outside));
        // Before #826 the read-only clear enumerated THROUGH the link (AllDirectories follows a junction —
        // measured) and stripped this outside file's attribute. The walk now never enters a link.
        Assert.True(
            File.GetAttributes(Path.Combine(outside, "file-0.txt")).HasFlag(FileAttributes.ReadOnly),
            "an outside file's attributes must never be touched");
    }

    [Fact]
    public void SafeDelete_ALinkInsideALinkTarget_IsNeverWalkedOrRemoved()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string further = TestLinks.OutsideFolder(_base, "further");
        TestLinks.DirectoryLink(Path.Combine(outside, "inner-link"), further); // lives in the TARGET, not the tree
        string tree = Path.Combine(_base, "tree");
        TestLinks.DirectoryLink(Path.Combine(tree, "to-outside"), outside);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(LinkSafeTree.IsLink(Path.Combine(outside, "inner-link")),
            "a link inside the target belongs to its owner — the walk must never reach it");
        Assert.Equal(21, TestLinks.FileCount(further));
    }

    [Fact]
    public void SafeDelete_OnALinkRoot_RemovesOnlyTheLink()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string link = Path.Combine(_base, "root-link");
        TestLinks.DirectoryLink(link, outside);

        SafeDelete.DeleteDirectory(link);

        Assert.False(LinkSafeTree.IsLink(link));
        Assert.False(Directory.Exists(link));
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void SafeDelete_ADanglingLink_IsRemovedWithTheTree()
    {
        string tree = Path.Combine(_base, "tree");
        TestLinks.DanglingLink(Path.Combine(tree, "dangling"), _base);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void SafeDelete_AFileSymlinkToAnOutsideFile_RemovesTheLinkOnly()
    {
        string outsideFile = Path.Combine(_base, "outside.txt");
        File.WriteAllText(outsideFile, "outside\n");
        string tree = Path.Combine(_base, "tree");
        Assert.SkipUnless(TestLinks.TryFileSymlink(Path.Combine(tree, "sub", "file-link.txt"), outsideFile), TestLinks.FileSymlinkSkipReason);

        SafeDelete.DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("outside\n", File.ReadAllText(outsideFile));
    }

    [Fact]
    public void SafeDelete_ALinkThatCannotBeRemoved_RefusesLoudly_AndDeletesNothing()
    {
        Assert.SkipWhen(TestLinks.IsUnixRoot, "root ignores the read-only parent that pins the link on Unix");
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(tree).FullName, "own.txt"), "mine\n");
        string link = Path.Combine(tree, "pinned", "to-outside");
        TestLinks.DirectoryLink(link, outside);

        LinkRemovalException refusal;
        using (TestLinks.Pin(link))
        {
            refusal = Assert.Throws<LinkRemovalException>(() => SafeDelete.DeleteDirectory(tree));
        }

        Assert.Contains(link, refusal.Unremoved);
        Assert.Contains("#826", refusal.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(tree, "own.txt")), "a refused delete deletes NOTHING");
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void TryRemoveLink_AReadOnlyJunction_IsRemoved_TargetUntouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a read-only link entry is a Windows attribute");
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string link = Path.Combine(_base, "ro-link");
        TestLinks.DirectoryLink(link, outside);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);

        Assert.True(LinkSafeTree.TryRemoveLink(link));

        Assert.False(Directory.Exists(link));
        Assert.Equal(21, TestLinks.FileCount(outside));
        Assert.False(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void TryRemoveLink_ARealDirectory_IsRefused_AndKept()
    {
        string real = Path.Combine(_base, "real");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(real).FullName, "a.txt"), "a");

        Assert.False(LinkSafeTree.TryRemoveLink(real));
        Assert.True(File.Exists(Path.Combine(real, "a.txt")));
    }

    [Fact]
    public void RemoveLinks_SkipRootGitDirectory_LeavesTheRootsGitInternalsUnwalked()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        TestLinks.DirectoryLink(Path.Combine(tree, ".git", "not-walked"), outside);
        TestLinks.DirectoryLink(Path.Combine(tree, "node_modules", "pkg"), outside);

        LinkSweepResult sweep = LinkSafeTree.RemoveLinks(tree, skipRootGitDirectory: true);

        Assert.True(sweep.Safe);
        Assert.Equal([Path.Combine(tree, "node_modules", "pkg")], sweep.Removed);
        Assert.True(LinkSafeTree.IsLink(Path.Combine(tree, ".git", "not-walked")));
    }

    [Fact]
    public void FindDirectoriesNamed_NeverDescendsIntoALink()
    {
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "_integration"));
        string root = Path.Combine(_base, "root");
        Directory.CreateDirectory(Path.Combine(root, "run-1", "_integration"));
        TestLinks.DirectoryLink(Path.Combine(root, "run-2", "node_modules", "pkg"), outside);

        List<string> found = LinkSafeTree.FindDirectoriesNamed(root, "_integration");

        Assert.Equal([Path.Combine(root, "run-1", "_integration")], found);
    }

    /// <summary>Audited site: the telemetry corpus purge goes through the link-safe delete.</summary>
    [Fact]
    public void TelemetryCorpusPurge_ALinkInTheCorpus_NeverDeletesThroughIt()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        var store = new TelemetryCorpusStore(Path.Combine(_base, "corpus"));
        TestLinks.DirectoryLink(Path.Combine(store.CorpusRoot, "linked"), outside);

        store.Purge();

        Assert.False(Directory.Exists(store.CorpusRoot));
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void RemoveLinks_ANestedRepositorysGitDirectory_IsWalked_OnlyTheRootsIsSkipped()
    {
        // #826 review WEAK 1: skipping EVERY `.git` let a junction inside a vendored checkout's .git/ survive the
        // sweep and be deleted through by git (reproduced 21 → 0).
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        string nested = Path.Combine(tree, "vendor", "lib", ".git", "hooks-link");
        TestLinks.DirectoryLink(nested, outside);

        LinkSweepResult sweep = LinkSafeTree.RemoveLinks(tree, skipRootGitDirectory: true);

        Assert.True(sweep.Safe);
        Assert.Equal([nested], sweep.Removed);
        Assert.Equal(21, TestLinks.FileCount(outside));
    }

    [Fact]
    public void FindLinks_ListsEveryLink_AndRemovesNothing()
    {
        string outside = TestLinks.OutsideFolder(_base, "outside");
        string tree = Path.Combine(_base, "tree");
        string link = Path.Combine(tree, "a", "link");
        TestLinks.DirectoryLink(link, outside);

        LinkSweepResult found = LinkSafeTree.FindLinks(tree);

        Assert.Empty(found.Removed);
        Assert.Equal([link], found.Unremoved);
        Assert.True(LinkSafeTree.IsLink(link));
    }

    /// <summary>
    /// The shared-primitive guarantee, held at the source (#826 review WEAK 3): every recursive delete in the
    /// harness is <see cref="SafeDelete"/>, no <c>git worktree remove</c> runs anywhere, and every git verb that
    /// deletes or writes a working tree through a link (<c>reset --hard</c>, <c>clean</c>) sits behind a link
    /// guard in <c>GitWorktreeProvider</c>. The scanner is proven against each evasion below.
    /// </summary>
    [Fact]
    public void Source_EveryRecursiveDeleteAndGitRewrite_GoesThroughTheLinkSafePrimitive()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            violations.AddRange(LinkSafetyScan.Violations(Path.GetFileName(file), File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("X.cs", "Directory.Delete(path, true);")]
    [InlineData("X.cs", "Directory.Delete(path, recursive: true);")]
    [InlineData("X.cs", "new DirectoryInfo(p).Delete(true);")]
    [InlineData("X.cs", "dir.Delete(recursive: true);")]
    [InlineData("X.cs", "GitIn(wt, \"worktree\", \"remove\", \"--force\", wt);")]
    [InlineData("X.cs", "Run(\"git worktree remove --force x\");")]
    [InlineData("X.cs", "GitIn(wt, \"reset\", \"--hard\", sha);")]
    [InlineData("X.cs", "Run(\"git reset --hard HEAD\");")]
    [InlineData("X.cs", "GitIn(wt, \"clean\", \"-fd\");")]
    [InlineData("X.cs", "Run(\"git clean -fdx\");")]
    [InlineData("X.cs", "psi.ArgumentList.Add(\"--hard\");")]   // Scheduler's pre-review best-effort rollback shape
    [InlineData("X.cs", "psi.ArgumentList.Add(\"clean\");")]
    [InlineData("SafeDelete.cs", "Directory.Delete(path, recursive: true);")]            // allowed file, but no sweep before it
    [InlineData("GitWorktreeProvider.cs", "GitIn(wt, \"worktree\", \"remove\", wt);")]  // never allowed, guard or not
    // The #826 review BLOCKER as it shipped in c17a7b1f: an unguarded reset + clean in the allowed file.
    [InlineData("GitWorktreeProvider.cs",
        "public static void ResetSegment(string worktreePath, string taskBase)\n{\n    GitIn(worktreePath, \"reset\", \"--hard\", taskBase);\n    GitIn(worktreePath, \"clean\", \"-fd\");\n}")]
    public void Scanner_CatchesEveryEvasion(string file, string code) =>
        Assert.NotEmpty(LinkSafetyScan.Violations(file, code));

    [Theory]
    [InlineData("SafeDelete.cs", "var s = LinkSafeTree.RemoveLinks(path, clearReadOnly: true);\nif (!s.Safe) throw x;\nDirectory.Delete(path, recursive: true);")]
    [InlineData("GitWorktreeProvider.cs", "DisarmLinksBeforeGitRewrite(worktreePath);\nGitIn(worktreePath, \"reset\", \"--hard\", taskBase);\nGitIn(worktreePath, \"clean\", \"-fd\");")]
    [InlineData("X.cs", "// a comment may say git reset --hard or Directory.Delete(p, true)\n/// <c>git clean -fd</c>")]
    public void Scanner_AcceptsGuardedCallsAndMentions(string file, string code) =>
        Assert.Empty(LinkSafetyScan.Violations(file, code));

    /// <summary>The #826 source rules, as a function of one file's name and text so the rules themselves are testable.</summary>
    private static class LinkSafetyScan
    {
        private const int GuardWindow = 12; // non-comment lines a guard may precede the guarded call by

        private static readonly Regex RecursiveDelete = new(
            @"Directory\.Delete\([^;]*,\s*(recursive:\s*)?true\s*\)|\.Delete\(\s*(recursive:\s*)?true\s*\)");

        // Argument-list forms ("reset", "--hard" / ArgumentList.Add("--hard")) and one-string command forms
        // ("git reset --hard …"). A bare "reset --hard" in prose (a diagnostic message) is not a call.
        private static readonly Regex WorktreeRemove = new(@"""worktree""\s*,\s*""remove""|git worktree remove");

        private static readonly Regex GitRewrite = new(@"""--hard""|git reset --hard|""clean""\s*[,)]|git clean -[a-z]*f");

        internal static List<string> Violations(string fileName, string text)
        {
            var violations = new List<string>();
            List<string> code = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .ToList();
            for (int i = 0; i < code.Count; i++)
            {
                string line = code[i];
                string where = $"{fileName}: {line.Trim()}";
                if (WorktreeRemove.IsMatch(line))
                {
                    violations.Add($"git worktree remove follows a junction — use RemoveWorktreeLinkSafe: {where}");
                }

                if (RecursiveDelete.IsMatch(line)
                    && !(fileName == "SafeDelete.cs" && GuardedBy(code, i, "LinkSafeTree.RemoveLinks(")))
                {
                    violations.Add($"recursive delete outside the link-safe SafeDelete: {where}");
                }

                if (GitRewrite.IsMatch(line)
                    && !(fileName == "GitWorktreeProvider.cs"
                         && GuardedBy(code, i, "DisarmLinksBeforeGitRewrite(", "LinksShadowingTrackedPaths(")))
                {
                    violations.Add($"git reset --hard / clean without a link guard first: {where}");
                }
            }

            return violations;
        }

        private static bool GuardedBy(List<string> code, int index, params string[] guards) =>
            code.Skip(Math.Max(0, index - GuardWindow)).Take(index - Math.Max(0, index - GuardWindow))
                .Any(l => guards.Any(g => l.Contains(g, StringComparison.Ordinal)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Guardrails.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Guardrails.sln not found above the test binary");
    }
}
