using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="StateManager"/> against real temp directories: init from
/// seed/empty, immutable snapshots, fragment merge with conflict logging, and the
/// invalid-fragment rejection paths (SSOT §6).
/// </summary>
public sealed class StateManagerTests : IDisposable
{
    private readonly string _planDir;
    private readonly string _stateDir;

    public StateManagerTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-state-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_planDir, "state");
        Directory.CreateDirectory(_stateDir);
    }

    private StateManager NewManager() => new(_planDir);

    private void WriteSeed(string json) => File.WriteAllText(Path.Combine(_stateDir, "seed.json"), json);

    private string StatePath => Path.Combine(_stateDir, "state.json");

    [Fact]
    public void Initialize_NoSeed_CreatesEmptyObject()
    {
        NewManager().Initialize();

        Assert.True(File.Exists(StatePath));
        Assert.Equal("{}", File.ReadAllText(StatePath).Trim());
    }

    [Fact]
    public void Initialize_WithSeed_CopiesSeedContent()
    {
        WriteSeed("""{ "recipientName": "World" }""");
        NewManager().Initialize();

        JsonObject state = NewManager().ReadState();
        Assert.Equal("\"World\"", state["recipientName"]!.ToJsonString());
    }

    [Fact]
    public void Initialize_IsIdempotent_DoesNotOverwriteExistingState()
    {
        File.WriteAllText(StatePath, """{ "accumulated": true }""");
        WriteSeed("""{ "recipientName": "World" }""");

        NewManager().Initialize();

        // Existing state survives — resume must not clobber accumulated state.
        JsonObject state = NewManager().ReadState();
        Assert.True(state.ContainsKey("accumulated"));
        Assert.False(state.ContainsKey("recipientName"));
    }

    [Fact]
    public void CreateSnapshot_IsACopy_ImmuneToLaterMerges()
    {
        WriteSeed("""{ "value": 1 }""");
        StateManager manager = NewManager();
        manager.Initialize();

        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        string snapshotPath = manager.CreateSnapshot(attemptDir);

        // Merge a new value AFTER snapshotting.
        string fragment = WriteFragment("""{ "value": 2 }""");
        manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        // The snapshot still reflects the pre-merge world.
        JsonObject snapshot = (JsonObject)JsonNode.Parse(File.ReadAllText(snapshotPath))!;
        Assert.Equal("1", snapshot["value"]!.ToJsonString());
        // Live state advanced.
        Assert.Equal("2", manager.ReadState()["value"]!.ToJsonString());
    }

    [Fact]
    public void MergeFragment_ValidObject_MergesAndCopiesFragment()
    {
        StateManager manager = NewManager();
        manager.Initialize();
        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        string fragment = WriteFragment("""{ "t": { "out": "value" } }""");

        MergeFragmentResult result = manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        Assert.True(result.Merged);
        JsonObject state = manager.ReadState();
        Assert.Equal("\"value\"", ((JsonObject)state["t"]!)["out"]!.ToJsonString());
        // Audit copy lands in the attempt dir.
        Assert.True(File.Exists(Path.Combine(attemptDir, "fragment.json")));
    }

    [Fact]
    public void MergeFragment_Overwrite_AppendsConflictLog()
    {
        File.WriteAllText(StatePath, """{ "shared": "old" }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "t2", "attempt-1");
        string fragment = WriteFragment("""{ "shared": "new" }""");

        manager.MergeFragment("t2", fragment, mergeSequence: 7, attemptDir);

        string log = File.ReadAllText(Path.Combine(_stateDir, "merge-conflicts.log"));
        string[] columns = log.Trim().Split('\t');
        Assert.Equal("7", columns[0]);          // seq
        Assert.Equal("t2", columns[1]);         // task
        Assert.Equal("shared", columns[2]);     // jsonPath
        Assert.Equal("\"old\"", columns[3]);    // old
        Assert.Equal("\"new\"", columns[4]);    // new
    }

    [Fact]
    public void MergeFragment_NoConflict_WritesNoConflictLog()
    {
        StateManager manager = NewManager();
        manager.Initialize();
        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        string fragment = WriteFragment("""{ "fresh": 1 }""");

        manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        Assert.False(File.Exists(Path.Combine(_stateDir, "merge-conflicts.log")));
    }

    [Fact]
    public void MergeFragment_NotJson_IsRejected_StateUnchanged()
    {
        File.WriteAllText(StatePath, """{ "keep": 1 }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        Directory.CreateDirectory(attemptDir);
        string fragment = WriteFragment("this is not json {");

        MergeFragmentResult result = manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.NotJson, result.Rejection);
        Assert.Equal("""{ "keep": 1 }""", File.ReadAllText(StatePath));
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void MergeFragment_NotAnObject_IsRejected_StateUnchanged(string fragmentJson)
    {
        File.WriteAllText(StatePath, """{ "keep": 1 }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        Directory.CreateDirectory(attemptDir);
        string fragment = WriteFragment(fragmentJson);

        MergeFragmentResult result = manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.NotAnObject, result.Rejection);
        Assert.Equal("""{ "keep": 1 }""", File.ReadAllText(StatePath));
    }

    [Fact]
    public void MergeFragment_AtomicWrite_ProducesValidJsonNoTempLeftBehind()
    {
        StateManager manager = NewManager();
        manager.Initialize();
        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        string fragment = WriteFragment("""{ "t": { "a": 1 } }""");

        manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        // Final state parses cleanly and no .tmp sidecar remains in the state dir.
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(StatePath)));
        Assert.Empty(Directory.EnumerateFiles(_stateDir, "*.tmp"));
    }

    private string WriteFragment(string content)
    {
        string path = Path.Combine(_planDir, "fragment-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
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
            // best-effort cleanup
        }
    }
}
