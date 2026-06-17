using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests the captured-file baseline store (issue #51): the harness snapshots a test-author task's
/// declared files and restores them to that baseline before a downstream task retries, so an
/// implementation task that edits a test file can no longer dead-end every retry.
/// </summary>
public sealed class CapturedFileStoreTests : IDisposable
{
    private readonly string _planDir;
    private readonly string _workspace;

    public CapturedFileStoreTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-capstore-" + Guid.NewGuid().ToString("N"));
        // Workspace is a sibling of the plan dir, mirroring a real layout (workspace = "..").
        _workspace = Path.Combine(_planDir, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    [Fact]
    public void Restore_AfterModification_RestoresAuthoredBytes()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL");

        store.Snapshot("01-author", ["tests/FooTests.cs"], _workspace);

        // A downstream task edits the captured test file (the cheat tests-untouched catches).
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL + CHEAT");

        IReadOnlyList<string> restored = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Equal(["tests/FooTests.cs"], restored);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_WhenUnchanged_IsNoOp()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL");
        store.Snapshot("01-author", ["tests/FooTests.cs"], _workspace);

        // Nothing modified the file (the first-attempt case).
        IReadOnlyList<string> restored = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Empty(restored);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_RecreatesDeletedFile()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL");
        store.Snapshot("01-author", ["tests/FooTests.cs"], _workspace);

        File.Delete(Path.Combine(_workspace, "tests/FooTests.cs"));

        IReadOnlyList<string> restored = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Equal(["tests/FooTests.cs"], restored);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_WithoutPriorSnapshot_DoesNothing()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "EDITED");

        // No Snapshot was ever taken for this id → nothing to restore against; leave the file alone.
        IReadOnlyList<string> restored = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Empty(restored);
        Assert.Equal("EDITED", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Snapshot_PreservesExactBytes_AcrossNestedPaths()
    {
        var store = new CapturedFileStore(_planDir);
        byte[] bytes = Encoding.UTF8.GetBytes("line1\nline2\twith tab\n");
        WriteWorkspaceBytes("a/b/c/DeepTests.cs", bytes);
        store.Snapshot("01-author", ["a/b/c/DeepTests.cs"], _workspace);

        WriteWorkspaceFile("a/b/c/DeepTests.cs", "clobbered");
        store.Restore("01-author", ["a/b/c/DeepTests.cs"], _workspace);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_workspace, "a/b/c/DeepTests.cs")));
    }

    private void WriteWorkspaceFile(string relative, string content) =>
        WriteWorkspaceBytes(relative, Encoding.UTF8.GetBytes(content));

    private void WriteWorkspaceBytes(string relative, byte[] bytes)
    {
        string full = Path.Combine(_workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private string ReadWorkspaceFile(string relative) =>
        File.ReadAllText(Path.Combine(_workspace, relative));
}
