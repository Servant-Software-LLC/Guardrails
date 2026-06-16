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
        // The written value is namespaced under the task's own id 't' so the merge satisfies the
        // single-writer-per-key rule (SSOT §6.2, issue #48); this test is about snapshot immutability,
        // not the rule.
        WriteSeed("""{ "t": { "value": 1 } }""");
        StateManager manager = NewManager();
        manager.Initialize();

        string attemptDir = Path.Combine(_stateDir, "logs", "t", "attempt-1");
        string snapshotPath = manager.CreateSnapshot(attemptDir);

        // Merge a new value AFTER snapshotting.
        string fragment = WriteFragment("""{ "t": { "value": 2 } }""");
        manager.MergeFragment("t", fragment, mergeSequence: 1, attemptDir);

        // The snapshot still reflects the pre-merge world.
        JsonObject snapshot = (JsonObject)JsonNode.Parse(File.ReadAllText(snapshotPath))!;
        Assert.Equal("1", ((JsonObject)snapshot["t"]!)["value"]!.ToJsonString());
        // Live state advanced.
        Assert.Equal("2", ((JsonObject)manager.ReadState()["t"]!)["value"]!.ToJsonString());
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
        // SSOT §6.3 conflict-log COLUMN FORMAT coverage. Under the single-writer-per-key rule
        // (SSOT §6.2, issue #48) root-level cross-task conflicts are impossible, so the conflict
        // is now WITHIN the task's own namespace: base state has { "t2": { "shared": "old" } } and
        // task t2 overwrites it with { "t2": { "shared": "new" } } — last-writer-wins still fires
        // and the conflict row for the nested path `t2.shared` must keep the exact tab-separated
        // column format `seq, task, jsonPath, old, new`.
        File.WriteAllText(StatePath, """{ "t2": { "shared": "old" } }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "t2", "attempt-1");
        string fragment = WriteFragment("""{ "t2": { "shared": "new" } }""");

        manager.MergeFragment("t2", fragment, mergeSequence: 7, attemptDir);

        string log = File.ReadAllText(Path.Combine(_stateDir, "merge-conflicts.log"));
        string[] columns = log.Trim().Split('\t');
        Assert.Equal("7", columns[0]);            // seq
        Assert.Equal("t2", columns[1]);           // task
        Assert.Equal("t2.shared", columns[2]);    // jsonPath (nested under the task's own namespace)
        Assert.Equal("\"old\"", columns[3]);      // old
        Assert.Equal("\"new\"", columns[4]);      // new
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
    public void MergeFragment_ForeignTaskIdKey_IsRejected_StateUnchanged_NamesKey()
    {
        // The #48 attack: task '02-x' writes a fragment keyed under ANOTHER task's id ('01-producer').
        // The single-writer-per-key rule (SSOT §6.2) rejects it — nothing is merged, state is unchanged,
        // and the reason names the offending key so the retry feedback is actionable.
        File.WriteAllText(StatePath, """{ "keep": 1 }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "02-x", "attempt-1");
        Directory.CreateDirectory(attemptDir);
        string fragment = WriteFragment("""{ "01-producer": { "fileHashes": { "Tests.cs": "DEADBEEF" } } }""");

        MergeFragmentResult result = manager.MergeFragment("02-x", fragment, mergeSequence: 1, attemptDir);

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.ForeignKey, result.Rejection);
        Assert.Equal("01-producer", Assert.Single(result.ForeignKeys));
        Assert.Contains("01-producer", result.Reason);
        Assert.Equal("""{ "keep": 1 }""", File.ReadAllText(StatePath));
    }

    [Fact]
    public void MergeFragment_ArbitrarySharedKey_IsRejected_StateUnchanged()
    {
        // A non-task shared top-level key ('config') is also rejected — only the task's own id (or a
        // harness reserved key, none in v1) is permitted at the root.
        File.WriteAllText(StatePath, """{ "keep": 1 }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "01-x", "attempt-1");
        Directory.CreateDirectory(attemptDir);
        string fragment = WriteFragment("""{ "config": { "shared": true } }""");

        MergeFragmentResult result = manager.MergeFragment("01-x", fragment, mergeSequence: 1, attemptDir);

        Assert.False(result.Merged);
        Assert.Equal(FragmentRejection.ForeignKey, result.Rejection);
        Assert.Equal("config", Assert.Single(result.ForeignKeys));
        Assert.Equal("""{ "keep": 1 }""", File.ReadAllText(StatePath));
    }

    [Fact]
    public void MergeFragment_OwnIdKey_MergesFine()
    {
        // Regression guard: a fragment keyed under the task's OWN id is the happy path and merges.
        StateManager manager = NewManager();
        manager.Initialize();
        string attemptDir = Path.Combine(_stateDir, "logs", "01-author", "attempt-1");
        string fragment = WriteFragment("""{ "01-author": { "greeting": "hello" } }""");

        MergeFragmentResult result = manager.MergeFragment("01-author", fragment, mergeSequence: 1, attemptDir);

        Assert.True(result.Merged);
        Assert.Equal("\"hello\"", ((JsonObject)manager.ReadState()["01-author"]!)["greeting"]!.ToJsonString());
    }

    [Fact]
    public void MergeFragment_CaptureOverlaidFragment_OwnIdWithFileHashes_PassesRuleAndMerges()
    {
        // The exact shape FileHashCapture produces (SSOT §3.1, issue #47): own id at the root with a
        // fileHashes object plus any keys the action published under the same id. Capture overlays
        // under task.Id BEFORE merge, so the only top-level key is the own id — the single-writer rule
        // (SSOT §6.2, issue #48) passes and the fragment merges. Proves the #47 capture path still works.
        StateManager manager = NewManager();
        manager.Initialize();
        string attemptDir = Path.Combine(_stateDir, "logs", "01-author", "attempt-1");
        string fragment = WriteFragment(
            """{ "01-author": { "agentKey": "v", "fileHashes": { "Tests.cs": "ABC123" } } }""");

        MergeFragmentResult result = manager.MergeFragment("01-author", fragment, mergeSequence: 1, attemptDir);

        Assert.True(result.Merged);
        JsonObject author = (JsonObject)manager.ReadState()["01-author"]!;
        Assert.Equal("\"v\"", author["agentKey"]!.ToJsonString());
        Assert.Equal("\"ABC123\"", ((JsonObject)author["fileHashes"]!)["Tests.cs"]!.ToJsonString());
    }

    [Fact]
    public void MergeFragment_EmptyObject_PassesVacuously_MergesNothing()
    {
        // An empty fragment has no top-level keys, so it passes the rule vacuously and merges nothing
        // (documented as intentionally allowed — out of scope to guard against).
        File.WriteAllText(StatePath, """{ "keep": 1 }""");
        StateManager manager = NewManager();
        string attemptDir = Path.Combine(_stateDir, "logs", "01-x", "attempt-1");
        Directory.CreateDirectory(attemptDir);
        string fragment = WriteFragment("{}");

        MergeFragmentResult result = manager.MergeFragment("01-x", fragment, mergeSequence: 1, attemptDir);

        Assert.True(result.Merged);
        Assert.Empty(result.Conflicts);
        Assert.True(manager.ReadState().ContainsKey("keep"));
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
