using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Injects harness-computed action result fields (<c>actionExitCode</c>, <c>actionKind</c>)
/// into the task's state fragment after a successful action (issue #62). Like
/// <see cref="FileHashCapture"/>, the harness writes these values — the action agent never
/// can, so they cannot be spoofed. The harness value always wins if the action also wrote
/// under the same key.
///
/// <para>The injected shape is <c>{ "&lt;taskId&gt;": { "actionExitCode": N, "actionKind": "script"|"prompt" } }</c>,
/// merged into whatever the action already wrote in its fragment. Once merged into
/// <c>state.json</c>, downstream tasks see the exit code in their <c>GUARDRAILS_STATE_IN</c>
/// snapshot — the parallel-safe path. The current task's own guardrails can read the same
/// data via <c>GUARDRAILS_STATE_FRAGMENT</c>.</para>
///
/// <para>If the fragment exists but is malformed (not a JSON object), the bytes are left
/// untouched and the call is a no-op — the merge step rejects them identically regardless.</para>
/// </summary>
internal static class ActionResultCapture
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Merge <c>actionExitCode</c> and <c>actionKind</c> under <paramref name="taskId"/> into
    /// the fragment at <paramref name="fragmentOutPath"/> (creating it if absent). Harness values
    /// win when the action wrote the same keys. Silently no-ops when the existing fragment is
    /// malformed — the merge step handles that rejection.
    /// </summary>
    public static void Inject(string taskId, int exitCode, string actionKind, string fragmentOutPath)
    {
        // null means the fragment exists but is malformed — leave bytes intact, merge step rejects.
        JsonObject? root = ReadOrCreateFragmentObject(fragmentOutPath);
        if (root is null)
        {
            return;
        }

        if (root[taskId] is not JsonObject taskObject)
        {
            taskObject = new JsonObject();
            root[taskId] = taskObject;
        }

        // Harness values always win — an action cannot forge its own exit code.
        taskObject["actionExitCode"] = exitCode;
        taskObject["actionKind"] = actionKind;

        AtomicFile.WriteAllText(fragmentOutPath, root.ToJsonString(IndentedOptions));
    }

    /// <summary>
    /// Read the fragment as a mutable <see cref="JsonObject"/>, or return an empty object when
    /// the file is absent. Returns <see langword="null"/> when the file exists but is not a
    /// parseable JSON object — the caller treats that as a no-op and leaves bytes intact.
    /// </summary>
    private static JsonObject? ReadOrCreateFragmentObject(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(fragmentOutPath), documentOptions: ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return node as JsonObject; // null when the root is not an object
    }
}
