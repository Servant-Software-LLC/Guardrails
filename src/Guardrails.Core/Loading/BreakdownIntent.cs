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
///
/// <para><b>But PRESENT-and-unusable is not silence (GR2064).</b> A manifest that exists and PARSES can
/// still yield zero usable folders — every <c>folder</c> blank, path-bearing, or an ordinal duplicate, or
/// no <c>tasks</c> entries at all. Read through <see cref="TryRead"/> alone that is indistinguishable from
/// ABSENT, so a single typo silently bought the operator no GR2063, no prefix preservation, and no
/// diagnostic naming either loss. <see cref="Read"/> is therefore the full-fidelity entry point — it
/// reports WHICH of the four states holds and why — and <see cref="TryRead"/> is the deliberate
/// usable-or-nothing convenience over it. Anything that must tell "there is no manifest" from "the
/// manifest is broken" (the validator's GR2064; the Scheduler's quarantine reason, which would otherwise
/// state a falsehood) must use <see cref="Read"/>.</para>
/// </summary>
public sealed record BreakdownIntent
{
    /// <summary>The manifest's file name inside the wave's hash-excluded <c>state/</c> tree.</summary>
    public const string FileName = "breakdown-intent.json";

    /// <summary>The current manifest schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// JSON comments and trailing commas are ACCEPTED, matching every other manifest this repo reads and
    /// doc 20 §4.4's own <c>jsonc</c> example. Tolerance here is deliberate: the strictness budget belongs
    /// on the one load-bearing field (<c>tasks[].folder</c>), and a rejected manifest silently costs the
    /// wave its salvage, so refusing one over punctuation buys nothing and loses the thing.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Schema version of this manifest. OPTIONAL — an absent <c>version</c> resolves
    /// <see cref="CurrentVersion"/>. Nothing in the harness reads it: the reader understands exactly one
    /// shape, so there is no version to switch on and no honest way to refuse one. It is written for the
    /// day a second shape exists, and until then absence is not an error (SSOT §14.11).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// When the breakdown declared this decomposition. OPTIONAL and purely informational — read by nothing.
    /// Requiring it would make a missing timestamp cost the wave its salvage, which is the exact failure
    /// GR2064 exists to stop.
    /// </summary>
    [JsonPropertyName("declaredAt")]
    public DateTimeOffset? DeclaredAt { get; init; }

    /// <summary>The ordered decomposition the breakdown intends to author.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<BreakdownIntentTask> Tasks { get; init; } = [];

    /// <summary>The manifest path for a wave folder: <c>&lt;wave&gt;/state/breakdown-intent.json</c>.</summary>
    public static string PathFor(string waveDirectory) =>
        Path.Combine(waveDirectory, "state", FileName);

    /// <summary>
    /// Read a wave's manifest, reporting WHICH of the four states holds — absent, unreadable/unparseable,
    /// present-but-yielding-no-usable-folder, or usable. Never throws: an unparseable manifest must not
    /// fail a <c>validate</c> or wedge a run.
    /// <para>This is the full-fidelity entry point. <see cref="TryRead"/> collapses the middle two states
    /// into <c>null</c>, which is right for every caller that only wants a declaration to compare against
    /// and wrong for every caller that must SAY why there is none.</para>
    /// </summary>
    public static BreakdownIntentRead Read(string waveDirectory)
    {
        string path = PathFor(waveDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return new BreakdownIntentRead
                {
                    Presence = BreakdownIntentPresence.Absent,
                    Path = path
                };
            }

            BreakdownIntent? intent =
                JsonSerializer.Deserialize<BreakdownIntent>(File.ReadAllText(path), ReadOptions);
            if (intent is null)
            {
                // Parseable JSON whose content is the literal `null`: present, and no manifest object.
                return new BreakdownIntentRead
                {
                    Presence = BreakdownIntentPresence.NoUsableEntries,
                    Path = path,
                    Explanation = "contains the JSON literal 'null' rather than a manifest object"
                };
            }

            if (intent.DeclaredFolders().Count > 0)
            {
                return new BreakdownIntentRead
                {
                    Presence = BreakdownIntentPresence.Usable,
                    Path = path,
                    Usable = intent
                };
            }

            IReadOnlyList<string> rejected = intent.RejectedEntries();
            return new BreakdownIntentRead
            {
                Presence = BreakdownIntentPresence.NoUsableEntries,
                Path = path,
                RejectedEntries = rejected,
                Explanation = rejected.Count == 0
                    ? "declares no 'tasks' entries at all"
                    : $"declares {rejected.Count} task entry(s), none of them usable"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BreakdownIntentRead
            {
                Presence = BreakdownIntentPresence.Unreadable,
                Path = path
            };
        }
    }

    /// <summary>
    /// Read a wave's manifest, or <c>null</c> when it is absent, unreadable, unparseable, or declares no
    /// usable task folders. Never throws.
    /// <para><b>Deliberately lossy.</b> A present-but-unusable manifest reads as <c>null</c> here exactly
    /// like an absent one, because a caller asking this question wants a declaration to compare against and
    /// there is none either way. A caller that must tell the two apart — to warn (GR2064) or to compose an
    /// honest message — must call <see cref="Read"/> instead.</para>
    /// </summary>
    public static BreakdownIntent? TryRead(string waveDirectory) => Read(waveDirectory).Usable;

    /// <summary>
    /// The declared task-folder names, trimmed, de-duplicated (ordinal), in declaration order. Entries with
    /// a missing/blank <c>folder</c>, or one carrying a path separator, are dropped: the manifest names
    /// folders directly under the wave's <c>tasks/</c>, and anything else is not something this compare can
    /// resolve — dropping it is the conservative reading (a shortfall we cannot name is not reported).
    /// <para>Dropping is silent HERE by design, but not silent overall: <see cref="RejectedEntries"/> is the
    /// paired accounting, and <see cref="Read"/> raises it once every entry is dropped.</para>
    /// </summary>
    public IReadOnlyList<string> DeclaredFolders() =>
        [.. Classify().Where(e => e.Reason is null).Select(e => e.Folder)];

    /// <summary>
    /// One human-readable line per entry <see cref="DeclaredFolders"/> DROPPED, naming the entry's position,
    /// its <c>folder</c> as written, and why it could not be used. Empty when every entry is usable (and
    /// also when there are no entries at all — "declares nothing" is a different sentence, composed by
    /// <see cref="Read"/>).
    /// </summary>
    public IReadOnlyList<string> RejectedEntries() =>
    [
        .. Classify()
            .Where(e => e.Reason is not null)
            .Select(e => $"entry {e.Index} '{e.Raw}' ({e.Reason})")
    ];

    /// <summary>
    /// The single pass both <see cref="DeclaredFolders"/> and <see cref="RejectedEntries"/> read, so the
    /// accepted set and the rejection ledger cannot disagree about any entry.
    /// </summary>
    private IEnumerable<(int Index, string Raw, string Folder, string? Reason)> Classify()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (BreakdownIntentTask task in Tasks)
        {
            index++;
            string raw = task.Folder ?? "";
            string folder = raw.Trim();
            string? reason =
                folder.Length == 0 ? "its 'folder' is missing or blank"
                : folder.Contains('/', StringComparison.Ordinal)
                  || folder.Contains('\\', StringComparison.Ordinal)
                    ? "it carries a path separator; the manifest names folders directly under the wave's 'tasks/'"
                    : !seen.Add(folder) ? "it repeats an earlier entry's folder"
                        : null;

            yield return (index, raw, folder, reason);
        }
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

/// <summary>Which of the four states a wave's <c>breakdown-intent.json</c> was found in (SSOT §14.11).</summary>
public enum BreakdownIntentPresence
{
    /// <summary>No file. The wave never declared a decomposition; every intent-keyed check is skipped.</summary>
    Absent,

    /// <summary>
    /// Present, but the bytes could not be turned into a manifest (IO error, malformed JSON). SILENT by
    /// SSOT §14.11 — <c>validate</c> is read-only and must not punish a plan for an unreadable runtime file.
    /// </summary>
    Unreadable,

    /// <summary>
    /// Present and PARSED, but it yields no usable task folder — no <c>tasks</c> entries, or every entry
    /// blank / path-bearing / a duplicate. Salvage is disabled exactly as if the file did not exist, which
    /// is why this state is NOT silent: it is <see cref="DiagnosticCodes.BreakdownIntentDeclaresNothing"/>.
    /// </summary>
    NoUsableEntries,

    /// <summary>Present, parsed, and declaring at least one usable folder.</summary>
    Usable
}

/// <summary>
/// The outcome of reading one wave's manifest — <see cref="BreakdownIntent.Read"/>'s full-fidelity result.
/// The point of the type is that <see cref="BreakdownIntentPresence.Absent"/> and
/// <see cref="BreakdownIntentPresence.NoUsableEntries"/> are DISTINGUISHABLE: they cost the wave the same
/// salvage, but only one of them is something an operator can fix, and only one of them should be said out
/// loud.
/// </summary>
public sealed record BreakdownIntentRead
{
    /// <summary>Which state the manifest was found in.</summary>
    public required BreakdownIntentPresence Presence { get; init; }

    /// <summary>The manifest path that was probed — named in the diagnostic, present or not.</summary>
    public required string Path { get; init; }

    /// <summary>The manifest, non-null iff <see cref="Presence"/> is <see cref="BreakdownIntentPresence.Usable"/>.</summary>
    public BreakdownIntent? Usable { get; init; }

    /// <summary>Per-entry rejection lines (<see cref="BreakdownIntent.RejectedEntries"/>); empty unless entries were dropped.</summary>
    public IReadOnlyList<string> RejectedEntries { get; init; } = [];

    /// <summary>
    /// One clause, in the third person and grammatical after "the manifest …", saying why a PRESENT manifest
    /// yields nothing. Empty for every other <see cref="Presence"/>.
    /// </summary>
    public string Explanation { get; init; } = "";

    /// <summary>True when a file exists at <see cref="Path"/>, whatever came of reading it.</summary>
    public bool IsPresent => Presence != BreakdownIntentPresence.Absent;
}
