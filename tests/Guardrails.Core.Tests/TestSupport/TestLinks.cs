using System.Runtime.InteropServices;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Microsoft.Win32.SafeHandles;

namespace Guardrails.TestSupport;

/// <summary>
/// Issue #826 test support: create the link shapes a harness teardown must never delete through, portably.
/// A DIRECTORY link is a junction on Windows (needs no privilege — the very shape <c>npm link</c> / pnpm make
/// there) and a symlink on Unix. A FILE symlink needs Developer Mode or elevation on Windows, so it is
/// optional (<see cref="TryFileSymlink"/>) and a test that needs it skips — saying so — rather than passing
/// vacuously.
/// </summary>
internal static class TestLinks
{
    internal const string FileSymlinkSkipReason =
        "could not create a FILE symlink here (Windows needs Developer Mode or elevation) — the file-link case "
        + "runs on Unix CI and on elevated Windows runners.";

    /// <summary>Create a directory link at <paramref name="link"/> → <paramref name="target"/>; throws when impossible.</summary>
    internal static void DirectoryLink(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(WorktreeJunction.TryCreateJunction(link, target), $"mklink /J {link} failed");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        Assert.True(LinkSafeTree.IsLink(link), $"fixture: {link} must be a link");
    }

    /// <summary>A link whose target does not exist (a deleted junction target on Windows, a symlink to nowhere on Unix).</summary>
    internal static void DanglingLink(string link, string scratchParent)
    {
        string gone = Path.Combine(scratchParent, "gone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(gone);
        DirectoryLink(link, gone);
        Directory.Delete(gone);
        Assert.False(Directory.Exists(gone));
        Assert.True(LinkSafeTree.IsLink(link), "fixture: the dangling link must still be there");
    }

    /// <summary>Try to create a FILE symlink; false (nothing created) when this machine forbids it.</summary>
    internal static bool TryFileSymlink(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>An "outside" folder holding <paramref name="files"/> files (one of them read-only) and a subfolder.</summary>
    internal static string OutsideFolder(string parent, string name, int files = 20)
    {
        string root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        for (int i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(root, $"file-{i}.txt"), $"outside {i}\n");
        }

        File.WriteAllText(Path.Combine(root, "sub", "deep.txt"), "deep\n");
        File.SetAttributes(Path.Combine(root, "file-0.txt"), FileAttributes.ReadOnly);
        return root;
    }

    /// <summary>Real files under <paramref name="root"/>, counted WITHOUT walking into links.</summary>
    internal static int FileCount(string root)
    {
        int count = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (FileSystemInfo entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (LinkSafeTree.IsLink(entry)) continue;
                if (entry is DirectoryInfo d) pending.Push(d);
                else count++;
            }
        }

        return count;
    }

    /// <summary>Best-effort cleanup that itself never deletes through a link.</summary>
    internal static void Cleanup(string root)
    {
        try
        {
            SafeDelete.DeleteDirectory(root); // link-safe itself (#826), and clears the fixture's read-only file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort temp cleanup
        }
    }

    /// <summary>
    /// Hold the link at <paramref name="link"/> so it CANNOT be removed until disposed. Windows: an open handle
    /// on the link itself without <c>FILE_SHARE_DELETE</c> (RemoveDirectory then fails with a sharing
    /// violation). Unix: the link's parent directory made read-only (unlink then fails with EACCES) — which a
    /// root user ignores, so the caller skips under root. The Unix pin holds only against a walk that does NOT
    /// clear read-only (the git-rewrite disarm); <see cref="SafeDelete"/> clears read-only BY DESIGN (#109), which
    /// hands the owner back write permission on the parent and defeats it — see <see cref="PinSurvivesSafeDelete"/>.
    /// </summary>
    internal static IDisposable Pin(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint GenericRead = 0x80000000; // a FILE_READ_ATTRIBUTES-only handle is exempt from share checks
            const uint ShareReadWrite = 0x1 | 0x2; // deliberately NOT FILE_SHARE_DELETE
            const uint OpenExisting = 3;
            const uint BackupSemanticsAndOpenReparsePoint = 0x02000000 | 0x00200000;
            SafeFileHandle handle = CreateFileW(
                link, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);
            Assert.False(handle.IsInvalid, $"could not open a handle on {link}");
            return handle;
        }

        string parent = Path.GetDirectoryName(link)!;
        UnixFileMode before = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() =>
        {
            // Tolerate the parent being gone: if the pin was defeated and the tree deleted, the test's own assertion
            // must be what reports it, not a DirectoryNotFoundException from this cleanup.
            if (!OperatingSystem.IsWindows() && Directory.Exists(parent))
            {
                try { File.SetUnixFileMode(parent, before); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort restore */ }
            }
        });
    }

    /// <summary>True when the process runs as root, which ignores directory permissions.</summary>
    internal static bool IsUnixRoot => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    /// <summary>
    /// True where <see cref="Pin"/> can make a link unremovable even for <see cref="SafeDelete"/> — Windows only. On
    /// Unix an unlink needs write permission on the PARENT directory, which its (non-root) owner can always grant
    /// themselves; SafeDelete's read-only clear (#109) does exactly that, so an "unremovable link" cannot be modelled
    /// there without root-only tools (<c>chattr +i</c>) or a parent owned by another user, neither available on CI.
    /// </summary>
    internal static bool PinSurvivesSafeDelete => OperatingSystem.IsWindows();

    internal const string PinDefeatedBySafeDeleteReason =
        "an unremovable link cannot be modelled on Unix against SafeDelete: its read-only clear (#109) restores the owner's "
        + "write permission on the parent directory, which is all unlink needs (only chattr +i / a foreign-owned parent, "
        + "neither available on CI, would hold). The Windows handle pin covers the refusal path.";

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
