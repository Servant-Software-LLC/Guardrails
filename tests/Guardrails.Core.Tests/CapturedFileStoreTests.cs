using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests the captured-file baseline store (issue #51): the harness snapshots a test-author task's
/// declared files and restores them to that baseline before a downstream task retries, so an
/// implementation task that edits a test file can no longer dead-end every retry. The store is
/// byte-exact and resolves every path against the plan workspace (FIX B); a captured file it cannot
/// restore (missing baseline, or a containment skip) is surfaced, never swallowed (FIX D).
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

        RestoreOutcome outcome = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Equal(["tests/FooTests.cs"], outcome.Restored);
        Assert.Empty(outcome.Unrestorable);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_WhenUnchanged_IsNoOp()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL");
        store.Snapshot("01-author", ["tests/FooTests.cs"], _workspace);

        // Nothing modified the file (the first-attempt case).
        RestoreOutcome outcome = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Empty(outcome.Restored);
        Assert.Empty(outcome.Unrestorable);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_RecreatesDeletedFile()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "ORIGINAL");
        store.Snapshot("01-author", ["tests/FooTests.cs"], _workspace);

        File.Delete(Path.Combine(_workspace, "tests/FooTests.cs"));

        RestoreOutcome outcome = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Equal(["tests/FooTests.cs"], outcome.Restored);
        Assert.Equal("ORIGINAL", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_WithoutPriorSnapshot_DoesNothing_ToAnExistingFile()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("tests/FooTests.cs", "EDITED");

        // No Snapshot was ever taken for this id → nothing to restore against. The file exists, so it
        // is not reported as unrestorable — we simply have no baseline to compare against; leave it.
        RestoreOutcome outcome = store.Restore("01-author", ["tests/FooTests.cs"], _workspace);

        Assert.Empty(outcome.Restored);
        Assert.Empty(outcome.Unrestorable);
        Assert.Equal("EDITED", ReadWorkspaceFile("tests/FooTests.cs"));
    }

    [Fact]
    public void Restore_NoBaselineAndFileMissing_IsReportedUnrestorable()
    {
        // FIX D: a captured file with no baseline AND no workspace file cannot be made pristine — the
        // store surfaces it so the harness can log the gap rather than restore silently.
        var store = new CapturedFileStore(_planDir);

        RestoreOutcome outcome = store.Restore("01-author", ["tests/Gone.cs"], _workspace);

        Assert.Empty(outcome.Restored);
        UnrestorableFile entry = Assert.Single(outcome.Unrestorable);
        Assert.Equal("tests/Gone.cs", entry.RelativePath);
        Assert.Contains("missing", entry.Reason, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Roundtrip_ZeroByteFile_IsPreserved()
    {
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceBytes("empty.txt", []);
        store.Snapshot("01-author", ["empty.txt"], _workspace);

        WriteWorkspaceFile("empty.txt", "no longer empty");
        RestoreOutcome outcome = store.Restore("01-author", ["empty.txt"], _workspace);

        Assert.Equal(["empty.txt"], outcome.Restored);
        Assert.Empty(File.ReadAllBytes(Path.Combine(_workspace, "empty.txt")));
    }

    [Fact]
    public void Roundtrip_BinaryAndMixedLineEndings_AreByteFaithful()
    {
        // CRLF vs LF and raw binary must survive byte-for-byte — the store matches the raw-byte
        // SHA-256 capture, so any normalization here would desync the two.
        var store = new CapturedFileStore(_planDir);
        byte[] bytes = [0x00, 0x0D, 0x0A, 0xFF, (byte)'L', (byte)'F', 0x0A, 0xC3, 0xA9, 0x7F];
        WriteWorkspaceBytes("mixed.bin", bytes);
        store.Snapshot("01-author", ["mixed.bin"], _workspace);

        WriteWorkspaceBytes("mixed.bin", [0x01]);
        store.Restore("01-author", ["mixed.bin"], _workspace);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_workspace, "mixed.bin")));
    }

    [Fact]
    public void Roundtrip_PathWithSpaceAndNonAscii_Works()
    {
        var store = new CapturedFileStore(_planDir);
        const string rel = "src/Mödül Tests/Wîdget Tests.cs";
        WriteWorkspaceFile(rel, "ORIGINAL ünïcode");
        store.Snapshot("01-author", [rel], _workspace);

        WriteWorkspaceFile(rel, "CHEATED");
        RestoreOutcome outcome = store.Restore("01-author", [rel], _workspace);

        Assert.Equal([rel], outcome.Restored);
        Assert.Equal("ORIGINAL ünïcode", ReadWorkspaceFile(rel));
    }

    [Fact]
    public void Restore_MixedBatch_ReturnsExactlyDeletedAndModified()
    {
        // A single Restore batch over three files: one deleted, one modified, one unchanged. The
        // returned restored-set must be exactly {deleted, modified} — the unchanged file is a no-op.
        var store = new CapturedFileStore(_planDir);
        WriteWorkspaceFile("deleted.cs", "D");
        WriteWorkspaceFile("modified.cs", "M");
        WriteWorkspaceFile("unchanged.cs", "U");
        string[] all = ["deleted.cs", "modified.cs", "unchanged.cs"];
        store.Snapshot("01-author", all, _workspace);

        File.Delete(Path.Combine(_workspace, "deleted.cs"));
        WriteWorkspaceFile("modified.cs", "M-dirty");
        // unchanged.cs left alone.

        RestoreOutcome outcome = store.Restore("01-author", all, _workspace);

        Assert.Equal(["deleted.cs", "modified.cs"], outcome.Restored.OrderBy(p => p, StringComparer.Ordinal).ToList());
        Assert.Empty(outcome.Unrestorable);
        Assert.Equal("D", ReadWorkspaceFile("deleted.cs"));
        Assert.Equal("M", ReadWorkspaceFile("modified.cs"));
        Assert.Equal("U", ReadWorkspaceFile("unchanged.cs"));
    }

    [Fact]
    public void TwoAncestors_SameRelPath_DoNotCollide_EachKeyedUnderItsOwnAuthorId()
    {
        // Two ancestor tasks both capture the SAME workspace-relative path. Each baseline is keyed
        // under its own author id (state/captured/<authorId>/...), so they must not collide: restoring
        // from author B yields B's bytes, not A's. (The harness restores ancestors in order; this
        // proves the STORE keeps them distinct so order, not collision, decides the final bytes.)
        var store = new CapturedFileStore(_planDir);

        WriteWorkspaceFile("shared/Tests.cs", "FROM-A");
        store.Snapshot("01-author-a", ["shared/Tests.cs"], _workspace);

        WriteWorkspaceFile("shared/Tests.cs", "FROM-B");
        store.Snapshot("02-author-b", ["shared/Tests.cs"], _workspace);

        // Dirty it, then restore from each author independently.
        WriteWorkspaceFile("shared/Tests.cs", "DIRTY");
        store.Restore("01-author-a", ["shared/Tests.cs"], _workspace);
        Assert.Equal("FROM-A", ReadWorkspaceFile("shared/Tests.cs"));

        store.Restore("02-author-b", ["shared/Tests.cs"], _workspace);
        Assert.Equal("FROM-B", ReadWorkspaceFile("shared/Tests.cs"));
    }

    [Fact]
    public void Snapshot_EscapingPath_IsSkipped_AndRestoreReportsIt()
    {
        // FIX B defense-in-depth: an entry that resolves outside the workspace is never read or
        // written. Snapshot skips it (no baseline created), and Restore reports it as unrestorable
        // (escape) rather than touching a file outside the workspace. GR2013 should have rejected this
        // at validate time; the store guards anyway.
        var store = new CapturedFileStore(_planDir);
        const string escaping = "../outside/Secret.cs";

        // Create the would-be escape target so a buggy copy would have something to clobber.
        string outsideDir = Path.Combine(_planDir, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "Secret.cs"), "SENSITIVE");

        store.Snapshot("01-author", [escaping], _workspace);
        RestoreOutcome outcome = store.Restore("01-author", [escaping], _workspace);

        Assert.Empty(outcome.Restored);
        UnrestorableFile entry = Assert.Single(outcome.Unrestorable);
        Assert.Contains("escape", entry.Reason, StringComparison.OrdinalIgnoreCase);
        // The outside file was never touched.
        Assert.Equal("SENSITIVE", File.ReadAllText(Path.Combine(outsideDir, "Secret.cs")));
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
