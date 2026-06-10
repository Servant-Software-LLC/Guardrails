using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Guardrails.Core.State;

/// <summary>
/// Why a fragment could not be merged into <c>state.json</c>. SSOT §6.2: a fragment that
/// exists but is not a parseable JSON object fails the attempt with "invalid state fragment".
/// </summary>
public enum FragmentRejection
{
    /// <summary>The fragment file was not valid JSON.</summary>
    NotJson,

    /// <summary>The fragment parsed but its top-level value is not a JSON object.</summary>
    NotAnObject
}

/// <summary>The outcome of attempting to merge a fragment into state.</summary>
public sealed record MergeFragmentResult
{
    /// <summary>True when the fragment was a valid object and was merged.</summary>
    public bool Merged { get; init; }

    /// <summary>When <see cref="Merged"/> is false, why the fragment was rejected.</summary>
    public FragmentRejection? Rejection { get; init; }

    /// <summary>When <see cref="Merged"/> is false, a one-line actionable reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Overwrites of pre-existing non-null values caused by this merge (empty on rejection).</summary>
    public IReadOnlyList<MergeConflict> Conflicts { get; init; } = [];

    internal static MergeFragmentResult Ok(IReadOnlyList<MergeConflict> conflicts) =>
        new() { Merged = true, Conflicts = conflicts };

    internal static MergeFragmentResult Reject(FragmentRejection rejection, string reason) =>
        new() { Merged = false, Rejection = rejection, Reason = reason };
}

/// <summary>
/// The single writer of <c>state/state.json</c> (SSOT §6). Owns initialization from
/// <c>seed.json</c>, per-attempt snapshots (<c>state-in.json</c>), and the deep-merge of
/// action fragments after guardrails pass. Every state write is atomic; snapshots are
/// COPIES (never live handles) so a future concurrent merge cannot mutate an attempt's
/// immutable view of the world.
///
/// In M3 execution is serial, but the class is written as if concurrent task completions
/// exist: <see cref="MergeFragment"/> guards state mutation with a private lock and treats
/// the merge sequence as caller-supplied (the journal is the authority on the counter), so
/// the M4 scheduler can call it from multiple worker loops without a redesign.
/// </summary>
public sealed class StateManager
{
    private const string StateFileName = "state.json";
    private const string SeedFileName = "seed.json";
    private const string ConflictsLogName = "merge-conflicts.log";
    private const string SnapshotFileName = "state-in.json";
    private const string FragmentCopyName = "fragment.json";

    private readonly string _stateDirectory;
    private readonly string _statePath;
    private readonly string _seedPath;
    private readonly string _conflictsLogPath;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public StateManager(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        _stateDirectory = Path.Combine(planDirectory, "state");
        _statePath = Path.Combine(_stateDirectory, StateFileName);
        _seedPath = Path.Combine(_stateDirectory, SeedFileName);
        _conflictsLogPath = Path.Combine(_stateDirectory, ConflictsLogName);
    }

    /// <summary>Absolute path to <c>state/state.json</c> (may not exist until <see cref="Initialize"/>).</summary>
    public string StatePath => _statePath;

    /// <summary>
    /// Ensure <c>state.json</c> exists. If missing, create it atomically from
    /// <c>seed.json</c> (if present) else <c>{}</c>. Idempotent — an existing
    /// <c>state.json</c> is left untouched (resume preserves accumulated state).
    /// </summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (File.Exists(_statePath))
            {
                return;
            }

            string initial = File.Exists(_seedPath)
                ? NormalizeOrEmpty(File.ReadAllText(_seedPath))
                : "{}";

            AtomicFile.WriteAllText(_statePath, initial);
        }
    }

    /// <summary>
    /// Copy the current <c>state.json</c> into <paramref name="attemptLogDir"/> as
    /// <c>state-in.json</c> and return its absolute path. The snapshot is a point-in-time
    /// COPY: later merges cannot change what this attempt reads (SSOT §5.1 / §6.2). If
    /// state has not been initialized the snapshot is an empty object.
    /// </summary>
    public string CreateSnapshot(string attemptLogDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptLogDir);
        Directory.CreateDirectory(attemptLogDir);
        string snapshotPath = Path.Combine(attemptLogDir, SnapshotFileName);

        lock (_gate)
        {
            string content = File.Exists(_statePath) ? File.ReadAllText(_statePath) : "{}";
            AtomicFile.WriteAllText(snapshotPath, content);
        }

        return snapshotPath;
    }

    /// <summary>
    /// Merge the fragment at <paramref name="fragmentPath"/> into <c>state.json</c> (SSOT §6.3).
    /// The fragment MUST be a JSON object; anything else is rejected (the caller surfaces this
    /// as the <c>invalid-fragment</c> attempt outcome and leaves state unchanged). On success:
    /// deep-merge, append each overwrite to <c>merge-conflicts.log</c> stamped with
    /// <paramref name="mergeSequence"/> and <paramref name="taskId"/>, atomically replace
    /// <c>state.json</c>, and copy the fragment into <paramref name="attemptLogDir"/> as
    /// <c>fragment.json</c> (audit trail).
    /// </summary>
    public MergeFragmentResult MergeFragment(string taskId, string fragmentPath, long mergeSequence, string attemptLogDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentPath);

        string rawFragment = File.ReadAllText(fragmentPath);

        JsonNode? fragmentNode;
        try
        {
            fragmentNode = JsonNode.Parse(rawFragment, documentOptions: ParseOptions);
        }
        catch (JsonException ex)
        {
            return MergeFragmentResult.Reject(FragmentRejection.NotJson,
                $"invalid state fragment: not valid JSON ({ex.Message})");
        }

        if (fragmentNode is not JsonObject fragmentObject)
        {
            string kind = fragmentNode is null ? "null" : fragmentNode.GetValueKind().ToString().ToLowerInvariant();
            return MergeFragmentResult.Reject(FragmentRejection.NotAnObject,
                $"invalid state fragment: top-level value must be a JSON object, was {kind}");
        }

        lock (_gate)
        {
            JsonObject baseObject = ReadStateLocked();
            MergeResult merge = JsonMerger.Merge(baseObject, fragmentObject);

            AppendConflicts(taskId, mergeSequence, merge.Conflicts);
            AtomicFile.WriteAllText(_statePath, merge.Merged.ToJsonString(IndentedOptions));

            // Audit copy of exactly what was merged.
            Directory.CreateDirectory(attemptLogDir);
            AtomicFile.WriteAllText(Path.Combine(attemptLogDir, FragmentCopyName), rawFragment);

            return MergeFragmentResult.Ok(merge.Conflicts);
        }
    }

    /// <summary>Read the current merged state as a node (for assertions/inspection). Empty object if absent.</summary>
    public JsonObject ReadState()
    {
        lock (_gate)
        {
            return ReadStateLocked();
        }
    }

    private JsonObject ReadStateLocked()
    {
        if (!File.Exists(_statePath))
        {
            return new JsonObject();
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(_statePath), documentOptions: ParseOptions);
        return node as JsonObject ?? new JsonObject();
    }

    private void AppendConflicts(string taskId, long mergeSequence, IReadOnlyList<MergeConflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (MergeConflict conflict in conflicts)
        {
            // Tab-separated: seq, task, jsonPath, old, new (SSOT §6.3).
            builder.Append(mergeSequence).Append('\t')
                   .Append(taskId).Append('\t')
                   .Append(conflict.JsonPath).Append('\t')
                   .Append(conflict.OldValue).Append('\t')
                   .Append(conflict.NewValue).Append('\n');
        }

        Directory.CreateDirectory(_stateDirectory);
        File.AppendAllText(_conflictsLogPath, builder.ToString());
    }

    /// <summary>Normalize seed text to a parseable JSON object string, or "{}" if it is not an object.</summary>
    private static string NormalizeOrEmpty(string seedText)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(seedText, documentOptions: ParseOptions);
            if (node is JsonObject obj)
            {
                return obj.ToJsonString(IndentedOptions);
            }
        }
        catch (JsonException)
        {
            // Fall through to empty — a malformed seed should not abort the run.
        }

        return "{}";
    }

    private static JsonDocumentOptions ParseOptions => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
