using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// Table-driven unit tests for the pure deep-merge (SSOT §6.3): objects recurse, scalars
/// AND arrays are last-writer-wins, overwrites of existing non-null values are reported as
/// conflicts, and a non-object fragment is the caller's concern (the merger only takes
/// objects).
/// </summary>
public sealed class JsonMergerTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void NewKeys_AreAdded_NoConflicts()
    {
        MergeResult result = JsonMerger.Merge(Obj("""{ "a": 1 }"""), Obj("""{ "b": 2 }"""));

        Assert.Equal("1", result.Merged["a"]!.ToJsonString());
        Assert.Equal("2", result.Merged["b"]!.ToJsonString());
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void NestedObjects_MergeRecursively()
    {
        MergeResult result = JsonMerger.Merge(
            Obj("""{ "outer": { "kept": 1 } }"""),
            Obj("""{ "outer": { "added": 2 } }"""));

        JsonObject outer = (JsonObject)result.Merged["outer"]!;
        Assert.Equal("1", outer["kept"]!.ToJsonString());
        Assert.Equal("2", outer["added"]!.ToJsonString());
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ScalarOverwrite_IsLastWriterWins_AndLogsConflict()
    {
        MergeResult result = JsonMerger.Merge(Obj("""{ "a": 1 }"""), Obj("""{ "a": 2 }"""));

        Assert.Equal("2", result.Merged["a"]!.ToJsonString());
        MergeConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal("a", conflict.JsonPath);
        Assert.Equal("1", conflict.OldValue);
        Assert.Equal("2", conflict.NewValue);
    }

    [Fact]
    public void ArrayOverwrite_IsLastWriterWins_NotConcatenated_AndLogsConflict()
    {
        MergeResult result = JsonMerger.Merge(
            Obj("""{ "items": [1, 2] }"""),
            Obj("""{ "items": [3] }"""));

        // Arrays are replaced wholesale, never merged element-wise.
        Assert.Equal("[3]", result.Merged["items"]!.ToJsonString());
        MergeConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal("items", conflict.JsonPath);
        Assert.Equal("[1,2]", conflict.OldValue);
        Assert.Equal("[3]", conflict.NewValue);
    }

    [Fact]
    public void NestedConflict_ReportsDottedPath()
    {
        MergeResult result = JsonMerger.Merge(
            Obj("""{ "a": { "b": { "c": "old" } } }"""),
            Obj("""{ "a": { "b": { "c": "new" } } }"""));

        MergeConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal("a.b.c", conflict.JsonPath);
        Assert.Equal("\"old\"", conflict.OldValue);
        Assert.Equal("\"new\"", conflict.NewValue);
    }

    [Fact]
    public void OverwritingExplicitNull_IsNotAConflict()
    {
        // Replacing a JSON null with a value is "filling a hole", not overwriting data.
        MergeResult result = JsonMerger.Merge(Obj("""{ "a": null }"""), Obj("""{ "a": 5 }"""));

        Assert.Equal("5", result.Merged["a"]!.ToJsonString());
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void SameValue_IsNotAConflict()
    {
        MergeResult result = JsonMerger.Merge(Obj("""{ "a": 1 }"""), Obj("""{ "a": 1 }"""));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ObjectReplacedByScalar_IsLastWriterWins_AndConflicts()
    {
        MergeResult result = JsonMerger.Merge(
            Obj("""{ "a": { "nested": 1 } }"""),
            Obj("""{ "a": "flat" }"""));

        Assert.Equal("\"flat\"", result.Merged["a"]!.ToJsonString());
        MergeConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal("a", conflict.JsonPath);
    }

    [Fact]
    public void MultipleConflicts_AreReportedInDocumentOrder()
    {
        MergeResult result = JsonMerger.Merge(
            Obj("""{ "a": 1, "b": 2 }"""),
            Obj("""{ "a": 10, "b": 20 }"""));

        Assert.Equal(2, result.Conflicts.Count);
        Assert.Equal("a", result.Conflicts[0].JsonPath);
        Assert.Equal("b", result.Conflicts[1].JsonPath);
    }

    [Fact]
    public void Merge_DoesNotMutateInputs()
    {
        JsonObject baseObj = Obj("""{ "a": 1 }""");
        JsonObject fragment = Obj("""{ "a": 2 }""");

        JsonMerger.Merge(baseObj, fragment);

        // Inputs untouched — the merge returned a fresh tree.
        Assert.Equal("1", baseObj["a"]!.ToJsonString());
        Assert.Equal("2", fragment["a"]!.ToJsonString());
    }

    [Fact]
    public void NullBase_IsTreatedAsEmptyObject()
    {
        MergeResult result = JsonMerger.Merge(baseObject: null, Obj("""{ "a": 1 }"""));
        Assert.Equal("1", result.Merged["a"]!.ToJsonString());
        Assert.Empty(result.Conflicts);
    }
}
