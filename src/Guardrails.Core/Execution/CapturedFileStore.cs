namespace Guardrails.Core.Execution;

/// <summary>
/// Harness-owned baseline store for files a task declares in <c>captureHashes</c> (issue #51).
/// When a test-author task succeeds, the harness already records each captured file's SHA-256
/// into state (issue #46); this store additionally keeps the file's <b>bytes</b> so the harness
/// can RESTORE it to that authored baseline before a downstream task retries.
///
/// <para><b>Why this exists.</b> The harness does not reset the workspace between attempts, and an
/// authored test file is typically untracked in git — so once an implementation task edits a test
/// file (to make a tests-pass guardrail go green), the <c>tests-untouched</c> guardrail fails and
/// <i>every</i> retry fails identically: the dirtied file persists on disk and the agent has no way
/// to reconstruct the original bytes from a hash. Restoring captured files to baseline at the start
/// of each attempt removes that dead-end — a retry begins from the pristine test file, so an
/// implementation that is correct against the ORIGINAL tests now passes.</para>
///
/// <para>The store lives under <c>state/captured/&lt;authorTaskId&gt;/&lt;relPath&gt;</c> (mirroring
/// the workspace-relative path). It is harness-owned runtime state under <c>state/</c> — gitignored
/// and excluded from the lock manifest, like the rest of <c>state/</c> runtime. Only files named in
/// a task's <c>captureHashes</c> are ever snapshotted or restored — never arbitrary workspace files.</para>
/// </summary>
internal sealed class CapturedFileStore
{
    private readonly string _storeRoot;

    public CapturedFileStore(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        _storeRoot = Path.Combine(planDirectory, "state", "captured");
    }

    /// <summary>
    /// Snapshot the authored bytes of each captured file (resolved against
    /// <paramref name="workspace"/>) into the baseline store under <paramref name="authorTaskId"/>.
    /// Called once, when the author task succeeds. A declared file that does not exist is skipped —
    /// the caller (capture step) already fails the attempt on a missing declared file, so this is
    /// only reached for files that are present.
    /// </summary>
    public void Snapshot(string authorTaskId, IReadOnlyList<string> relativePaths, string workspace)
    {
        foreach (string relativePath in relativePaths)
        {
            string source = Path.GetFullPath(Path.Combine(workspace, relativePath));
            if (!File.Exists(source))
            {
                continue;
            }

            string baseline = BaselinePath(authorTaskId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.Copy(source, baseline, overwrite: true);
        }
    }

    /// <summary>
    /// Restore each captured file of <paramref name="authorTaskId"/> from its baseline copy when the
    /// current workspace bytes differ (or the file was deleted). Byte-for-byte — matching the
    /// raw-byte SHA-256 capture (issue #46), so no normalization drift. Returns the relative paths
    /// actually restored (empty when everything already matched, e.g. the first attempt).
    /// </summary>
    public IReadOnlyList<string> Restore(string authorTaskId, IReadOnlyList<string> relativePaths, string workspace)
    {
        var restored = new List<string>();

        foreach (string relativePath in relativePaths)
        {
            string baseline = BaselinePath(authorTaskId, relativePath);
            if (!File.Exists(baseline))
            {
                continue; // no snapshot recorded — nothing to restore against
            }

            string target = Path.GetFullPath(Path.Combine(workspace, relativePath));
            if (File.Exists(target) && BytesEqual(baseline, target))
            {
                continue; // already pristine
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(baseline, target, overwrite: true);
            restored.Add(relativePath);
        }

        return restored;
    }

    private string BaselinePath(string authorTaskId, string relativePath)
    {
        // relativePath is workspace-relative and forward-slashed (normalized at load, PlanLoader).
        string safeRelative = relativePath.Replace('\\', '/').TrimStart('/');
        return Path.GetFullPath(Path.Combine(_storeRoot, authorTaskId, safeRelative));
    }

    private static bool BytesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length)
        {
            return false;
        }

        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
