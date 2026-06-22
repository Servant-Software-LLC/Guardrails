namespace Guardrails.Core.Io;

/// <summary>
/// Windows-safe recursive directory deletion (issue #109). On Windows, git marks loose and
/// packed objects under <c>.git/objects/</c> <b>read-only</b>; .NET's
/// <see cref="Directory.Delete(string, bool)"/> throws <see cref="UnauthorizedAccessException"/>
/// — which is <b>not</b> an <see cref="IOException"/> — the moment it reaches one, so any code
/// that deletes a directory containing a git repo or worktree fails on Windows (and the common
/// <c>catch (IOException)</c> does not catch it). <see cref="DeleteDirectory"/> strips the
/// read-only attribute off every file first, then deletes, retrying briefly to ride out a
/// transient lock (e.g. a virus scanner or git background process still holding a handle).
/// </summary>
public static class SafeDelete
{
    /// <summary>
    /// Recursively delete <paramref name="path"/>, clearing read-only attributes first so
    /// read-only git objects do not abort the delete on Windows. A no-op when the directory does
    /// not exist. Transient failures (a momentary lock) are retried a few times with a short
    /// backoff; the final attempt is allowed to throw so a genuine, persistent failure still
    /// surfaces rather than being silently swallowed.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                                        attempt < maxAttempts && Directory.Exists(path))
            {
                // A handle is still open (scanner, git background gc) or an attribute clear raced a
                // concurrent write. Back off briefly and retry — the read-only re-clear on the next
                // pass also picks up any file created since the last sweep.
                Thread.Sleep(20 * attempt);
            }
        }
    }

    /// <summary>
    /// Clear the read-only attribute from every file under <paramref name="root"/> (and the
    /// directory entries themselves), so a subsequent recursive delete is not blocked by a
    /// read-only git object. Best-effort per entry: a file that vanishes mid-sweep (e.g. a
    /// concurrent git process pruning it) is skipped rather than aborting the clear.
    /// </summary>
    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            TryClear(file);
        }

        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            TryClear(dir);
        }

        TryClear(root);
    }

    private static void TryClear(string entry)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // The entry vanished (concurrent prune) or cannot be touched — let the delete pass
            // surface any real, persistent problem.
        }
    }
}
