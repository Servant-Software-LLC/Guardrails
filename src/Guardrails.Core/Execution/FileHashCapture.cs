using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Records SHA-256 content hashes of a task's declared <c>captureHashes</c> files into its state
/// fragment after a successful action (SSOT §3.1 / issue #46). The hash is computed HERE, in
/// harness code — the action agent never runs <c>git hash-object</c> or any shell command, so a
/// scoped <c>allowedTools</c> can never block the capture. A downstream <c>tests-untouched</c>
/// guardrail recomputes the same hash with <c>Get-FileHash -Algorithm SHA256</c> (a pwsh cmdlet
/// run by the interpreter, not the agent sandbox) and compares.
///
/// <para>SHA-256 over the file's RAW BYTES is deliberate: it is computed identically by
/// <see cref="SHA256"/> here and by <c>Get-FileHash</c> in a guardrail (no git dependency), it is
/// exactly the "file untouched" semantics the guardrail wants, and it sidesteps the
/// git-blob normalization hazard (no shared git index is mutated, so it is parallel-safe). It is an
/// EXACT raw-byte match, however: if a line-ending normalization touches the file between capture
/// and the downstream recompute (git autocrlf on checkout, or an IDE/formatter that rewrites the
/// file), the comparison FAILS CLOSED — a spurious "tests changed" block that a human then reviews.
/// Safe, but possible; it does not silently pass.</para>
/// </summary>
internal static class FileHashCapture
{
    /// <summary>
    /// Compute SHA-256 for each declared file (resolved against <paramref name="workspace"/>) and
    /// merge <c>{ "&lt;taskId&gt;": { "fileHashes": { "&lt;relPath&gt;": "&lt;hex&gt;" } } }</c> into
    /// the fragment at <paramref name="fragmentOutPath"/> (created if the action wrote nothing). The
    /// merge OVERLAYS onto the action's own fragment, preserving every other key the action wrote;
    /// on the <c>fileHashes</c> key the harness value takes precedence (SSOT §3.1).
    ///
    /// <para>If the action's fragment exists but is NOT a JSON object (or will not parse), capture
    /// does NOT overwrite it — the original bytes are left intact and <see cref="CaptureResult.InvalidFragment"/>
    /// is returned, so the caller routes to the same <c>invalid-fragment</c> attempt failure it would
    /// reach without <c>captureHashes</c> declared (the merge step re-reads and rejects the same bytes).
    /// Capture must never paper over a malformed fragment the harness is contractually required to reject.</para>
    ///
    /// <para>Returns <see cref="CaptureResult.Missing"/> when a declared file is absent; in that case
    /// nothing is written.</para>
    /// </summary>
    public static CaptureResult Capture(
        string taskId,
        IReadOnlyList<string> relativePaths,
        string workspace,
        string fragmentOutPath)
    {
        // Pre-check the action's fragment shape BEFORE hashing/overwriting. A fragment that exists
        // but is not a JSON object is the harness's responsibility to reject — capturing into a
        // clean object would silently destroy bytes that the merge step is required to fail on.
        if (TryReadFragmentObject(fragmentOutPath, out JsonObject root) is { } rejection)
        {
            return CaptureResult.Invalid(rejection);
        }

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

        MergeIntoFragment(taskId, hashes, root, fragmentOutPath);
        return CaptureResult.Ok();
    }

    private static void MergeIntoFragment(
        string taskId,
        IReadOnlyDictionary<string, string> hashes,
        JsonObject root,
        string fragmentOutPath)
    {
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

        // Harness value wins for a captured path the action also wrote (SSOT §3.1 precedence).
        foreach (KeyValuePair<string, string> entry in hashes)
        {
            fileHashes[entry.Key] = entry.Value;
        }

        AtomicFile.WriteAllText(fragmentOutPath, root.ToJsonString(IndentedOptions));
    }

    /// <summary>
    /// Read the action's fragment into a mutable <see cref="JsonObject"/> to overlay hashes onto.
    /// Returns <c>null</c> on success (absent file ⇒ empty object); returns the
    /// <see cref="FragmentRejection"/> reason WITHOUT touching the file when the fragment exists but
    /// is not a JSON object or will not parse, so the caller can fail the attempt as invalid-fragment
    /// while the original (malformed) bytes stay on disk for the merge step to reject identically.
    /// </summary>
    private static FragmentRejection? TryReadFragmentObject(string fragmentOutPath, out JsonObject root)
    {
        root = new JsonObject();

        if (!File.Exists(fragmentOutPath))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(fragmentOutPath), documentOptions: ParseOptions);
        }
        catch (JsonException)
        {
            return FragmentRejection.NotJson;
        }

        if (node is not JsonObject obj)
        {
            return FragmentRejection.NotAnObject;
        }

        root = obj;
        return null;
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

    /// <summary>Declared files that did not exist after the action (empty unless <see cref="IsMissing"/>).</summary>
    public IReadOnlyList<string> MissingFiles { get; private init; } = [];

    /// <summary>
    /// Set when the action's pending fragment is not a JSON object (or unparseable). Capture left the
    /// bytes intact; the caller maps this to the <c>invalid-fragment</c> attempt outcome (SSOT §3.1 /
    /// §6.2) exactly as it would reach without <c>captureHashes</c> declared.
    /// </summary>
    public FragmentRejection? Rejection { get; private init; }

    /// <summary>True when a declared file was missing after the action.</summary>
    public bool IsMissing => MissingFiles.Count > 0;

    /// <summary>True when the action's pending fragment was malformed (not a JSON object).</summary>
    public bool IsInvalidFragment => Rejection is not null;

    public static CaptureResult Ok() => new() { Succeeded = true };

    public static CaptureResult Missing(IReadOnlyList<string> missing) =>
        new() { Succeeded = false, MissingFiles = missing };

    public static CaptureResult Invalid(FragmentRejection rejection) =>
        new() { Succeeded = false, Rejection = rejection };
}
