namespace Guardrails.Core.Breakdown;

/// <summary>The status of one authored file when LOCAL (on disk) is compared to BASE (the lock).</summary>
public enum BreakdownFileStatus
{
    /// <summary>BASE == LOCAL — the human did not touch it; the merge may take REMOTE freely.</summary>
    Unchanged,

    /// <summary>Present in BASE and LOCAL but the content differs — a human edit to preserve.</summary>
    Edited,

    /// <summary>Present in LOCAL but absent from BASE — a human-authored file to preserve.</summary>
    Added,

    /// <summary>Present in BASE but absent from LOCAL — deleted on disk since last generation.</summary>
    Missing
}

/// <summary>
/// The LOCAL-vs-BASE half of the regeneration merge (SSOT §11, issue #5): which authored files
/// a human created, edited, or deleted since <c>/plan-breakdown</c> last wrote the lock. This is
/// the deterministic primitive the skill consumes to know what NOT to clobber; combining it with
/// REMOTE (a fresh generation) to apply the full per-guardrail merge table — and to block on
/// conflicts — is the skill-orchestration layer tracked separately.
/// </summary>
public sealed record BreakdownDiff
{
    /// <summary><c>relativePath</c> → status, ordinal-sorted.</summary>
    public required IReadOnlyDictionary<string, BreakdownFileStatus> Files { get; init; }

    /// <summary>True when any file is Edited, Added, or Missing relative to BASE.</summary>
    public bool HasDrift => Files.Values.Any(s => s != BreakdownFileStatus.Unchanged);

    /// <summary>Paths a human edited since the lock was written (ordinal order).</summary>
    public IEnumerable<string> Edited => PathsWith(BreakdownFileStatus.Edited);

    /// <summary>Paths a human added since the lock was written (ordinal order).</summary>
    public IEnumerable<string> Added => PathsWith(BreakdownFileStatus.Added);

    /// <summary>Paths present in BASE but no longer on disk (ordinal order).</summary>
    public IEnumerable<string> Missing => PathsWith(BreakdownFileStatus.Missing);

    /// <summary>
    /// Classify each authored file by comparing a freshly captured <paramref name="current"/>
    /// snapshot against the <paramref name="baseManifest"/> recorded in the lock.
    /// </summary>
    public static BreakdownDiff Compute(BreakdownManifest baseManifest, BreakdownManifest current)
    {
        ArgumentNullException.ThrowIfNull(baseManifest);
        ArgumentNullException.ThrowIfNull(current);

        var result = new SortedDictionary<string, BreakdownFileStatus>(StringComparer.Ordinal);

        foreach ((string path, string currentHash) in current.Files)
        {
            if (!baseManifest.Files.TryGetValue(path, out string? baseHash))
            {
                result[path] = BreakdownFileStatus.Added;
            }
            else
            {
                result[path] = string.Equals(baseHash, currentHash, StringComparison.Ordinal)
                    ? BreakdownFileStatus.Unchanged
                    : BreakdownFileStatus.Edited;
            }
        }

        foreach (string path in baseManifest.Files.Keys)
        {
            if (!current.Files.ContainsKey(path))
            {
                result[path] = BreakdownFileStatus.Missing;
            }
        }

        return new BreakdownDiff { Files = result };
    }

    private IEnumerable<string> PathsWith(BreakdownFileStatus status) =>
        Files.Where(kvp => kvp.Value == status).Select(kvp => kvp.Key);
}
