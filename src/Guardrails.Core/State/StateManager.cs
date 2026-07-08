using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Guardrails.Core.State;

/// <summary>
/// Why a fragment could not be merged into <c>state.json</c>. SSOT §6.2: a fragment that
/// exists but is not a parseable JSON object fails the attempt with "invalid state fragment";
/// a fragment whose top-level keys are not all owned by the writing task fails the same way.
/// </summary>
public enum FragmentRejection
{
    /// <summary>The fragment file was not valid JSON.</summary>
    NotJson,

    /// <summary>The fragment parsed but its top-level value is not a JSON object.</summary>
    NotAnObject,

    /// <summary>
    /// The fragment is a JSON object but carries a top-level key that the writing task does not
    /// own — a foreign task id or an arbitrary shared key. The single-writer-per-key rule (SSOT
    /// §6.2, issue #48) requires every top-level key to be the task's own id (or a harness
    /// reserved key — none in v1), so no task can poison another's namespace.
    /// </summary>
    ForeignKey
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

    /// <summary>
    /// When <see cref="Rejection"/> is <see cref="FragmentRejection.ForeignKey"/>, the offending
    /// top-level key(s) the task does not own. Empty otherwise. Lets the caller compose feedback
    /// that names the exact stray key for the retry.
    /// </summary>
    public IReadOnlyList<string> ForeignKeys { get; init; } = [];

    /// <summary>Overwrites of pre-existing non-null values caused by this merge (empty on rejection).</summary>
    public IReadOnlyList<MergeConflict> Conflicts { get; init; } = [];

    internal static MergeFragmentResult Ok(IReadOnlyList<MergeConflict> conflicts) =>
        new() { Merged = true, Conflicts = conflicts };

    internal static MergeFragmentResult Reject(FragmentRejection rejection, string reason) =>
        new() { Merged = false, Rejection = rejection, Reason = reason };

    internal static MergeFragmentResult RejectForeignKeys(string reason, IReadOnlyList<string> foreignKeys) =>
        new() { Merged = false, Rejection = FragmentRejection.ForeignKey, Reason = reason, ForeignKeys = foreignKeys };
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

    /// <summary>
    /// Top-level keys a merged fragment may carry IN ADDITION to the writing task's own id
    /// (SSOT §6.2, issue #48). EMPTY in v1 by design: the harness is the single writer of every
    /// namespace, so no task can write under another task's id or any shared key. Any future
    /// reserved key MUST carry its own anti-poisoning analysis before admission here — a shared
    /// writable namespace is exactly the cross-task poisoning vector this rule closes.
    /// </summary>
    internal static readonly IReadOnlySet<string> ReservedMergeKeys =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly string _planDirectory;
    private readonly string _stateDirectory;
    private readonly string _statePath;
    private readonly string _seedPath;
    private readonly string _conflictsLogPath;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public StateManager(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        _planDirectory = planDirectory;
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
    /// Also scaffolds the plan-root <c>.gitignore</c> (issue #258) that keeps transient
    /// runtime state out of version control — see <see cref="PlanGitignore"/>.
    /// </summary>
    public void Initialize()
    {
        // Issue #258: scaffold the plan-root .gitignore so a routine `git add <plan-folder>/` cannot
        // commit transient runtime state (run.json/state.json/logs/... — the RunReset.Fresh set).
        // This is THE run-init choke point: reached by every run (SchedulerFactory.CreateExecutor),
        // by `--fresh` (RunReset.Fresh re-seeds through here), and by the direct-Initialize test paths.
        // Done BEFORE the resume early-return below so a plan first run predating this feature still
        // gets its ignore file on the next run. Non-clobbering + idempotent (PlanGitignore guards).
        PlanGitignore.EnsureScaffolded(_planDirectory);

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
    /// The fragment MUST be a JSON object whose top-level keys are each <paramref name="taskId"/>
    /// (or a harness reserved key — none in v1); anything else — non-JSON, a non-object, or a
    /// foreign/shared top-level key — is rejected (the caller surfaces this as the
    /// <c>invalid-fragment</c> attempt outcome and leaves state unchanged). On success:
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

        // Single-writer-per-key (SSOT §6.2, issue #48): every top-level key must be the writing
        // task's OWN id (Ordinal — matches duplicate-id detection and task.Id = folder name) or a
        // harness reserved key (none in v1). A foreign task id or an arbitrary shared key would let
        // one task poison another's namespace (e.g. the captured tests-untouched hashes), so we
        // REJECT — not strip — the whole fragment. The attempt then fails as invalid-fragment,
        // retries with feedback, and nothing is merged. An empty fragment passes vacuously.
        // A task overwriting its OWN namespace is allowed (self-inflicted, not cross-task poisoning);
        // `needsHuman` is exempt because it short-circuits before this merge runs.
        IReadOnlyList<string> foreignKeys = fragmentObject
            .Select(pair => pair.Key)
            .Where(key => !string.Equals(key, taskId, StringComparison.Ordinal)
                          && !ReservedMergeKeys.Contains(key))
            .ToList();

        if (foreignKeys.Count > 0)
        {
            string named = string.Join(", ", foreignKeys.Select(k => $"'{k}'"));
            return MergeFragmentResult.RejectForeignKeys(
                $"invalid state fragment: top-level key(s) {named} are not owned by task '{taskId}'; " +
                $"a task may only write under its own id",
                foreignKeys);
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
