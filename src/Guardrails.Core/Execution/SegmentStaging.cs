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
    public static IReadOnlyList<string> StageAllArguments()
    {
        var args = new List<string> { "add", "-A", "--", "." };
        foreach (string name in ReconstructableExclusions)
        {
            args.Add($":(exclude,glob)**/{name}/**");
        }

        return args;
    }

    /// <summary>
    /// Run the excluding <c>git add</c> (<see cref="StageAllArguments"/>) in
    /// <paramref name="repoPath"/>. Throws <see cref="InvalidOperationException"/> on a non-zero git exit
    /// — mirroring the callers' existing git runners so every routed site keeps its current error
    /// contract (the write-scope check catches this to fail/keep-open; <see cref="GitWorktreeProvider"/>
    /// lets it propagate as an integration fault, #150).
    /// </summary>
    public static void StageAll(string repoPath)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in StageAllArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", StageAllArguments())} (in {repoPath}) exited {proc.ExitCode}: {stderr.Trim()}");
        }
    }
}
