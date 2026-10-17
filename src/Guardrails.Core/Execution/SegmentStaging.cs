using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #280 (SSOT §5.3(D)): the single home of the harness's segment-staging policy — the curated
/// set of reconstructable dependency/build directories that must NEVER be captured into a segment
/// commit, and the excluding <c>git add</c> primitive every harness staging site routes through.
/// </summary>
/// <remarks>
/// A guardrail is a <i>verifier</i>: its filesystem side effects (an <c>npm ci</c>, a build cache, a
/// generated <c>dist/</c>) should never become part of the committed artifact. The observed #280
/// incident: a guardrail ran <c>npm ci</c> to smoke-test an import, creating a nested
/// <c>node_modules</c> that a plain <c>git add -A</c> segment commit then captured — shipping a broken
/// vendored dependency. Excluding the reconstructable set at EVERY staging site makes those dirs
/// uniformly invisible to harness git, closing the <c>.gitignore</c>-timing fragility and the
/// reused-linear-chain-worktree false-violation regardless of whether the task declares a
/// <c>writeScope</c>.
/// <para>
/// <b>Stage-exclusion, NOT worktree deletion (the #255 constraint):</b> the dirs remain on disk
/// (discarded with the segment, or left in place for a reused worktree) — they are only kept out of
/// the <i>index/commit</i>. The future warm-cache / worktree-pool work (#255) depends on the files
/// staying put; per-worktree dependency reconstruction (#259) is complementary.
/// </para>
/// <para>
/// <b>Also applied to <see cref="GitWorktreeProvider.PreserveAttemptToRef"/></b> (issue #306 review
/// NIT-2). The #195 retry-salvage snapshot was originally exempt on the grounds that it is only
/// human-inspected — but #306 makes it an AGENT-APPLYABLE patch (<c>git apply prior-attempt.patch</c>),
/// so the same reconstructable dirs that must stay out of a segment commit must also stay out of the
/// salvage patch (patch-bloat + consistency). The snapshot still stages into its own THROWAWAY index
/// (<c>GIT_INDEX_FILE</c>), so applying the exclusions there never touches the segment's real index.
/// </para>
/// </remarks>
public static class SegmentStaging
{
    /// <summary>
    /// The curated set of path-component names EXCLUDED from every harness segment-staging
    /// <c>git add</c> (SSOT §5.3(D)). v1: <c>node_modules</c> at any depth (a reconstructable dependency
    /// dir), plus the harness's own scaffolding folders <c>.guardrails-staging/</c> (§3.5) and
    /// <c>.guardrails-agent-io/</c> (§9.5). Extensible in code; a <c>guardrails.json</c>-driven set is
    /// deferred. Each name is matched as a whole path COMPONENT at any depth (see
    /// <see cref="StageAllArguments"/>), so a file merely CONTAINING the name as a substring
    /// (<c>src/node_modules_helper.cs</c>) is never excluded.
    /// </summary>
    public static readonly IReadOnlyList<string> ReconstructableExclusions =
    [
        "node_modules",
        ".guardrails-staging",
        ".guardrails-agent-io"
    ];

    /// <summary>
    /// The <c>git add</c> argument vector that stages the whole worktree EXCEPT the
    /// <see cref="ReconstructableExclusions"/> set: <c>add -A -- .</c> followed by one
    /// <c>:(exclude,glob)**/&lt;name&gt;/**</c> pathspec per excluded name. The leading <c>**/</c> makes
    /// the exclusion match at ANY depth including the repo root (git's <c>:(glob)</c> <c>**/</c> matches
    /// zero or more leading path components), and the trailing <c>/**</c> excludes the directory's whole
    /// subtree. Crucially, an in-scope MODIFY/DELETE of a file NOT under an excluded dir is still staged
    /// (the <c>-A</c> semantics apply to everything the excludes do not remove) — pinned by
    /// <c>SegmentStagingTests</c>.
    /// </summary>
    public static IReadOnlyList<string> StageAllArguments(IReadOnlyList<string>? skippedNestedRepos = null)
    {
        var args = new List<string> { "add", "-A", "--", "." };
        foreach (string name in ReconstructableExclusions)
        {
            args.Add($":(exclude,glob)**/{name}/**");
        }

        // #827: each commit-less nested repo is excluded by its exact (literal) path, so a name holding glob
        // characters or spaces still names only that directory. ArgumentList carries it unquoted.
        foreach (string repo in skippedNestedRepos ?? [])
        {
            args.Add($":(exclude,literal){repo}");
        }

        return args;
    }

    /// <summary>
    /// #827: the untracked nested git repositories under <paramref name="repoPath"/> that have NO commit checked
    /// out (a fresh <c>git init</c>), as repo-relative forward-slash directory paths without a trailing slash.
    /// <c>git add -A</c> cannot index such a directory as a gitlink and fails the whole add ("does not have a
    /// commit checked out"), which took down the serial write-scope snapshot and worktree staging. A nested repo
    /// WITH a commit is not returned: git stages it as a gitlink exactly as before.
    /// <para>
    /// Detection is <c>ls-files --others --exclude-standard -z</c> (a nested repository is listed as a single
    /// entry ending in <c>/</c>; <c>-z</c> keeps a space or non-ASCII name literal) then
    /// <c>rev-parse --verify HEAD</c> inside each. FAILS TO THE OLD BEHAVIOR: if the listing itself errors, nothing
    /// is excluded and git reports its own error rather than files being silently dropped. A directory whose HEAD
    /// check errors for any reason is excluded only when git says it has no HEAD, i.e. exits non-zero — the same
    /// condition under which <c>add</c> would have failed on it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindCommitlessNestedRepos(
        string repoPath, string? indexFile = null, CancellationToken cancellationToken = default)
    {
        string listing;
        try
        {
            listing = ScopeGit.Run(
                repoPath, indexFile, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var found = new List<string>();
        foreach (string entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.EndsWith('/'))
            {
                continue;
            }

            string relative = entry.TrimEnd('/');
            string full = Path.Combine(repoPath, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                ScopeGit.Run(full, indexFile: null, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
            }
            catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
            {
                found.Add(relative);
            }
        }

        return found;
    }

    /// <summary>
    /// Run the excluding <c>git add</c> (<see cref="StageAllArguments"/>) in
    /// <paramref name="repoPath"/>. Throws <see cref="InvalidOperationException"/> on a non-zero git exit
    /// — mirroring the callers' existing git runners so every routed site keeps its current error
    /// contract (the write-scope check catches this to fail/keep-open; <see cref="GitWorktreeProvider"/>
    /// lets it propagate as an integration fault, #150).
    /// </summary>
    /// <param name="repoPath">The working tree to stage.</param>
    /// <param name="indexFile">
    /// #816: a private <c>GIT_INDEX_FILE</c> to stage into (the serial-mode scope snapshot, <see cref="ScopeDiffBase"/>),
    /// so the repository's own index is never touched. Null — every other caller — stages the repository's index.
    /// </param>
    /// <param name="cancellationToken">
    /// #816 review: bounds the call (with <see cref="ScopeGit"/>'s timeout) so a hung git can never wedge a run.
    /// </param>
    /// <returns>
    /// #827: the commit-less nested repositories left OUT of the add (<see cref="FindCommitlessNestedRepos"/>),
    /// so every caller can REPORT them — the skip is never silent. Empty in the ordinary case.
    /// </returns>
    public static IReadOnlyList<string> StageAll(
        string repoPath, string? indexFile = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> skipped = FindCommitlessNestedRepos(repoPath, indexFile, cancellationToken);
        // #816 review: through ScopeGit, which drains stderr concurrently. Reading stdout to its end first deadlocked
        // once git's per-file line-ending warnings filled the stderr pipe (~35 files under core.autocrlf=true).
        ScopeGit.Run(repoPath, indexFile, StageAllArguments(skipped), cancellationToken);
        return skipped;
    }
}
