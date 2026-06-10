using System.Text.Json;
using System.Text.Json.Nodes;

namespace Guardrails.Core.State;

/// <summary>
/// A single overwrite recorded during a deep merge: an existing non-null value at
/// <see cref="JsonPath"/> was replaced by a new value (SSOT §6.3 conflict log row,
/// minus the <c>seq</c>/<c>task</c> columns the caller owns).
/// </summary>
public sealed record MergeConflict
{
    /// <summary>Dotted/indexed JSON path of the overwritten value (e.g. <c>a.b[0]</c>).</summary>
    public required string JsonPath { get; init; }

    /// <summary>The pre-existing value's compact JSON serialization.</summary>
    public required string OldValue { get; init; }

    /// <summary>The replacement value's compact JSON serialization.</summary>
    public required string NewValue { get; init; }
}

/// <summary>The result of a deep merge: the merged tree plus every overwrite it caused.</summary>
public sealed record MergeResult
{
    /// <summary>The merged JSON object.</summary>
    public required JsonObject Merged { get; init; }

    /// <summary>Overwrites of pre-existing non-null values, in document order.</summary>
    public required IReadOnlyList<MergeConflict> Conflicts { get; init; }
}

/// <summary>
/// Pure, deterministic deep-merge of one JSON object (the fragment) into another (the
/// base), per SSOT §6.3: objects merge recursively; scalars AND arrays are
/// last-writer-wins. Every overwrite of an existing non-null value is reported as a
/// <see cref="MergeConflict"/> (the caller stamps <c>seq</c>/<c>task</c> and persists it).
///
/// The merger is side-effect-free — it returns a fresh merged tree and never mutates
/// its inputs — so it is trivially unit-testable and safe to call from concurrent code.
/// </summary>
public static class JsonMerger
{
    /// <summary>
    /// Deep-merge <paramref name="fragment"/> into <paramref name="baseObject"/>. Neither
    /// input is mutated. A null base is treated as an empty object.
    /// </summary>
    public static MergeResult Merge(JsonObject? baseObject, JsonObject fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        JsonObject merged = baseObject is null
            ? new JsonObject()
            : (JsonObject)baseObject.DeepClone();

        var conflicts = new List<MergeConflict>();
        MergeInto(merged, fragment, path: string.Empty, conflicts);
        return new MergeResult { Merged = merged, Conflicts = conflicts };
    }

    private static void MergeInto(JsonObject target, JsonObject source, string path, List<MergeConflict> conflicts)
    {
        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            string childPath = Append(path, entry.Key);
            JsonNode? incoming = entry.Value;

            if (target.TryGetPropertyValue(entry.Key, out JsonNode? existing))
            {
                // Objects on both sides recurse; everything else is last-writer-wins.
                if (existing is JsonObject existingObject && incoming is JsonObject incomingObject)
                {
                    MergeInto(existingObject, incomingObject, childPath, conflicts);
                    continue;
                }

                // Overwriting an existing non-null scalar/array/object with a new value is a
                // conflict worth logging. Overwriting a JSON null is not (nothing was there).
                if (existing is not null && !ValuesEqual(existing, incoming))
                {
                    conflicts.Add(new MergeConflict
                    {
                        JsonPath = childPath,
                        OldValue = ToCompactJson(existing),
                        NewValue = ToCompactJson(incoming)
                    });
                }

                target[entry.Key] = CloneOrNull(incoming);
            }
            else
            {
                target[entry.Key] = CloneOrNull(incoming);
            }
        }
    }

    private static JsonNode? CloneOrNull(JsonNode? node) => node?.DeepClone();

    private static string Append(string path, string key) =>
        path.Length == 0 ? key : $"{path}.{key}";

    private static bool ValuesEqual(JsonNode? left, JsonNode? right) =>
        ToCompactJson(left) == ToCompactJson(right);

    private static string ToCompactJson(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(CompactOptions);

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };
}
