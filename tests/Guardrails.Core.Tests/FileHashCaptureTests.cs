using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests the harness-computed file-hash capture (issue #46): the hash is produced in code so the
/// action agent never shells out, and it is recorded into the task's state fragment so a
/// downstream tests-untouched guardrail can recompute (Get-FileHash) and compare.
/// </summary>
public sealed class FileHashCaptureTests : IDisposable
{
    private readonly string _root;

    public FileHashCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-hashcap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    [Fact]
    public void Capture_RecordsUppercaseSha256_MatchingGetFileHash()
    {
        // Get-FileHash -Algorithm SHA256 returns uppercase hex of the file's raw bytes; the
        // harness must produce the exact same string so the guardrail's recompute compares equal.
        string workspace = _root;
        string relative = "tests/CloudDestinationTests.cs";
        byte[] bytes = Encoding.UTF8.GetBytes("public class CloudDestinationTests { }\n");
        WriteWorkspaceFile(relative, bytes);

        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        CaptureResult result = FileHashCapture.Capture("10-author", [relative], workspace, fragmentPath);

        Assert.True(result.Succeeded);
        string expected = Convert.ToHexString(SHA256.HashData(bytes)); // uppercase hex, == Get-FileHash
        string recorded = ReadFragmentHash(fragmentPath, "10-author", relative);
        Assert.Equal(expected, recorded);
        Assert.Equal(64, recorded.Length);
        Assert.Matches("^[0-9A-F]{64}$", recorded);
    }

    [Fact]
    public void Capture_CreatesFragment_WhenActionWroteNothing()
    {
        string relative = "a.txt";
        WriteWorkspaceFile(relative, Encoding.UTF8.GetBytes("hello"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        Assert.False(File.Exists(fragmentPath));

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(fragmentPath));
        Assert.False(string.IsNullOrEmpty(ReadFragmentHash(fragmentPath, "01-x", relative)));
    }

    [Fact]
    public void Capture_PreservesExistingFragmentContent()
    {
        // The action may have published its own state under its own task id; capture must merge,
        // not clobber. The published keys are all under the OWN id '01-x' — under the
        // single-writer-per-key rule (SSOT §6.2, issue #48) a fragment may only carry the writing
        // task's own top-level key, so the action's own-namespace keys (and a sibling fileHashes
        // entry) are what capture must preserve.
        string relative = "a.txt";
        WriteWorkspaceFile(relative, Encoding.UTF8.GetBytes("hi"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        File.WriteAllText(fragmentPath,
            """
            { "01-x": { "agentKey": "agentValue", "fileHashes": { "other.txt": "CAFEBABE" } } }
            """);

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        JsonObject root = (JsonObject)JsonNode.Parse(File.ReadAllText(fragmentPath))!;
        // Pre-existing own-namespace keys survive.
        Assert.Equal("agentValue", (string?)root["01-x"]!["agentKey"]);
        // A pre-existing fileHashes entry the action wrote under its own id is preserved.
        Assert.Equal("CAFEBABE", (string?)root["01-x"]!["fileHashes"]!["other.txt"]);
        // New fileHashes added under the task id.
        Assert.False(string.IsNullOrEmpty((string?)root["01-x"]!["fileHashes"]![relative]));
    }

    [Fact]
    public void Capture_MissingFile_ReturnsFailure_AndDoesNotWriteFragment()
    {
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");

        CaptureResult result = FileHashCapture.Capture("01-x", ["does/not/exist.cs"], _root, fragmentPath);

        Assert.False(result.Succeeded);
        Assert.Equal("does/not/exist.cs", Assert.Single(result.MissingFiles));
        Assert.False(File.Exists(fragmentPath)); // nothing recorded when a declared file is missing
    }

    [Fact]
    public void Capture_MultipleFiles_AllRecorded()
    {
        WriteWorkspaceFile("one.cs", Encoding.UTF8.GetBytes("one"));
        WriteWorkspaceFile("sub/two.cs", Encoding.UTF8.GetBytes("two"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");

        CaptureResult result = FileHashCapture.Capture("t", ["one.cs", "sub/two.cs"], _root, fragmentPath);

        Assert.True(result.Succeeded);
        Assert.NotEqual(
            ReadFragmentHash(fragmentPath, "t", "one.cs"),
            ReadFragmentHash(fragmentPath, "t", "sub/two.cs"));
    }

    [Fact]
    public void Capture_EmptyFile_RecordsCanonicalSha256_MatchingGetFileHash()
    {
        // SHA-256 of zero bytes is a fixed constant; Get-FileHash -Algorithm SHA256 of an empty file
        // returns exactly this. The harness must record the same canonical value.
        const string canonicalEmptySha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
        string relative = "Empty.cs";
        WriteWorkspaceFile(relative, []);
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        Assert.Equal(canonicalEmptySha256, ReadFragmentHash(fragmentPath, "01-x", relative));
        // And it equals SHA256 over the actual (empty) bytes — the same thing Get-FileHash computes.
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), ReadFragmentHash(fragmentPath, "01-x", relative));
    }

    [Fact]
    public void Capture_PathWithSpaceAndNonAscii_CapturesCorrectly()
    {
        // A path containing a space and a non-ASCII character must resolve and hash correctly — the
        // relative key is preserved verbatim in the fragment.
        string relative = "tests/Café Tests/Wîdget Tests.cs";
        byte[] bytes = Encoding.UTF8.GetBytes("public class WidgetTests { } // café\n");
        WriteWorkspaceFile(relative, bytes);
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), ReadFragmentHash(fragmentPath, "01-x", relative));
    }

    [Fact]
    public void Capture_ActionWroteOwnFileHashesForSamePath_HarnessValueWins()
    {
        // W2 precedence (SSOT §3.1): if the action published its own fileHashes entry for a path the
        // harness also captures, the harness-computed value overwrites the action's.
        string relative = "a.txt";
        byte[] bytes = Encoding.UTF8.GetBytes("real content");
        WriteWorkspaceFile(relative, bytes);
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        File.WriteAllText(fragmentPath,
            """
            { "01-x": { "fileHashes": { "a.txt": "DEADBEEF", "other.txt": "CAFEBABE" } } }
            """);

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        // Harness value wins for the captured path...
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), ReadFragmentHash(fragmentPath, "01-x", relative));
        Assert.NotEqual("DEADBEEF", ReadFragmentHash(fragmentPath, "01-x", relative));
        // ...but an unrelated fileHashes entry the action wrote is preserved.
        Assert.Equal("CAFEBABE", ReadFragmentHash(fragmentPath, "01-x", "other.txt"));
    }

    [Fact]
    public void Capture_NonObjectFragment_LeavesBytesIntact_AndReportsInvalidFragment()
    {
        // FIX 1: a malformed (non-object) action fragment is the harness's to reject. Capture must NOT
        // overwrite it with a clean hashes-only object — the original bytes stay on disk so the merge
        // step rejects them identically, and the result signals invalid-fragment (not success).
        string relative = "a.txt";
        WriteWorkspaceFile(relative, Encoding.UTF8.GetBytes("hi"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        const string original = "[1, 2, 3]";
        File.WriteAllText(fragmentPath, original);

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.False(result.Succeeded);
        Assert.True(result.IsInvalidFragment);
        Assert.False(result.IsMissing);
        // The malformed bytes are untouched — capture did not write a hashes object over them.
        Assert.Equal(original, File.ReadAllText(fragmentPath));
    }

    [Fact]
    public void Capture_UnparseableFragment_LeavesBytesIntact_AndReportsInvalidFragment()
    {
        string relative = "a.txt";
        WriteWorkspaceFile(relative, Encoding.UTF8.GetBytes("hi"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        const string original = "{ not valid json ";
        File.WriteAllText(fragmentPath, original);

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.False(result.Succeeded);
        Assert.True(result.IsInvalidFragment);
        Assert.Equal(original, File.ReadAllText(fragmentPath));
    }

    private void WriteWorkspaceFile(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static string ReadFragmentHash(string fragmentPath, string taskId, string relative)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(File.ReadAllText(fragmentPath))!;
        return (string?)root[taskId]!["fileHashes"]![relative] ?? string.Empty;
    }
}
