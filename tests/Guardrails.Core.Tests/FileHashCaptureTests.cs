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
        // The action may have published its own state under the same task id; capture must merge,
        // not clobber.
        string relative = "a.txt";
        WriteWorkspaceFile(relative, Encoding.UTF8.GetBytes("hi"));
        string fragmentPath = Path.Combine(_root, "action-out-fragment.json");
        File.WriteAllText(fragmentPath,
            """
            { "01-x": { "agentKey": "agentValue" }, "other-task": { "k": 1 } }
            """);

        CaptureResult result = FileHashCapture.Capture("01-x", [relative], _root, fragmentPath);

        Assert.True(result.Succeeded);
        JsonObject root = (JsonObject)JsonNode.Parse(File.ReadAllText(fragmentPath))!;
        // Pre-existing keys survive.
        Assert.Equal("agentValue", (string?)root["01-x"]!["agentKey"]);
        Assert.Equal(1, (int)root["other-task"]!["k"]!);
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
