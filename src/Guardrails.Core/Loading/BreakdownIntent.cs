using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardrails.Core.Loading;

/// <summary>
/// The wave breakdown's DECLARED decomposition — <c>&lt;plan&gt;/&lt;wave&gt;/state/breakdown-intent.json</c>
/// (SSOT §14.11, design of record <c>docs/plans/20-jit-breakdown-durability.md</c> §4.4, issues #385/#402).
///
/// <para><b>Why a declared list at all.</b> A truncated breakdown leaves a valid PREFIX, and the prefix's
/// DEBT is not computable from the prefix: the measured recovery read the same artifacts and concluded 13
/// tasks when the real number was 14, and the missed one was the SSOT schema-delta task that would have
/// failed the terminal gate after every other task ran green (#474). So <c>plan-breakdown</c> declares the
/// decomposition BEFORE authoring bodies, and the harness compares the declaration against what exists.
/// The rejected alternative — reconstructing the debt from forward references in the already-authored
/// gates — is exactly the fuzzy-text inference GR2055/GR2057 spent their conservatism budget avoiding.</para>
///
/// <para><b>Placement is deliberate.</b> <c>&lt;wave&gt;/state/</c> is already in the §14.1 layout, is already
/// excluded from every definition hash, and is already what <c>--fresh</c> clears — so a mid-breakdown wave
/// that is reset starts over, which is right. The file's lifetime is ONE breakdown attempt: the harness
/// removes it when the wave settles complete, and a quarantine reverts it along with everything else the
/// attempt wrote.</para>
///
/// <para><b>Silence is not proof of validity.</b> An absent or unparseable manifest is SKIPPED entirely
/// (the GR2062 rule) — never an error, never an inferred zero. It does, however, cost the wave its
/// salvage: see <see cref="Execution.Scheduler"/>'s classification, where a preserved prefix requires a
/// manifest because the manifest is the only durable signal that keeps a prefix from reading as a finished
/// wave on the NEXT run.</para>
/// </summary>
public sealed record BreakdownIntent
{
    /// <summary>The manifest's file name inside the wave's hash-excluded <c>state/</c> tree.</summary>
    public const string FileName = "breakdown-intent.json";

    /// <summary>The current manifest schema version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Schema version of this manifest.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>When the breakdown declared this decomposition (informational).</summary>
    [JsonPropertyName("declaredAt")]
    public DateTimeOffset? DeclaredAt { get; init; }

    /// <summary>The ordered decomposition the breakdown intends to author.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<BreakdownIntentTask> Tasks { get; init; } = [];

    /// <summary>The manifest path for a wave folder: <c>&lt;wave&gt;/state/breakdown-intent.json</c>.</summary>
    public static string PathFor(string waveDirectory) =>
        Path.Combine(waveDirectory, "state", FileName);

    /// <summary>
    /// Read a wave's manifest, or <c>null</c> when it is absent, unreadable, unparseable, or declares no
    /// task folders. Never throws — an unparseable manifest must not fail a <c>validate</c> or wedge a run;
    /// it simply means the wave has no declaration to compare against.
    /// </summary>
    public static BreakdownIntent? TryRead(string waveDirectory)
    {
        try
        {
            string path = PathFor(waveDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            BreakdownIntent? intent =
                JsonSerializer.Deserialize<BreakdownIntent>(File.ReadAllText(path), ReadOptions);
            return intent is null || intent.DeclaredFolders().Count == 0 ? null : intent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The declared task-folder names, trimmed, de-duplicated (ordinal), in declaration order. Entries with
    /// a missing/blank <c>folder</c>, or one carrying a path separator, are dropped: the manifest names
    /// folders directly under the wave's <c>tasks/</c>, and anything else is not something this compare can
    /// resolve — dropping it is the conservative reading (a shortfall we cannot name is not reported).
    /// </summary>
    public IReadOnlyList<string> DeclaredFolders()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<string>();
        foreach (BreakdownIntentTask task in Tasks)
        {
            string folder = task.Folder?.Trim() ?? "";
            if (folder.Length == 0
                || folder.Contains('/', StringComparison.Ordinal)
                || folder.Contains('\\', StringComparison.Ordinal)
                || !seen.Add(folder))
            {
                continue;
            }

            folders.Add(folder);
        }

        return folders;
    }

    /// <summary>
    /// The declared folders that have no COMPLETE task folder under <c>&lt;wave&gt;/tasks/</c>, in
    /// declaration order. Empty ⇒ the manifest is satisfied.
    /// </summary>
    public IReadOnlyList<string> MissingFolders(string waveDirectory)
    {
        string tasksDir = Path.Combine(waveDirectory, "tasks");
        return [.. DeclaredFolders().Where(f => !IsCompleteTaskFolder(Path.Combine(tasksDir, f)))];
    }

    /// <summary>
    /// The loader's own completeness predicate for one task folder, re-expressed as a file-system question:
    /// a <c>task.json</c> is present AND an action resolves (either exactly the convention — some
    /// <c>action.*</c> file in the folder — or an explicit <c>action.path</c> the manifest names).
    /// <para>Deliberately conservative in favour of KEEPING: this predicate decides what the post-invocation
    /// sweep MOVES to <c>rejected/</c>, so a folder is treated as complete unless it is clearly not. A
    /// task.json we cannot parse counts as "has an explicit action" rather than risking a sweep of authored
    /// work — the loader will report it honestly at the validate gate either way.</para>
    /// </summary>
    public static bool IsCompleteTaskFolder(string taskFolder)
    {
        try
        {
            if (!Directory.Exists(taskFolder) || !File.Exists(Path.Combine(taskFolder, "task.json")))
            {
                return false;
            }

            bool conventionAction = Directory
                .EnumerateFiles(taskFolder, "action.*", SearchOption.TopDirectoryOnly)
                .Any();

            return conventionAction || DeclaresExplicitActionPath(Path.Combine(taskFolder, "task.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a <c>task.json</c> carries a non-empty <c>action.path</c> — or when it cannot be read/parsed
    /// at all (see the conservatism note on <see cref="IsCompleteTaskFolder"/>).
    /// </summary>
    private static bool DeclaresExplicitActionPath(string taskJsonPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(taskJsonPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("action", out JsonElement action)
                   && action.ValueKind == JsonValueKind.Object
                   && action.TryGetProperty("path", out JsonElement path)
                   && path.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(path.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true; // unreadable/unparseable ⇒ do not sweep it; let the validate gate speak
        }
    }
}

/// <summary>One declared entry in a <see cref="BreakdownIntent"/>: the task folder and why it exists.</summary>
public sealed record BreakdownIntentTask
{
    /// <summary>The task folder name the breakdown intends to author under <c>&lt;wave&gt;/tasks/</c>.</summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; init; }

    /// <summary>A one-line statement of the task's purpose (informational; surfaced in the resume prompt).</summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }
}
