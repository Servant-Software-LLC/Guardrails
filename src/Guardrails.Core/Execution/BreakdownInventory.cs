using System.Security.Cryptography;
using System.Text.Json;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Execution;

/// <summary>
/// The harness's own record of what a wave folder contained IMMEDIATELY BEFORE a JIT breakdown invocation
/// (SSOT §14.11, design of record <c>docs/plans/20-jit-breakdown-durability.md</c> §4.3/§5, issues
/// #385/#402/#471/#489). The harness is the single writer of merged state (invariant 2) and owns the
/// invocation boundary, so this is EXACT — not a heuristic about provenance.
///
/// <para><b>Why it exists.</b> The shipped quarantine moved <c>tasks/</c> and reported "the wave reverted to
/// its empty stub" while leaving the wave's <c>guardrails/</c> and <c>preflights/</c> behind — a message that
/// was false, and a residue that moved <c>PlanDefinitionHash</c> and so spent the plan's review attestation
/// (#471). The obvious correction — revert the gate folders too — has a hazard the issue missed: a human may
/// hand-author a wave's exit gate BEFORE the breakdown runs ("define the postconditions, let the breakdown
/// fill the tasks" is a good pattern), and a blind revert moves that human's work to <c>rejected/</c> while
/// calling it a revert. The inventory dissolves the dilemma: the revert moves exactly what the ATTEMPT wrote
/// and leaves everything that pre-dated it byte-identical.</para>
///
/// <para><b>Scope.</b> The three subtrees that <c>PlanDefinitionHash</c> actually folds for a wave —
/// <c>tasks/</c>, <c>guardrails/</c>, <c>preflights/</c>. That scope is what makes §5.4's property provable
/// rather than hopeful: restore those three to their pre-invocation bytes and the plan hash is byte-identical.
/// The hash-excluded <c>state/</c> tree is deliberately NOT swept wholesale (it holds the wave's review
/// marker, which no breakdown writes); the one <c>state/</c> file a breakdown does write —
/// <see cref="BreakdownIntent.FileName"/> — is tracked individually.</para>
///
/// <para><b>Content snapshot, not just hashes.</b> The design records <c>path → (size, sha256)</c>, which is
/// enough to CLASSIFY a file but not enough to RESTORE one the attempt overwrote. A pre-existing file that the
/// attempt modified must go back to its own bytes, or the "hash is byte-identical after a quarantine"
/// invariant quietly fails in exactly the hand-authored-gate case the design added the inventory to protect.
/// So the capture also copies the pre-existing files (a wave stub holds a handful of small scripts) into a
/// sibling <c>pre-invocation/</c> folder under the breakdown log dir.</para>
/// </summary>
public sealed class BreakdownInventory
{
    /// <summary>The wave subtrees an inventory covers — exactly what a wave contributes to <c>PlanDefinitionHash</c>.</summary>
    private static readonly string[] HashedSubtrees = ["tasks", "guardrails", "preflights"];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _waveDirectory;
    private readonly string _snapshotRoot;

    /// <summary>Pre-invocation files, keyed by wave-relative path with <c>/</c> separators.</summary>
    private readonly Dictionary<string, FileFingerprint> _files;

    /// <summary>Wave-relative directory paths that existed before the invocation (so a revert never prunes one).</summary>
    private readonly HashSet<string> _directories;

    /// <summary>True when the wave already carried a breakdown-intent manifest before this attempt.</summary>
    private readonly bool _intentManifestPreExisted;

    private BreakdownInventory(
        string waveDirectory,
        string snapshotRoot,
        Dictionary<string, FileFingerprint> files,
        HashSet<string> directories,
        bool intentManifestPreExisted)
    {
        _waveDirectory = waveDirectory;
        _snapshotRoot = snapshotRoot;
        _files = files;
        _directories = directories;
        _intentManifestPreExisted = intentManifestPreExisted;
    }

    /// <summary>
    /// Walk the wave folder's hashed subtrees, fingerprint every file, snapshot their bytes under
    /// <paramref name="breakdownLogDir"/><c>/pre-invocation/</c>, and write the forensic
    /// <c>pre-invocation.json</c> the #471 investigation had to reconstruct by hand. Returns <c>null</c> only
    /// when the walk itself failed — the caller then degrades to the pre-#471 whole-<c>tasks/</c> quarantine
    /// rather than guessing at provenance.
    /// </summary>
    public static BreakdownInventory? Capture(string waveDirectory, string breakdownLogDir)
    {
        try
        {
            string snapshotRoot = Path.Combine(breakdownLogDir, "pre-invocation");
            var files = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);

            foreach (string subtree in HashedSubtrees)
            {
                string root = Path.Combine(waveDirectory, subtree);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                directories.Add(subtree);
                foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    directories.Add(RelativeKey(waveDirectory, dir));
                }

                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string key = RelativeKey(waveDirectory, file);
                    files[key] = Fingerprint(file);
                    SnapshotFile(file, Path.Combine(snapshotRoot, key.Replace('/', Path.DirectorySeparatorChar)));
                }
            }

            bool intentPreExisted = File.Exists(BreakdownIntent.PathFor(waveDirectory));
            var inventory = new BreakdownInventory(
                waveDirectory, snapshotRoot, files, directories, intentPreExisted);
            inventory.PersistManifest(Path.Combine(breakdownLogDir, "pre-invocation.json"));
            return inventory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The number of files that pre-dated the invocation (for the halt message and for tests).</summary>
    public int PreExistingFileCount => _files.Count;

    /// <summary>True when the attempt CREATED the task folder <paramref name="folderName"/> (nothing under it pre-existed).</summary>
    public bool AttemptCreatedTaskFolder(string folderName)
    {
        string prefix = $"tasks/{folderName}/";
        return !_files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
               && !_directories.Contains($"tasks/{folderName}");
    }

    /// <summary>
    /// Move to <c>rejected/</c> every task folder that (a) the inventory shows the attempt CREATED and
    /// (b) fails the loader's completeness predicate (<c>task.json</c> present AND an action resolved).
    /// Both conditions must hold; nothing is deleted. This is what turns an "11 complete + 1 half-written"
    /// truncation into an 11-task valid prefix instead of a whole discarded wave.
    /// Returns the swept folder names, ordinal-sorted.
    /// </summary>
    public IReadOnlyList<string> SweepIncompleteTrailingTaskFolders(string rejectedRoot)
    {
        string tasksDir = Path.Combine(_waveDirectory, "tasks");
        if (!Directory.Exists(tasksDir))
        {
            return [];
        }

        var swept = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(tasksDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            if (!AttemptCreatedTaskFolder(name) || BreakdownIntent.IsCompleteTaskFolder(folder))
            {
                continue;
            }

            if (TryMoveDirectory(folder, Path.Combine(rejectedRoot, "tasks", name)))
            {
                swept.Add(name);
            }
        }

        return swept;
    }

    /// <summary>
    /// Revert the wave to its pre-invocation state: every file the attempt CREATED or MODIFIED under the
    /// hashed subtrees moves to <paramref name="rejectedRoot"/> preserving its relative path; every
    /// pre-existing file is restored byte-for-byte from the snapshot; every file the attempt did not touch is
    /// left exactly where it is. The empty <c>tasks/</c> stub is restored so the plan stays loadable and the
    /// JIT checkpoint cleanly re-fires. The attempt's own <c>state/breakdown-intent.json</c> is removed too
    /// (its lifetime is one attempt) unless it pre-dated the invocation.
    ///
    /// <para><b>The provable property:</b> a wave contributes to <c>PlanDefinitionHash</c> exactly its tasks'
    /// file sets plus its <c>guardrails/</c> and <c>preflights/</c> folders, so restoring those three to their
    /// pre-invocation bytes leaves the plan hash byte-identical — a quarantine never spends a review
    /// attestation. That is not a claim; it is the regression test.</para>
    /// </summary>
    public RevertSummary Revert(string rejectedRoot)
    {
        var moved = new List<string>();
        var restored = new List<string>();
        var kept = new List<string>();

        foreach (string subtree in HashedSubtrees)
        {
            string root = Path.Combine(_waveDirectory, subtree);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                string key = RelativeKey(_waveDirectory, file);
                _files.TryGetValue(key, out FileFingerprint? before);
                if (before is not null && Fingerprint(file) == before)
                {
                    kept.Add(key); // pre-existing and untouched — a human's hand-authored gate stays put
                    continue;
                }

                if (!TryMoveFile(file, Path.Combine(rejectedRoot, key.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                moved.Add(key);
                if (before is not null && RestoreFromSnapshot(key))
                {
                    restored.Add(key);
                }
            }
        }

        // A pre-existing file the attempt DELETED is restored too — same property, other direction.
        foreach (string key in _files.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string path = Path.Combine(_waveDirectory, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) && RestoreFromSnapshot(key))
            {
                restored.Add(key);
            }
        }

        PruneAttemptCreatedDirectories();
        RemoveAttemptIntentManifest();
        TryCreateDirectory(Path.Combine(_waveDirectory, "tasks")); // the empty JIT stub

        return new RevertSummary
        {
            MovedPaths = moved,
            RestoredPaths = restored,
            KeptPaths = kept
        };
    }

    /// <summary>Delete the attempt's <c>state/breakdown-intent.json</c> — its lifetime is one attempt.</summary>
    public void RemoveAttemptIntentManifest()
    {
        if (_intentManifestPreExisted)
        {
            return;
        }

        TryDeleteFile(BreakdownIntent.PathFor(_waveDirectory));
    }

    /// <summary>
    /// Delete directories under the hashed subtrees that are now EMPTY and did not pre-exist — the folder
    /// shells a moved-away attempt leaves behind. A pre-existing empty directory is never pruned (it may be a
    /// deliberate stub), and the three subtree roots themselves are never pruned.
    /// </summary>
    private void PruneAttemptCreatedDirectories()
    {
        foreach (string subtree in HashedSubtrees)
        {
            string root = Path.Combine(_waveDirectory, subtree);
            if (!Directory.Exists(root))
            {
                continue;
            }

            // Deepest-first, so a nested shell is removed before its parent is considered.
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                string key = RelativeKey(_waveDirectory, dir);
                if (_directories.Contains(key))
                {
                    continue;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best-effort: an un-prunable shell is cosmetic, and it contributes nothing to the hash
                }
            }
        }
    }

    private bool RestoreFromSnapshot(string key)
    {
        string source = Path.Combine(_snapshotRoot, key.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(_waveDirectory, key.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(source))
            {
                return false;
            }

            TryCreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void PersistManifest(string path)
    {
        try
        {
            TryCreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new
            {
                version = 1,
                capturedAt = DateTimeOffset.UtcNow,
                waveDirectory = _waveDirectory,
                files = _files
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { path = kv.Key, size = kv.Value.Size, sha256 = kv.Value.Sha256 })
                    .ToArray()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort forensic tee: losing it must never fail the run
        }
    }

    private static void SnapshotFile(string source, string target)
    {
        TryCreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static FileFingerprint Fingerprint(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new FileFingerprint(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string RelativeKey(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool TryMoveFile(string source, string target)
    {
        try
        {
            TryCreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(source, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMoveDirectory(string source, string target)
    {
        try
        {
            TryCreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(source, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }

    /// <summary>A pre-invocation file's size + content hash — enough to classify created / modified / untouched.</summary>
    private sealed record FileFingerprint(long Size, string Sha256);
}

/// <summary>
/// What an inventory-scoped revert actually did, in the operator's terms — the contract behind the halt
/// message that used to say "the wave reverted to its empty stub" while leaving eight files behind (#471).
/// Rendering is a <c>guardrails-ux</c>/#469 concern; these are the facts it renders.
/// </summary>
public sealed record RevertSummary
{
    /// <summary>Wave-relative paths the attempt wrote, now under <c>rejected/</c> preserving their relative paths.</summary>
    public IReadOnlyList<string> MovedPaths { get; init; } = [];

    /// <summary>Wave-relative paths restored byte-for-byte from the pre-invocation snapshot (the attempt had overwritten or deleted them).</summary>
    public IReadOnlyList<string> RestoredPaths { get; init; } = [];

    /// <summary>Wave-relative paths that pre-dated the attempt and were left byte-identical — a human's hand-authored gate lives here.</summary>
    public IReadOnlyList<string> KeptPaths { get; init; } = [];
}
