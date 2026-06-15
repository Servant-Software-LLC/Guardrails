using System.Security.Cryptography;
using System.Text.Json;
using Guardrails.Core.State;

namespace Guardrails.Core.Breakdown;

/// <summary>
/// The breakdown manifest (SSOT §11): a content snapshot of the AUTHORED files in a plan
/// folder — <c>guardrails.json</c>, every task's <c>task.json</c> / <c>action.*</c> /
/// <c>guardrails/*</c> file, and the committed <c>state/seed.json</c> — recorded as
/// <c>relativePath → SHA-256</c>. Written to <c>guardrails.baseline</c> at the plan-folder root
/// the moment <c>/plan-breakdown</c> generates (or regenerates) a folder, it is the BASE that
/// a later regeneration diffs the on-disk LOCAL files against, so a human's guardrail edits
/// can be recognized and preserved instead of clobbered (see issue #5). The file is named
/// <c>.baseline</c> (not <c>.lock</c>) because it is a durable, committed drift-detection
/// reference point — a <c>.lock</c> extension would wrongly imply a gitignored transient mutex.
///
/// Harness-owned runtime under <c>state/</c> (<c>state.json</c>, <c>run.json</c>,
/// <c>merge-conflicts.log</c>, <c>logs/…</c>), the generated <c>diagram.md</c>, and the baseline
/// file itself are excluded — they are not authored breakdown content. Hashes are taken over
/// newline-normalized bytes (CRLF/CR → LF, matching <c>PlanHash</c>'s normalization) so CRLF/LF
/// checkouts hash identically; normalization is byte-level, so a non-UTF-8 file is hashed as-is
/// rather than through a lossy decode. The baseline carries no timestamp, so re-capturing an
/// unchanged folder rewrites a byte-identical file (a deterministic projection, no git churn).
/// </summary>
public sealed record BreakdownManifest
{
    /// <summary>The baseline file name, written at the plan-folder root.</summary>
    public const string FileName = "guardrails.baseline";

    /// <summary>The generated diagram artifact (SSOT §10), excluded from the snapshot.</summary>
    private const string DiagramFileName = "diagram.md";

    /// <summary>The current manifest schema version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Schema version of this manifest.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// <c>relativePath</c> (forward-slash, ordinal-sorted) → lowercase-hex SHA-256 of the
    /// file's newline-normalized bytes. This map IS the snapshot — there is no timestamp, so two
    /// manifests with identical <see cref="Files"/> are byte-identical when written.
    /// </summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Absolute path to the baseline file for the given plan folder.</summary>
    public static string BaselineFilePath(string planDirectory) =>
        Path.Combine(Path.GetFullPath(planDirectory), FileName);

    /// <summary>
    /// Snapshot the authored files under <paramref name="planDirectory"/> into a manifest.
    /// Throws <see cref="DirectoryNotFoundException"/> if the folder does not exist.
    /// </summary>
    public static BreakdownManifest Capture(string planDirectory)
    {
        string root = Path.GetFullPath(planDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Plan folder does not exist: {root}");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = ToRelative(root, absolutePath);
            if (ShouldInclude(relativePath))
            {
                files[relativePath] = HashFile(absolutePath);
            }
        }

        return new BreakdownManifest
        {
            Version = CurrentVersion,
            Files = files
        };
    }

    /// <summary>Write the manifest atomically to <c>&lt;planDirectory&gt;/guardrails.baseline</c>.</summary>
    public void Write(string planDirectory)
    {
        var dto = new ManifestDto
        {
            Version = Version,
            // A SortedDictionary serializes in ordinal key order, so the file is stable across runs.
            Files = new SortedDictionary<string, string>(
                Files.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), StringComparer.Ordinal)
        };

        string json = JsonSerializer.Serialize(dto, WriteOptions);
        AtomicFile.WriteAllText(BaselineFilePath(planDirectory), json + "\n");
    }

    /// <summary>
    /// Read the baseline file for <paramref name="planDirectory"/>, or <c>null</c> when it is
    /// missing or cannot be parsed. A missing/unreadable BASE is a normal "no prior generation"
    /// condition the caller decides how to handle — never an exception. Callers that must tell
    /// a missing baseline apart from a corrupt one check <see cref="BaselineFilePath"/> existence
    /// themselves.
    /// </summary>
    public static BreakdownManifest? Read(string planDirectory)
    {
        string path = BaselineFilePath(planDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            ManifestDto? dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(path), ReadOptions);
            if (dto is null)
            {
                return null;
            }

            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (dto.Files is not null)
            {
                foreach ((string key, string value) in dto.Files)
                {
                    files[key] = value;
                }
            }

            return new BreakdownManifest { Version = dto.Version, Files = files };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a plan-folder-relative path (forward-slash) is authored breakdown content that
    /// belongs in the snapshot. Excludes the baseline file (anywhere), the generated
    /// <c>diagram.md</c> (at the plan root), atomic-write temp residue (<c>*.tmp</c>), and
    /// harness-owned runtime under <c>state/</c> — but keeps the committed <c>state/seed.json</c>.
    /// Reserved-name and segment comparisons are <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// so the exclusions hold on case-insensitive filesystems (Windows/macOS).
    /// </summary>
    private static bool ShouldInclude(string relativePath)
    {
        // The baseline file itself, anywhere, is never authored content.
        if (string.Equals(Path.GetFileName(relativePath), FileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Atomic-write residue is never authored.
        if (relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = relativePath.Split('/');

        // The generated diagram lives at the plan root and is a non-authored artifact (§10).
        if (segments.Length == 1 &&
            string.Equals(segments[0], DiagramFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Under state/, only the top-level committed seed.json is authored; everything else
        // (state.json, run.json, merge-conflicts.log, logs/…) is harness-owned runtime.
        if (string.Equals(segments[0], "state", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 2 &&
                string.Equals(segments[1], "seed.json", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string ToRelative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    private static string HashFile(string absolutePath)
    {
        byte[] normalized = NormalizeNewlines(File.ReadAllBytes(absolutePath));
        byte[] hash = SHA256.HashData(normalized);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Collapse CRLF and lone CR to LF at the byte level so CRLF/LF checkouts of the same text
    /// hash identically — without UTF-8 decoding (a non-UTF-8 file would otherwise pick up
    /// U+FFFD replacement characters that could make two distinct files collide). Operates on
    /// raw bytes: <c>0x0D 0x0A → 0x0A</c>, and any remaining <c>0x0D → 0x0A</c>.
    /// </summary>
    private static byte[] NormalizeNewlines(byte[] bytes)
    {
        const byte CR = 0x0D;
        const byte LF = 0x0A;

        var output = new byte[bytes.Length];
        int length = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == CR)
            {
                output[length++] = LF;
                // Skip a following LF so CRLF collapses to a single LF.
                if (i + 1 < bytes.Length && bytes[i + 1] == LF)
                {
                    i++;
                }
            }
            else
            {
                output[length++] = bytes[i];
            }
        }

        return length == output.Length ? output : output[..length];
    }

    /// <summary>Serialization shape of <c>guardrails.baseline</c>.</summary>
    private sealed class ManifestDto
    {
        public int Version { get; set; } = CurrentVersion;
        public IDictionary<string, string>? Files { get; set; }
    }
}
