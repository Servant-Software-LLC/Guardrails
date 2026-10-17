using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.TestSupport;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #835 (a sibling of #826): the mergeOnSuccess delivery runs <c>git merge</c> in the OPERATOR's checkout, and
/// git writes the incoming paths into the working tree. When the operator has replaced a directory (or a tracked file)
/// with a junction/symlink to a folder outside the repo, those writes land in that folder: an added file appears in the
/// external store and a modified tracked file rewrites the external copy. The delivery must refuse BEFORE git runs,
/// name the link and the paths, leave the plan branch in place, and report the structured
/// <see cref="MergeOnSuccessResult.LinkShadowed"/> outcome (journal token <c>link-shadowed</c>).
/// <para>
/// Every assertion that matters is on the OUTSIDE folder, the thing a wrong guard would let be written.
/// <see cref="RawGitMerge_OnTheSameShape_WritesThroughTheJunction_OnWindows"/> is the negative control: the same shape
/// handed straight to <c>git merge</c> writes through, so the refusals above it are proven to be the guard's doing.
/// </para>
/// </summary>
public sealed class MergeThroughLinkTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "gr835-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _repo;
    private readonly string _root;
    private readonly string _outside;

    public MergeThroughLinkTests()
    {
        _repo = Path.Combine(_base, "repo");
        _root = Path.Combine(_base, "worktrees");
        _outside = Path.Combine(_base, "external-store");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_root);
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.email", "test@guardrails.local");
        Git(_repo, "config", "user.name", "Guardrails Test");
        Git(_repo, "config", "core.autocrlf", "false");
        Git(_repo, "config", "core.hooksPath", Path.Combine(_base, "no-hooks"));
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# test\n");
        Directory.CreateDirectory(Path.Combine(_repo, "assets"));
        File.WriteAllText(Path.Combine(_repo, "assets", "keep.txt"), "original\n");
        File.WriteAllText(Path.Combine(_repo, "cfg"), "setting=1\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose() => TestLinks.Cleanup(_base);

    /// <summary>
    /// The operator moves <c>assets/</c> to an external store and links it back, byte-for-byte identical, so git sees
    /// a clean tree (the only difference is the link) — exactly what the issue describes.
    /// </summary>
    private void ReplaceAssetsWithLink()
    {
        Directory.Move(Path.Combine(_repo, "assets"), _outside);
        TestLinks.DirectoryLink(Path.Combine(_repo, "assets"), _outside);
    }

    /// <summary>A plan branch whose single commit applies <paramref name="change"/> to its integration worktree.</summary>
    private (GitWorktreeProvider Provider, IntegrationHandle Integ) PlanWith(Action<string> change)
    {
        var provider = new GitWorktreeProvider(_repo, _root);
        IntegrationHandle integ = provider.CreateIntegration("link-plan", "run-835", CancellationToken.None);
        change(integ.IntegrationWorktreePath);
        Git(integ.IntegrationWorktreePath, "add", "-A");
        Git(integ.IntegrationWorktreePath, "commit", "-q", "--no-verify", "-m", "plan work");
        return (provider, integ);
    }

    private string OutsideSnapshot() =>
        string.Join("|", Directory.EnumerateFiles(_outside, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(_outside, f) + "=" + File.ReadAllText(f)));

    [Fact]
    public void AddingAFileUnderALinkedDirectory_IsRefused_AndNothingIsWrittenToTheTarget()
    {
        ReplaceAssetsWithLink();
        string before = OutsideSnapshot();
        string headBefore = Git(_repo, "rev-parse", "HEAD").Trim();
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "assets", "added.txt"), "plan output\n"));

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.LinkShadowed, result);
        Assert.False(File.Exists(Path.Combine(_outside, "added.txt")), "the merge must not write through the link");
        Assert.Equal(before, OutsideSnapshot());
        Assert.True(LinkSafeTree.IsLink(Path.Combine(_repo, "assets")), "the operator's link is theirs and must be left in place");
        Assert.Equal(headBefore, Git(_repo, "rev-parse", "HEAD").Trim());
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains("'assets'", provider.LastMergeOnSuccessDetail, StringComparison.Ordinal);
        Assert.Contains("assets/added.txt", provider.LastMergeOnSuccessDetail, StringComparison.Ordinal);
        Git(_repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{integ.PlanBranchName}"); // throws if the plan branch was lost
    }

    [Fact]
    public void ModifyingATrackedFileUnderALinkedDirectory_IsRefused_AndTheExternalCopyIsUntouched()
    {
        ReplaceAssetsWithLink();
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "assets", "keep.txt"), "rewritten by the plan\n"));

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.LinkShadowed, result);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_outside, "keep.txt")));
        Assert.Contains("assets/keep.txt", provider.LastMergeOnSuccessDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamingAFileOutOfALinkedDirectory_IsRefused_FromTheOldPathToo()
    {
        ReplaceAssetsWithLink();
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt =>
            {
                Directory.CreateDirectory(Path.Combine(wt, "docs"));
                File.Move(Path.Combine(wt, "assets", "keep.txt"), Path.Combine(wt, "docs", "keep.txt"));
            });

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        // The old path is a DELETE through the link; --no-renames lists it, so a rename out is caught from that end.
        Assert.Equal(MergeOnSuccessResult.LinkShadowed, result);
        Assert.True(File.Exists(Path.Combine(_outside, "keep.txt")), "the delete must not reach the external copy");
        Assert.Contains("assets/keep.txt", provider.LastMergeOnSuccessDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AJunctionOverATrackedFile_IsRefusedWhenThePlanModifiesThatFile()
    {
        // The path ITSELF is the link (not an ancestor): the tracked file `cfg` replaced by a link to an outside folder.
        string cfg = Path.Combine(_repo, "cfg");
        File.Delete(cfg);
        Directory.CreateDirectory(Path.Combine(_base, "cfg-target"));
        TestLinks.DirectoryLink(cfg, Path.Combine(_base, "cfg-target"));
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "cfg"), "setting=2\n"));

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.LinkShadowed, result);
        Assert.True(LinkSafeTree.IsLink(cfg));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_base, "cfg-target")));
    }

    [Fact]
    public void ARepoWithNoLinks_StillDeliversByFastForward()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "assets", "added.txt"), "plan output\n"));

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.FastForwarded, result);
        Assert.Equal("plan output\n", File.ReadAllText(Path.Combine(_repo, "assets", "added.txt")).Replace("\r\n", "\n"));
        Assert.Null(provider.LastMergeOnSuccessDetail);
    }

    [Fact]
    public void ALinkThatNoTouchedPathPassesThrough_DoesNotBlockDelivery()
    {
        // An operator link over an UNRELATED directory (a store the plan never touches) must not refuse a clean delivery.
        TestLinks.DirectoryLink(Path.Combine(_repo, "unrelated-store"), TestLinks.OutsideFolder(_base, "unrelated-target", files: 2));
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "README.md"), "# changed\n"));

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.FastForwarded, result);
    }

    [Fact]
    public void WhenTheTouchedPathsCannotBeComputed_TheDeliveryFailsClosed()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "README.md"), "# changed\n"));
        // Break the diff: with the plan ref gone `git diff HEAD...<plan>` fails, so link safety cannot be proven.
        Git(_repo, "update-ref", "-d", $"refs/heads/{integ.PlanBranchName}");
        string headBefore = Git(_repo, "rev-parse", "HEAD").Trim();

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.LinkShadowed, result);
        Assert.Contains("link safety cannot be proven", provider.LastMergeOnSuccessDetail, StringComparison.Ordinal);
        Assert.Equal(headBefore, Git(_repo, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public void FindShadowingLinks_NamesTheOutermostLink_AndGroupsThePathsBeneathIt()
    {
        string store = TestLinks.OutsideFolder(_base, "grouped-target", files: 1);
        Directory.CreateDirectory(Path.Combine(_repo, "a"));
        TestLinks.DirectoryLink(Path.Combine(_repo, "a", "linked"), store);

        IReadOnlyDictionary<string, List<string>> found = GitWorktreeProvider.FindShadowingLinks(
            _repo,
            ["a/linked/x.txt", "a/linked/sub/y.txt", "a/real.txt", "README.md", "missing/dir/z.txt"],
            _ => false);

        KeyValuePair<string, List<string>> only = Assert.Single(found);
        Assert.Equal("a/linked", only.Key);
        Assert.Equal(["a/linked/x.txt", "a/linked/sub/y.txt"], only.Value);
    }

    [Fact]
    public void FindShadowingLinks_ExemptsAPathThatIsItselfATrackedSymlink_ButNotAnUntrackedLink()
    {
        string store = TestLinks.OutsideFolder(_base, "exempt-target", files: 1);
        TestLinks.DirectoryLink(Path.Combine(_repo, "tracked-link"), store);
        TestLinks.DirectoryLink(Path.Combine(_repo, "operator-link"), store);

        IReadOnlyDictionary<string, List<string>> found = GitWorktreeProvider.FindShadowingLinks(
            _repo, ["tracked-link", "operator-link"], p => p == "tracked-link");

        Assert.Equal(["operator-link"], found.Keys);
    }

    /// <summary>
    /// NEGATIVE CONTROL: the same shape handed straight to <c>git merge</c> — what every delivery did before #835 —
    /// writes through the junction into the external store. If this ever stops showing the write, the refusals above
    /// stop proving anything. Characterizes the installed git: where it does not reproduce the hazard, say so and skip.
    /// </summary>
    [Fact]
    public void RawGitMerge_OnTheSameShape_WritesThroughTheJunction_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "git follows a directory JUNCTION on Windows; Unix git refuses a path beyond a symlink");
        ReplaceAssetsWithLink();
        (_, IntegrationHandle integ) =
            PlanWith(wt => File.WriteAllText(Path.Combine(wt, "assets", "added.txt"), "plan output\n"));

        try { Git(_repo, "merge", "--ff-only", integ.PlanBranchName); }
        catch (InvalidOperationException) { /* the damage is what is asserted */ }

        if (!File.Exists(Path.Combine(_outside, "added.txt")))
        {
            Assert.Skip($"{Git(_repo, "--version").Trim()} did not write through the junction on `merge`; "
                + "the guard tests above still assert the target stays untouched");
        }

        Assert.Equal("plan output\n", File.ReadAllText(Path.Combine(_outside, "added.txt")).Replace("\r\n", "\n"));
    }

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
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
