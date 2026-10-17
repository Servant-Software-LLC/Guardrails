namespace Guardrails.Core.Io;

/// <summary>
/// The harness's ONE recursive directory delete — Windows-safe (issue #109) and link-safe (issue #826).
/// <para>
/// <b>#109.</b> On Windows, git marks loose and packed objects under <c>.git/objects/</c> <b>read-only</b>;
/// .NET's <see cref="Directory.Delete(string, bool)"/> throws <see cref="UnauthorizedAccessException"/> —
/// which is <b>not</b> an <see cref="IOException"/> — the moment it reaches one. The read-only attribute is
/// stripped off every entry first, retrying briefly to ride out a transient lock.
/// </para>
/// <para>
/// <b>#826.</b> A link inside the tree (symlink, junction, dangling link) points at something the harness
/// does not own. The tree is walked WITHOUT following links (<see cref="LinkSafeTree.RemoveLinks"/>) and
/// every link is removed as an ENTRY before the recursive delete runs, so the delete only ever sees real
/// files and directories. (Measured, Windows: .NET's recursive delete does not delete through a junction
/// — it removes the link and then throws on it — but the <c>AllDirectories</c> enumeration the read-only
/// clear used to run DID walk into the target and rewrite outside files' attributes.) If any link cannot be
/// removed the delete is REFUSED with a <see cref="LinkRemovalException"/> and nothing is deleted.
/// </para>
/// </summary>
public static class SafeDelete
{
    /// <summary>
    /// Recursively delete <paramref name="path"/> without ever deleting through a link. When
    /// <paramref name="path"/> is itself a link, only the link is removed. A no-op when nothing is there.
    /// Transient failures (a momentary lock) are retried a few times with a short backoff; the final
    /// attempt is allowed to throw so a genuine, persistent failure still surfaces rather than being
    /// silently swallowed.
    /// </summary>
    /// <exception cref="LinkRemovalException">A link inside the tree could not be removed; nothing was deleted.</exception>
    public static void DeleteDirectory(string path)
    {
        if (LinkSafeTree.IsLink(path))
        {
            LinkSweepResult root = LinkSafeTree.RemoveLinks(path);
            if (!root.Safe) throw new LinkRemovalException(path, root.Unremoved);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // One walk does both jobs: disarm every link, and clear read-only off every real entry.
                // Re-run on each retry so an entry created since the last pass is covered too.
                LinkSweepResult sweep = LinkSafeTree.RemoveLinks(path, clearReadOnly: true);
                if (!sweep.Safe)
                {
                    throw new LinkRemovalException(path, sweep.Unremoved);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                                        attempt < maxAttempts && Directory.Exists(path))
            {
                // A handle is still open (scanner, git background gc) or an attribute clear raced a
                // concurrent write. Back off briefly and retry.
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
