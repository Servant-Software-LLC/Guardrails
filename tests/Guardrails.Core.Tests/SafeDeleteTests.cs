using Guardrails.Core.Io;

namespace Guardrails.Core.Tests;

/// <summary>
/// Proves <see cref="SafeDelete.DeleteDirectory"/> removes a directory tree even when it contains
/// read-only files (issue #109). On Windows, git marks loose objects under <c>.git/objects</c>
/// read-only and a plain <see cref="Directory.Delete(string, bool)"/> throws
/// <see cref="UnauthorizedAccessException"/> — which is NOT an <see cref="IOException"/> — the
/// moment it reaches one. These tests are cross-platform: the read-only attribute is honoured on
/// every OS, so the read-only-clear sweep is exercised everywhere even though the Access-Denied
/// abort it prevents is Windows-specific.
/// </summary>
public sealed class SafeDeleteTests
{
    [Fact]
    public void DeleteDirectory_RemovesTree_WithReadOnlyFileAtAnyDepth()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-safedelete-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "objects", "ab");
        Directory.CreateDirectory(nested);

        string topFile = Path.Combine(root, "top.txt");
        string nestedFile = Path.Combine(nested, "loose-object");
        File.WriteAllText(topFile, "top");
        File.WriteAllText(nestedFile, "deadbeef");

        // Mark both files read-only, exactly as git does to loose/packed objects on Windows.
        File.SetAttributes(topFile, File.GetAttributes(topFile) | FileAttributes.ReadOnly);
        File.SetAttributes(nestedFile, File.GetAttributes(nestedFile) | FileAttributes.ReadOnly);

        SafeDelete.DeleteDirectory(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DeleteDirectory_PlainDeleteWouldThrow_OnReadOnlyFile()
    {
        // Pins the bug SafeDelete exists to fix: a vanilla Directory.Delete(recursive) over a tree
        // with a read-only file throws on Windows (UnauthorizedAccessException). On POSIX the
        // read-only attribute does not block deletion of the file given a writable parent dir, so
        // this expectation is asserted only where it holds; everywhere, SafeDelete must succeed.
        string root = Path.Combine(Path.GetTempPath(), "gr-safedelete-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "readonly.bin");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<UnauthorizedAccessException>(() => Directory.Delete(root, recursive: true));
            }

            // SafeDelete clears read-only first, so it always succeeds.
            SafeDelete.DeleteDirectory(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            // Defensive teardown if an assertion above left the tree behind.
            SafeDelete.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeleteDirectory_MissingPath_IsNoOp()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gr-safedelete-missing-" + Guid.NewGuid().ToString("N"));

        SafeDelete.DeleteDirectory(missing); // must not throw

        Assert.False(Directory.Exists(missing));
    }
}
