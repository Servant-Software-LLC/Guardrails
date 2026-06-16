using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Records SHA-256 content hashes of a task's declared <c>captureHashes</c> files into its state
/// fragment after a successful action (SSOT §3 / issue #46). The hash is computed HERE, in
/// harness code — the action agent never runs <c>git hash-object</c> or any shell command, so a
/// scoped <c>allowedTools</c> can never block the capture. A downstream <c>tests-untouched</c>
/// guardrail recomputes the same hash with <c>Get-FileHash -Algorithm SHA256</c> (a pwsh cmdlet
/// run by the interpreter, not the agent sandbox) and compares.
///
/// <para>SHA-256 over the file's RAW BYTES is deliberate: it is computed identically by
/// <see cref="SHA256"/> here and by <c>Get-FileHash</c> in a guardrail (no git dependency), it is
/// exactly the "file untouched" semantics the guardrail wants, and it sidesteps the
/// CRLF-normalization hazards that bite git-blob hashing on Windows checkouts. Parallel-safe: no
/// shared git index is mutated.</para>
/// </summary>
internal static class FileHashCapture
{
    /// <summary>
    /// Compute SHA-256 for each declared file (resolved against <paramref name="workspace"/>) and
    /// merge <c>{ "&lt;taskId&gt;": { "fileHashes": { "&lt;relPath&gt;": "&lt;hex&gt;" } } }</c> into
    /// the fragment at <paramref name="fragmentOutPath"/> (created if the action wrote nothing). The
    /// merge preserves whatever the action already published. Returns the missing files (if any);
    /// when non-empty the caller fails the attempt and nothing is written.
    /// </summary>
    public static CaptureResult Capture(
        string taskId,
        IReadOnlyList<string> relativePaths,
        string workspace,
        string fragmentOutPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (string relativePath in relativePaths)
        {
            string fullPath = Path.GetFullPath(Path.Combine(workspace, relativePath));
            if (!File.Exists(fullPath))
            {
                missing.Add(relativePath);
                continue;
            }

            hashes[relativePath] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        }

        if (missing.Count > 0)
        {
            // The action reported success but did not produce a declared file — fail the attempt
            // with the precise list rather than recording a partial/empty hash set.
            return CaptureResult.Missing(missing);
        }

        MergeIntoFragment(taskId, hashes, fragmentOutPath);
        return CaptureResult.Ok();
    }

    private static void MergeIntoFragment(string taskId, IReadOnlyDictionary<string, string> hashes, string fragmentOutPath)
    {
        JsonObject root = ReadFragmentObject(fragmentOutPath);

        // Ensure root["<taskId>"]["fileHashes"] exists, preserving anything the action wrote there.
        if (root[taskId] is not JsonObject taskObject)
        {
            taskObject = new JsonObject();
            root[taskId] = taskObject;
        }

        if (taskObject["fileHashes"] is not JsonObject fileHashes)
        {
            fileHashes = new JsonObject();
            taskObject["fileHashes"] = fileHashes;
        }

        foreach (KeyValuePair<string, string> entry in hashes)
        {
            fileHashes[entry.Key] = entry.Value;
        }

        AtomicFile.WriteAllText(fragmentOutPath, root.ToJsonString(IndentedOptions));
    }

    private static JsonObject ReadFragmentObject(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return new JsonObject();
        }

        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(fragmentOutPath), documentOptions: ParseOptions);
            // If the action wrote a non-object fragment, MergeFragment would later reject it anyway;
            // start clean so the hashes are at least well-formed (the merge step reports the conflict).
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private static JsonDocumentOptions ParseOptions => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>The outcome of a <see cref="FileHashCapture.Capture"/> call.</summary>
internal sealed record CaptureResult
{
    public bool Succeeded { get; private init; }

    /// <summary>Declared files that did not exist after the action (empty on success).</summary>
    public IReadOnlyList<string> MissingFiles { get; private init; } = [];

    public static CaptureResult Ok() => new() { Succeeded = true };

    public static CaptureResult Missing(IReadOnlyList<string> missing) =>
        new() { Succeeded = false, MissingFiles = missing };
}
