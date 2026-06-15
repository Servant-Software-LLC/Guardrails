using Guardrails.Core.Breakdown;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="BreakdownManifest"/> (SSOT §10) against real temp folders:
/// capture hashes authored files, applies the include/exclude rule (lock + diagram.md +
/// state runtime out, seed.json in), normalizes newlines, and round-trips through
/// <c>guardrails.lock</c>. <see cref="BreakdownManifest.Read"/> tolerates missing/garbage.
/// </summary>
public sealed class BreakdownManifestTests : IDisposable
{
    private readonly string _planDir;

    public BreakdownManifestTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    private void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(_planDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Capture_RecordsAuthoredFiles_WithLowercaseHex64Hashes()
    {
        WriteFile("guardrails.json", "{ \"version\": 1 }");
        WriteFile("tasks/01-a/task.json", "{ \"description\": \"x\" }");
        WriteFile("tasks/01-a/guardrails/01-build.ps1", "exit 0\n");

        BreakdownManifest manifest = BreakdownManifest.Capture(_planDir);

        Assert.Equal(
            new[] { "guardrails.json", "tasks/01-a/guardrails/01-build.ps1", "tasks/01-a/task.json" },
            manifest.Files.Keys.ToArray()); // ordinal-sorted, forward-slash
        Assert.All(manifest.Files.Values, h => Assert.Matches("^[0-9a-f]{64}$", h));
    }

    [Fact]
    public void Capture_ExcludesLockFile_DiagramAndStateRuntime_ButKeepsSeed()
    {
        WriteFile("guardrails.json", "{ \"version\": 1 }");
        WriteFile("tasks/01-a/task.json", "{ \"description\": \"x\" }");
        WriteFile(BreakdownManifest.FileName, "{ \"version\": 1, \"files\": {} }"); // self
        WriteFile("diagram.md", "<!-- generated -->");                             // generated artifact
        WriteFile("state/state.json", "{}");                                       // harness runtime
        WriteFile("state/run.json", "{}");                                         // harness runtime
        WriteFile("state/logs/01-a/attempt-1/action-stdout.log", "noise");         // harness runtime
        WriteFile("state/seed.json", "{ \"seeded\": true }");                      // authored, committed
        WriteFile("guardrails.json.tmp", "{ }");                                    // atomic-write residue

        BreakdownManifest manifest = BreakdownManifest.Capture(_planDir);

        Assert.Contains("guardrails.json", manifest.Files.Keys);
        Assert.Contains("tasks/01-a/task.json", manifest.Files.Keys);
        Assert.Contains("state/seed.json", manifest.Files.Keys);
        Assert.DoesNotContain(BreakdownManifest.FileName, manifest.Files.Keys);
        Assert.DoesNotContain("diagram.md", manifest.Files.Keys);
        Assert.DoesNotContain("state/state.json", manifest.Files.Keys);
        Assert.DoesNotContain("state/run.json", manifest.Files.Keys);
        Assert.DoesNotContain("state/logs/01-a/attempt-1/action-stdout.log", manifest.Files.Keys);
        Assert.DoesNotContain("guardrails.json.tmp", manifest.Files.Keys);
    }

    [Fact]
    public void Capture_NestedStateSeed_IsExcluded_OnlyTopLevelSeedKept()
    {
        // Only the top-level committed state/seed.json is authored; a seed.json nested deeper
        // under state/ is harness runtime, not authored content.
        WriteFile("state/seed.json", "{ \"seeded\": true }");
        WriteFile("state/logs/seed.json", "{ \"noise\": true }");

        BreakdownManifest manifest = BreakdownManifest.Capture(_planDir);

        Assert.Contains("state/seed.json", manifest.Files.Keys);
        Assert.DoesNotContain("state/logs/seed.json", manifest.Files.Keys);
    }

    [Fact]
    public void WriteThenWrite_OnUnchangedFolder_IsByteIdentical()
    {
        // The lock carries no timestamp, so re-locking an unchanged folder is byte-identical
        // (a deterministic projection, no git churn).
        WriteFile("guardrails.json", "{ \"version\": 1 }");
        WriteFile("tasks/01-a/task.json", "{ \"description\": \"x\" }");

        BreakdownManifest.Capture(_planDir).Write(_planDir);
        byte[] first = File.ReadAllBytes(BreakdownManifest.LockFilePath(_planDir));

        BreakdownManifest.Capture(_planDir).Write(_planDir);
        byte[] second = File.ReadAllBytes(BreakdownManifest.LockFilePath(_planDir));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Capture_NormalizesNewlines_CrlfAndLfHashEqual()
    {
        WriteFile("a/lf.txt", "line1\nline2\n");
        WriteFile("a/crlf.txt", "line1\r\nline2\r\n");

        BreakdownManifest manifest = BreakdownManifest.Capture(_planDir);

        Assert.Equal(manifest.Files["a/lf.txt"], manifest.Files["a/crlf.txt"]);
    }

    [Fact]
    public void Capture_HashChanges_WhenContentChanges()
    {
        WriteFile("tasks/01-a/guardrails/01-build.ps1", "exit 0\n");
        string before = BreakdownManifest.Capture(_planDir).Files["tasks/01-a/guardrails/01-build.ps1"];

        WriteFile("tasks/01-a/guardrails/01-build.ps1", "Test-Path a, b\nexit 0\n");
        string after = BreakdownManifest.Capture(_planDir).Files["tasks/01-a/guardrails/01-build.ps1"];

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Capture_MissingFolder_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => BreakdownManifest.Capture(missing));
    }

    [Fact]
    public void WriteThenRead_RoundTripsFiles()
    {
        WriteFile("guardrails.json", "{ \"version\": 1 }");
        WriteFile("tasks/01-a/task.json", "{ \"description\": \"x\" }");

        BreakdownManifest captured = BreakdownManifest.Capture(_planDir);
        captured.Write(_planDir);

        Assert.True(File.Exists(BreakdownManifest.LockFilePath(_planDir)));

        BreakdownManifest? read = BreakdownManifest.Read(_planDir);
        Assert.NotNull(read);
        Assert.Equal(captured.Files, read!.Files);
    }

    [Fact]
    public void Read_MissingLock_ReturnsNull()
    {
        Assert.Null(BreakdownManifest.Read(_planDir));
    }

    [Fact]
    public void Read_MalformedLock_ReturnsNull()
    {
        File.WriteAllText(BreakdownManifest.LockFilePath(_planDir), "{ not json");
        Assert.Null(BreakdownManifest.Read(_planDir));
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
            // Best-effort cleanup.
        }
    }
}
