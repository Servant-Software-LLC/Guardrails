namespace Guardrails.Core.Io;

/// <summary>
/// Issue #826: the ONE link-safe tree walk every harness recursive delete goes through. A link — a
/// directory symlink, a file symlink, a Windows junction, or a dangling one of any of those — inside a
/// harness-owned tree points at something the harness does NOT own (an <c>npm link</c> / pnpm entry under
/// <c>node_modules</c> points at the developer's own package source). Measured on Windows (git 2.53):
/// <c>git worktree remove --force</c> deletes EVERY file in a junction's target, and .NET's recursive
/// enumeration walks into it (so the read-only clear <see cref="SafeDelete"/> used to run before deleting
/// rewrote the attributes of files outside the tree).
/// <para>
/// The walk reads each entry with lstat semantics — the entry itself, never what it points at — and NEVER
/// enumerates into a link: a link is removed as an ENTRY (<see cref="TryRemoveLink"/>) and the walk moves
/// on. Only real directories are descended into, so the cost is one directory listing per real directory
/// in the tree (the same listings a recursive delete makes anyway) and nothing inside any link target.
/// </para>
/// </summary>
public static class LinkSafeTree
{
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,            // hidden/system entries can be links too
        IgnoreInaccessible = false,      // an unlistable directory is reported, never silently skipped
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// True when <paramref name="entry"/> is itself a link (symlink, junction, dangling link), read without
    /// following it. On Windows the reparse-point attribute (free from the directory listing) gates the
    /// per-entry <see cref="FileSystemInfo.LinkTarget"/> read, which opens a handle; <c>LinkTarget</c> alone
    /// decides, because non-link reparse points (a OneDrive Files-On-Demand folder) carry the attribute too.
    /// On Unix <c>LinkTarget</c> is one <c>readlink</c>, cheap enough to ask of every entry.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell: a reparse point we cannot read is treated as a link, so it is never walked into.
            return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }
    }

    /// <summary>True when <paramref name="path"/> is itself a link (read without following it).</summary>
    public static bool IsLink(string path) => IsLink(new FileInfo(path));

    /// <summary>
    /// Remove the link at <paramref name="linkPath"/> — the ENTRY only, never what it points at. A Windows
    /// junction or directory symlink is removed with a non-recursive directory delete (<c>RemoveDirectory</c>
    /// on the link); everything else — a file symlink, a dangling link, and every link on Unix — with a file
    /// delete (<c>unlink</c> does not follow). Returns false, deleting nothing, when the path is not a link or
    /// the removal failed; true only when the entry is verifiably gone.
    /// </summary>
    public static bool TryRemoveLink(string linkPath)
    {
        try
        {
            var entry = new FileInfo(linkPath);
            if (entry.LinkTarget is null)
            {
                return false; // not a link: never delete a real directory or file here
            }

            if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                // A read-only LINK refuses removal (RemoveDirectory → access denied). The attribute is the link
                // entry's own — measured: setting/clearing it on a junction leaves the target's untouched.
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(linkPath, recursive: false); // removes the junction / dir-symlink entry only
            }
            else
            {
                File.Delete(linkPath);
            }

            return !EntryExists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walk <paramref name="root"/> without ever following a link and remove every link entry in it
    /// (<see cref="TryRemoveLink"/>). When <paramref name="root"/> is itself a link, only that link is removed.
    /// </summary>
    /// <param name="root">The tree to disarm. A missing root is a no-op.</param>
    /// <param name="skipRootGitDirectory">
    /// Do not descend into <c>&lt;root&gt;/.git</c> — the repository's own internals (a main checkout's object
    /// store), which can be large. ONLY the root's: a nested repository's <c>.git</c> (a vendored checkout, a
    /// submodule) is ordinary content that can hold a link like any other directory (#826 review WEAK 1).
    /// </param>
    /// <param name="clearReadOnly">
    /// Also clear the read-only attribute on every real file and directory visited (issue #109: git marks
    /// loose objects read-only, which aborts a recursive delete on Windows) — folded into the same walk so a
    /// delete lists each directory once.
    /// </param>
    /// <returns>What was removed, and every link (or unlistable directory) that could NOT be made safe.</returns>
    public static LinkSweepResult RemoveLinks(string root, bool skipRootGitDirectory = false, bool clearReadOnly = false) =>
        Walk(root, skipRootGitDirectory, clearReadOnly, removeLinks: true);

    /// <summary>
    /// Every link under <paramref name="root"/> (and <paramref name="root"/> itself when it is one), found by the
    /// same no-follow walk as <see cref="RemoveLinks"/> but REMOVING NOTHING — for a tree the harness does not own
    /// (the operator's checkout), where a link is theirs. <see cref="LinkSweepResult.Removed"/> is always empty;
    /// <see cref="LinkSweepResult.Unremoved"/> lists the links found (and any directory that could not be listed).
    /// </summary>
    public static LinkSweepResult FindLinks(string root, bool skipRootGitDirectory = false) =>
        Walk(root, skipRootGitDirectory, clearReadOnly: false, removeLinks: false);

    private static LinkSweepResult Walk(string root, bool skipRootGitDirectory, bool clearReadOnly, bool removeLinks)
    {
        var removed = new List<string>();
        var unsafeEntries = new List<string>();
        void OnLink(string path) => (removeLinks && TryRemoveLink(path) ? removed : unsafeEntries).Add(path);

        if (IsLink(root))
        {
            OnLink(root);
            return new LinkSweepResult(removed, unsafeEntries);
        }

        if (!Directory.Exists(root))
        {
            return new LinkSweepResult(removed, unsafeEntries);
        }

        var rootInfo = new DirectoryInfo(root);
        string rootGit = Path.Combine(rootInfo.FullName, ".git");
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                // Materialized per directory: entries are removed while this level is processed.
                entries = directory.EnumerateFileSystemInfos("*", OneLevel).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                continue; // vanished mid-walk (a concurrent prune) — nothing left in it to disarm
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Links inside it cannot be seen, so the tree cannot be proven safe to delete.
                unsafeEntries.Add($"{directory.FullName} (could not be listed: {ex.Message})");
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (IsLink(entry))
                {
                    OnLink(entry.FullName);
                    continue;
                }

                if (clearReadOnly)
                {
                    ClearReadOnly(entry);
                }

                if (entry is DirectoryInfo child
                    && !(skipRootGitDirectory && string.Equals(child.FullName, rootGit, StringComparison.OrdinalIgnoreCase)))
                {
                    pending.Push(child);
                }
            }
        }

        if (clearReadOnly)
        {
            ClearReadOnly(rootInfo);
        }

        return new LinkSweepResult(removed, unsafeEntries);
    }

    /// <summary>
    /// Every real directory under <paramref name="root"/> named <paramref name="name"/>, found WITHOUT
    /// descending into a link (unlike <c>Directory.EnumerateDirectories(…, AllDirectories)</c>, which on
    /// Windows walks into junctions — measured). Unlistable directories are skipped: this is a search, and
    /// a miss only leaves a harness directory behind.
    /// </summary>
    public static List<string> FindDirectoriesNamed(string root, string name)
    {
        var found = new List<string>();
        if (!Directory.Exists(root) || IsLink(root))
        {
            return found;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            List<DirectoryInfo> children;
            try { children = directory.EnumerateDirectories("*", OneLevel).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (DirectoryInfo child in children)
            {
                if (IsLink(child))
                {
                    continue;
                }

                if (string.Equals(child.Name, name, StringComparison.Ordinal))
                {
                    found.Add(child.FullName);
                }
                else
                {
                    pending.Push(child);
                }
            }
        }

        return found;
    }

    /// <summary>True when an entry — including a dangling link, which <see cref="Path.Exists"/> misses — is at the path.</summary>
    private static bool EntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void ClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            FileAttributes attributes = entry.Attributes;
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes = attributes & ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            // The entry vanished (concurrent prune) or cannot be touched — the delete surfaces any real problem.
        }
    }
}

/// <summary>The outcome of <see cref="LinkSafeTree.RemoveLinks"/>.</summary>
/// <param name="Removed">Every link entry removed (the link only; its target untouched).</param>
/// <param name="Unremoved">
/// Every link that could not be removed, plus any directory that could not be listed — each one a reason the
/// tree must NOT be handed to a link-following delete.
/// </param>
public sealed record LinkSweepResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Unremoved)
{
    /// <summary>True when nothing stands in the way of deleting the tree.</summary>
    public bool Safe => Unremoved.Count == 0;
}

/// <summary>
/// Issue #826: a harness delete REFUSED because a link inside the tree could not be removed as an entry.
/// The tree (and, for a worktree, its git registration) is left in place rather than risk deleting through
/// the link into files the harness does not own. An <see cref="IOException"/>, so the existing delete
/// failure paths carry it; every best-effort teardown site catches it by type and reports it loudly.
/// </summary>
public sealed class LinkRemovalException : IOException
{
    /// <summary>Create the refusal for <paramref name="tree"/>, naming every link that could not be removed.</summary>
    /// <param name="tree">The tree left in place.</param>
    /// <param name="unremoved">The links (or unlistable directories) in the way.</param>
    /// <param name="operation">
    /// What was refused, completing "refused to …" — by default deleting the tree; a git rewrite passes e.g.
    /// <c>"reset"</c> (<c>git reset --hard</c> / <c>git clean</c> write and delete through a link too).
    /// </param>
    public LinkRemovalException(string tree, IReadOnlyList<string> unremoved, string operation = "delete")
        : this([tree], unremoved, operation)
    {
    }

    private LinkRemovalException(IReadOnlyList<string> trees, IReadOnlyList<string> unremoved, string operation)
        : base(
            $"refused to {operation} {string.Join(", ", trees.Select(t => $"'{t}'"))}: {unremoved.Count} link(s) inside could not be " +
            $"removed, and doing so with them in place could delete or overwrite files OUTSIDE it through the link (issue #826). " +
            $"Left on disk: {string.Join("; ", unremoved)}. Remove each link ENTRY by hand (Windows: rmdir <link> — never " +
            $"rmdir /s; Unix: rm <link> — never rm -r), then re-run.")
    {
        Trees = trees;
        Unremoved = unremoved;
    }

    private LinkRemovalException(string message, IReadOnlyList<string> trees, IReadOnlyList<string> unremoved)
        : base(message)
    {
        Trees = trees;
        Unremoved = unremoved;
    }

    /// <summary>
    /// #826: a hard reset of a tree the OPERATOR may own was narrowed to HEAD + index, because the operator's own
    /// links (never removed — they are theirs, and removable) have tracked paths beneath them. The message says
    /// exactly what state the tree is now in and what makes the hard reset safe; it is NOT the "could not be
    /// removed" text, which would be wrong for a link nobody tried to remove.
    /// </summary>
    public static LinkRemovalException OperatorResetRefused(string workspace, string target, IReadOnlyList<string> links) =>
        new(
            $"reset '{workspace}' to {target} WITHOUT touching its working tree: HEAD and the index are now at {target}, " +
            $"but the working tree was left as it was — files from the undone change(s) remain on disk, now untracked or " +
            $"modified relative to {target}. `git reset --hard` was NOT run because these link(s), which are yours and were " +
            $"left in place, have tracked paths beneath them, and a hard reset would write or delete files THROUGH them in " +
            $"the folders they point at (issue #826): {string.Join("; ", links)}. Once each such link is moved out of the " +
            $"way, `git reset --hard {target}` is safe to run.",
            [workspace],
            links);

    /// <summary>The tree(s) left in place.</summary>
    public IReadOnlyList<string> Trees { get; }

    /// <summary>The links (or unlistable directories) that blocked the delete.</summary>
    public IReadOnlyList<string> Unremoved { get; }

    /// <summary>One refusal naming every tree and link in <paramref name="refusals"/>.</summary>
    public static LinkRemovalException Combine(IReadOnlyList<LinkRemovalException> refusals) =>
        refusals.Count == 1
            ? refusals[0]
            : new LinkRemovalException(
                refusals.SelectMany(r => r.Trees).ToList(), refusals.SelectMany(r => r.Unremoved).ToList(), "delete");
}
