using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Guardrails.Core.State;

namespace Guardrails.Core.Breakdown;

/// <summary>
/// The breakdown manifest (SSOT §10): a content snapshot of the AUTHORED files in a plan
/// folder — <c>guardrails.json</c>, every task's <c>task.json</c> / <c>action.*</c> /
/// <c>guardrails/*</c> file, and the committed <c>state/seed.json</c> — recorded as
/// <c>relativePath → SHA-256</c>. Written to <c>guardrails.lock</c> at the plan-folder root
/// the moment <c>/plan-breakdown</c> generates (or regenerates) a folder, it is the BASE that
/// a later regeneration diffs the on-disk LOCAL files against, so a human's guardrail edits
/// can be recognized and preserved instead of clobbered (see issue #5).
///
/// Harness-owned runtime under <c>state/</c> (<c>state.json</c>, <c>run.json</c>,
/// <c>merge-conflicts.log</c>, <c>logs/…</c>), the generated <c>diagram.md</c>, and the lock
/// file itself are excluded — they are not authored breakdown content. Hashes are taken over
/// newline-normalized text (matching <c>PlanHash</c>) so CRLF/LF checkouts hash identically.
/// </summary>
public sealed record BreakdownManifest
{
    /// <summary>The lock file name, written at the plan-folder root.</summary>
    public const string FileName = "guardrails.lock";

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
    /// Informational UTC generation timestamp (<c>yyyy-MM-ddTHH:mm:ssZ</c>). NOT part of the
    /// drift comparison — two manifests with identical <see cref="Files"/> are equivalent.
    /// </summary>
    public string? Generated { get; init; }

    /// <summary>
    /// <c>relativePath</c> (forward-slash, ordinal-sorted) → lowercase-hex SHA-256 of the
    /// file's newline-normalized bytes. This map IS the snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Absolute path to the lock file for the given plan folder.</summary>
    public static string LockFilePath(string planDirectory) =>
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
            Generated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Files = files
        };
    }

    /// <summary>Write the manifest atomically to <c>&lt;planDirectory&gt;/guardrails.lock</c>.</summary>
    public void Write(string planDirectory)
    {
        var dto = new ManifestDto
        {
            Version = Version,
            Generated = Generated,
            // A SortedDictionary serializes in ordinal key order, so the file is stable across runs.
            Files = new SortedDictionary<string, string>(
                Files.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), StringComparer.Ordinal)
        };

        string json = JsonSerializer.Serialize(dto, WriteOptions);
        AtomicFile.WriteAllText(LockFilePath(planDirectory), json + "\n");
    }

    /// <summary>
    /// Read the lock file for <paramref name="planDirectory"/>, or <c>null</c> when it is
    /// missing or cannot be parsed. A missing/unreadable BASE is a normal "no prior generation"
    /// condition the caller decides how to handle — never an exception.
    /// </summary>
    public static BreakdownManifest? Read(string planDirectory)
    {
        string path = LockFilePath(planDirectory);
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

            return new BreakdownManifest { Version = dto.Version, Generated = dto.Generated, Files = files };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a plan-folder-relative path (forward-slash) is authored breakdown content that
    /// belongs in the snapshot. Excludes the lock file, the generated <c>diagram.md</c>,
    /// atomic-write temp residue, and harness-owned runtime under <c>state/</c> — but keeps the
    /// committed <c>state/seed.json</c>.
    /// </summary>
    private static bool ShouldInclude(string relativePath)
    {
        if (relativePath is FileName or "diagram.md")
        {
            return false;
        }

        if (relativePath.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        if (relativePath.StartsWith("state/", StringComparison.Ordinal))
        {
            return relativePath == "state/seed.json";
        }

        return true;
    }

    private static string ToRelative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    private static string HashFile(string absolutePath)
    {
        string normalized = File.ReadAllText(absolutePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Serialization shape of <c>guardrails.lock</c>.</summary>
    private sealed class ManifestDto
    {
        public int Version { get; set; } = CurrentVersion;
        public string? Generated { get; set; }
        public IDictionary<string, string>? Files { get; set; }
    }
}
