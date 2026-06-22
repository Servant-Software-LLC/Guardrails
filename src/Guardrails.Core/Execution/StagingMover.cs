using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The staging MOVE built-in (SSOT §3.5, issue #130): after a <c>stagingOutputs</c>-declaring task's
/// action succeeds and BEFORE the write-scope check and guardrails, relocate the action's staged
/// deliverable from the per-task staging root into its real <c>.claude/</c> destination inside the
/// task's segment worktree, then delete the whole staging tree so no scaffolding is committed.
/// </summary>
/// <remarks>
/// Pure and static: it takes the absolute staging root, the absolute effective-workspace root (the
/// segment worktree in worktree mode, the plan workspace in serial mode), and the declared
/// <c>from→to</c> list. It performs filesystem moves only — no git, no journal, no env. The
/// <see cref="TaskExecutor"/> owns the timing (gated on action success) and the failure routing
/// (an empty-source/IO failure is a failed attempt via <see cref="RetryPolicy.ForStagingFailure"/>).
///
/// <para><b>Move semantics.</b> For each entry, <c>from</c> is resolved against the staging root.
/// A glob (a <c>from</c> containing <c>*</c>) matches a subtree: each matched file's path RELATIVE
/// to the glob's fixed prefix (the leading non-wildcard directory segments) is preserved under
/// <c>to</c>. A bare <c>from</c> that names a directory moves that directory's whole subtree under
/// <c>to</c>; a bare <c>from</c> that names a file moves the one file. A <c>to</c> ending in <c>/</c>
/// (or that resolves to a directory) lands the matched source(s) UNDER it preserving relative
/// structure; a <c>to</c> naming a file moves a single matched source to that exact path. Directories
/// are created as needed; existing destination files are overwritten (last task wins, like any file
/// write). A declared <c>from</c> that matches NO files is a failure — the action did not produce
/// what it promised.</para>
/// </remarks>
public static class StagingMover
{
    /// <summary>
    /// Move every <paramref name="entries"/> mapping from <paramref name="stagingRoot"/> into
    /// <paramref name="workspaceRoot"/>, then delete the staging root tree. Returns the outcome; the
    /// caller routes a non-<see cref="StagingMoveResult.Succeeded"/> result as a failed attempt.
    /// </summary>
    public static StagingMoveResult Move(
        string stagingRoot,
        string workspaceRoot,
        IReadOnlyList<StagingOutput> entries)
    {
        var moved = new List<string>();

        try
        {
            foreach (StagingOutput entry in entries)
            {
                if (!MoveEntry(stagingRoot, workspaceRoot, entry, moved, out string? emptyReason))
                {
                    return StagingMoveResult.EmptySource(entry.From, emptyReason!);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StagingMoveResult.IoError(ex.Message);
        }
        finally
        {
            // Primary git-hygiene guarantee: the staging tree never reaches a commit. Best-effort —
            // a delete failure must not mask a successful move (the segment reset/clean catches residue).
            TryDeleteTree(stagingRoot);
        }

        return StagingMoveResult.Ok(moved);
    }

    /// <summary>
    /// Move one entry. Returns false (with <paramref name="emptyReason"/> set) when the declared
    /// <c>from</c> matches no files under the staging root — a deliverable-not-produced condition.
    /// </summary>
    private static bool MoveEntry(
        string stagingRoot,
        string workspaceRoot,
        StagingOutput entry,
        List<string> moved,
        out string? emptyReason)
    {
        emptyReason = null;
        bool toIsDirectory = entry.To.EndsWith('/') || entry.To.EndsWith('\\');
        string toRelative = entry.To.Replace('\\', '/').TrimEnd('/');

        if (entry.From.Contains('*'))
        {
            return MoveGlob(stagingRoot, workspaceRoot, entry.From, toRelative, moved, out emptyReason);
        }

        // A bare (non-glob) from: a directory moves its whole subtree; a file moves the one file.
        string sourcePath = Path.Combine(stagingRoot, entry.From.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(sourcePath))
        {
            string[] files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                emptyReason = "the staged directory is empty";
                return false;
            }

            foreach (string file in files)
            {
                string relativeBelowSource = Path.GetRelativePath(sourcePath, file).Replace('\\', '/');
                string destination = CombineWorkspace(workspaceRoot, toRelative, relativeBelowSource);
                MoveFile(file, destination, moved, workspaceRoot);
            }

            return true;
        }

        if (File.Exists(sourcePath))
        {
            // A directory-shaped to ("foo/") lands the file UNDER it keeping the basename; a
            // file-shaped to is the exact destination path.
            string destination = toIsDirectory
                ? CombineWorkspace(workspaceRoot, toRelative, Path.GetFileName(sourcePath))
                : Path.Combine(workspaceRoot, toRelative.Replace('/', Path.DirectorySeparatorChar));
            MoveFile(sourcePath, destination, moved, workspaceRoot);
            return true;
        }

        emptyReason = "no file or directory exists at that staged path";
        return false;
    }

    /// <summary>
    /// Move a glob <c>from</c> (e.g. <c>skill/**</c>): every matched file's path relative to the
    /// glob's fixed (non-wildcard) directory prefix is preserved under <paramref name="toRelative"/>.
    /// </summary>
    private static bool MoveGlob(
        string stagingRoot,
        string workspaceRoot,
        string from,
        string toRelative,
        List<string> moved,
        out string? emptyReason)
    {
        emptyReason = null;
        string normalizedFrom = from.Replace('\\', '/');

        // The fixed prefix = leading segments before the first one containing '*'. Matches are
        // re-rooted relative to this prefix so 'skill/**' under staging lands the subtree below
        // 'skill/' directly under 'to'.
        string[] segments = normalizedFrom.Split('/');
        var fixedSegments = segments.TakeWhile(s => !s.Contains('*')).ToArray();
        string fixedPrefix = string.Join('/', fixedSegments);

        string searchRoot = string.IsNullOrEmpty(fixedPrefix)
            ? stagingRoot
            : Path.Combine(stagingRoot, fixedPrefix.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(searchRoot))
        {
            emptyReason = "the staged source directory does not exist";
            return false;
        }

        // Resolve the glob against the staging tree (the matcher already used for write-scope).
        var matched = new List<string>();
        foreach (string file in Directory.GetFiles(stagingRoot, "*", SearchOption.AllDirectories))
        {
            string relativeFromStaging = Path.GetRelativePath(stagingRoot, file).Replace('\\', '/');
            if (WriteScope.IsInScope(relativeFromStaging, [normalizedFrom]))
            {
                matched.Add(file);
            }
        }

        if (matched.Count == 0)
        {
            emptyReason = "the glob matched no files under the staging dir";
            return false;
        }

        string prefixWithSlash = string.IsNullOrEmpty(fixedPrefix) ? "" : fixedPrefix + "/";
        foreach (string file in matched)
        {
            string relativeFromStaging = Path.GetRelativePath(stagingRoot, file).Replace('\\', '/');
            string relativeBelowPrefix = relativeFromStaging.StartsWith(prefixWithSlash, StringComparison.Ordinal)
                ? relativeFromStaging[prefixWithSlash.Length..]
                : Path.GetFileName(relativeFromStaging);
            string destination = CombineWorkspace(workspaceRoot, toRelative, relativeBelowPrefix);
            MoveFile(file, destination, moved, workspaceRoot);
        }

        return true;
    }

    private static string CombineWorkspace(string workspaceRoot, string toRelative, string relativeBelow) =>
        Path.Combine(
            workspaceRoot,
            toRelative.Replace('/', Path.DirectorySeparatorChar),
            relativeBelow.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Move one file to <paramref name="destination"/>, creating parent dirs and overwriting an
    /// existing destination. Records the destination's workspace-relative path in
    /// <paramref name="moved"/> (the post-move changed surface).
    /// </summary>
    private static void MoveFile(string source, string destination, List<string> moved, string workspaceRoot)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
        moved.Add(Path.GetRelativePath(workspaceRoot, destination).Replace('\\', '/'));
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the segment reset/clean (F2) sweeps any residue on the next attempt, and
            // the move's durable effect is the .claude/ files, not the staging dir.
        }
    }
}

/// <summary>
/// The outcome of a <see cref="StagingMover.Move"/> call (SSOT §3.5).
/// </summary>
public sealed record StagingMoveResult
{
    /// <summary>True when every declared <c>from</c> moved at least one file with no IO error.</summary>
    public bool Succeeded { get; init; }

    /// <summary>The workspace-relative destination paths the move produced (the post-move surface).</summary>
    public IReadOnlyList<string> MovedPaths { get; init; } = [];

    /// <summary>A human-readable reason when <see cref="Succeeded"/> is false; null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The declared <c>from</c> that produced no files, when the failure was an empty source.</summary>
    public string? EmptySourceFrom { get; init; }

    public static StagingMoveResult Ok(IReadOnlyList<string> moved) =>
        new() { Succeeded = true, MovedPaths = moved };

    public static StagingMoveResult EmptySource(string from, string reason) =>
        new()
        {
            Succeeded = false,
            EmptySourceFrom = from,
            FailureReason = $"stagingOutputs entry '{from}' matched no files under the staging dir ({reason})"
        };

    public static StagingMoveResult IoError(string message) =>
        new() { Succeeded = false, FailureReason = $"staging move failed with an IO error: {message}" };
}
