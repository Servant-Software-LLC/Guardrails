using Guardrails.Core.Io;

namespace Guardrails.Core.Execution;

/// <summary>
/// What an attempt's write-scope check diffs against (issue #816): a git tree-ish to compare the attempt's
/// working tree with, the repository it lives in, and — in serial mode only — the throwaway index the
/// comparison is staged into.
///
/// <para><b>Worktree mode</b> (<see cref="ForSegment"/>): the segment's own index and its <c>taskBase</c> commit,
/// byte-for-byte what <see cref="WriteScopeCheck"/> has always done.</para>
///
/// <para><b>Serial mode</b> (<see cref="TryCaptureSerial"/>): there is no segment and no <c>taskBase</c> — the
/// task writes straight into the operator's checkout, on top of every earlier task's UNCOMMITTED work. So the
/// base is a SNAPSHOT of the workspace taken immediately before the attempt's action: the working tree staged
/// into a private index (<c>GIT_INDEX_FILE</c>, seeded from the real index so the stat cache keeps it cheap) and
/// written as a tree object. The same private index is re-staged after the action and diffed against that tree,
/// so the operator's real index is never read for the verdict and never written. The diff therefore names
/// exactly what changed DURING the attempt — never an earlier task's work, never the operator's own pending
/// changes made before the run.</para>
///
/// <para>Never an offense, so never reverted: the reconstructable/scaffolding set every harness staging site
/// excludes (<see cref="SegmentStaging"/>), plus the plan's <c>logs/</c> and <c>state/</c> directories when the
/// plan folder sits inside the workspace — the harness itself writes there while the attempt runs. Those two are
/// dropped from the diff by PREFIX (<see cref="HarnessOwnedPrefixes"/>) rather than excluded by a pathspec: git
/// refuses an exclude pathspec that names a <c>.gitignore</c>d directory, which a plan's <c>logs/</c> usually is.</para>
/// </summary>
public sealed class ScopeDiffBase : IDisposable
{
    private ScopeDiffBase(string repoPath, string baseTreeish, string? indexFile, IReadOnlyList<string> harnessOwnedPrefixes)
    {
        RepoPath = repoPath;
        Base = baseTreeish;
        IndexFile = indexFile;
        HarnessOwnedPrefixes = harnessOwnedPrefixes;
    }

    /// <summary>The repository working tree the check stages and reverts in.</summary>
    public string RepoPath { get; }

    /// <summary>The tree-ish the attempt is diffed against: the segment's taskBase, or the serial snapshot tree.</summary>
    public string Base { get; }

    /// <summary>The private <c>GIT_INDEX_FILE</c> (serial mode), or null to use the repository's own index.</summary>
    public string? IndexFile { get; }

    /// <summary>
    /// Workspace-relative, forward-slashed directory prefixes (each ending in <c>/</c>) the harness itself writes
    /// under during an attempt; a changed path under one is never an offense. Empty in worktree mode, where the
    /// plan's logs and state live in the operator's checkout, not the segment.
    /// </summary>
    public IReadOnlyList<string> HarnessOwnedPrefixes { get; }

    /// <summary>True when <paramref name="path"/> (workspace-relative, forward-slashed) is harness-owned.</summary>
    public bool IsHarnessOwned(string path) =>
        HarnessOwnedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worktree-mode base: the segment's own index, diffed against its taskBase.</summary>
    public static ScopeDiffBase ForSegment(string worktreePath, string taskBase) =>
        new(worktreePath, taskBase, indexFile: null, harnessOwnedPrefixes: []);


    /// <summary>
    /// Why the serial snapshot cannot be taken in <paramref name="workspace"/>, or null when it can: the workspace
    /// must be the top level of a git work tree (the scope globs are workspace-relative, and git reports paths
    /// relative to its top level). Asked once per executor — the answer cannot change during a run.
    /// </summary>
    public static string? SerialUnavailableReason(string workspace)
    {
        string topLevel;
        try
        {
            topLevel = ScopeGit.Run(workspace, indexFile: null, ["rev-parse", "--show-toplevel"]).Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return $"the workspace '{workspace}' is not a git work tree";
        }

        return RealPath.SamePath(Path.GetFullPath(topLevel), Path.GetFullPath(workspace))
            ? null
            : $"the workspace '{workspace}' is not the top level of its git work tree ('{topLevel}')";
    }

    /// <summary>
    /// Snapshot <paramref name="workspace"/> for a serial attempt (see the type remarks), in raw-bytes mode
    /// (<see cref="ScopeGit"/>) so a revert later writes back exactly the bytes that were on disk. On any git
    /// failure returns null with <paramref name="failure"/> set — the caller runs the attempt without the
    /// retrospective check and says so LOUDLY; a snapshot failure must never fail the attempt itself.
    /// </summary>
    public static ScopeDiffBase? TryCaptureSerial(
        string workspace, string planDirectory, out string? failure, CancellationToken cancellationToken = default)
    {
        string indexFile = NewPrivateIndex(workspace, cancellationToken, out failure);
        if (failure is not null)
        {
            return null;
        }

        try
        {
            SegmentStaging.StageAll(workspace, indexFile, cancellationToken);
            string tree = ScopeGit.Run(workspace, indexFile, ["write-tree"], cancellationToken).Trim();
            return new ScopeDiffBase(workspace, tree, indexFile, PlanOwnedPrefixes(workspace, planDirectory));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            TryDelete(indexFile);
            failure = $"git could not snapshot the workspace before the attempt ({ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// A serial base over a snapshot tree an EARLIER process journaled (#816 review, Q2b): an attempt that never
    /// ended — the harness was killed, crashed, or the host slept — left its snapshot in <c>run.json</c>, and the
    /// resumed task diffs the workspace against it before its next attempt starts. Null (with
    /// <paramref name="failure"/>) when git cannot set up the private index or no longer has the tree.
    /// </summary>
    public static ScopeDiffBase? TryFromSerialTree(
        string workspace, string planDirectory, string tree, out string? failure)
    {
        string indexFile = NewPrivateIndex(workspace, CancellationToken.None, out failure);
        if (failure is not null)
        {
            return null;
        }

        try
        {
            ScopeGit.Run(workspace, indexFile, ["cat-file", "-e", tree + "^{tree}"]);
            return new ScopeDiffBase(workspace, tree, indexFile, PlanOwnedPrefixes(workspace, planDirectory));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            TryDelete(indexFile);
            failure = $"the journaled snapshot tree {tree} is no longer readable ({ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// A private index file under the system temp dir, seeded from the real index for SPEED only (the stat cache):
    /// whatever the real index holds is overwritten by the staging for every path it covers, and nothing but this
    /// class ever touches the copy.
    /// </summary>
    private static string NewPrivateIndex(string workspace, CancellationToken cancellationToken, out string? failure)
    {
        string indexFile = Path.Combine(Path.GetTempPath(), $"gr-scope-index-{Guid.NewGuid():N}");
        try
        {
            string realIndex = Path.GetFullPath(Path.Combine(
                workspace, ScopeGit.Run(workspace, null, ["rev-parse", "--git-path", "index"], cancellationToken).Trim()));
            if (File.Exists(realIndex))
            {
                File.Copy(realIndex, indexFile, overwrite: true);
            }

            failure = null;
            return indexFile;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            TryDelete(indexFile);
            failure = $"git could not prepare a private index ({ex.Message})";
            return indexFile;
        }
    }

    /// <summary>
    /// The plan's harness-written directories as workspace-relative prefixes, when the plan folder sits inside
    /// the workspace: <c>logs/</c> (every attempt's artifacts, written while the attempt runs) and <c>state/</c>
    /// (<c>run.json</c>, <c>state.json</c>). Empty when the plan folder lives elsewhere. Both endpoints are
    /// canonicalised (<see cref="RealPath.Resolve"/>) first, as <c>TaskExecutor.ResolveWorkingDirectory</c> does, so
    /// a symlinked temp root (macOS <c>/var</c> → <c>/private/var</c>) cannot make a nested plan folder look
    /// outside the workspace.
    /// </summary>
    internal static IReadOnlyList<string> PlanOwnedPrefixes(string workspace, string planDirectory)
    {
        string relative = Path.GetRelativePath(
                RealPath.Resolve(Path.GetFullPath(workspace)), RealPath.Resolve(Path.GetFullPath(planDirectory)))
            .Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return [];
        }

        string prefix = relative == "." ? "" : relative + "/";
        return [prefix + "logs/", prefix + "state/"];
    }

    /// <summary>Delete the private index. The snapshot TREE is an ordinary unreferenced object git prunes on its own.</summary>
    public void Dispose()
    {
        if (IndexFile is not null)
        {
            TryDelete(IndexFile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a stray temp file is harmless.
        }
    }
}
