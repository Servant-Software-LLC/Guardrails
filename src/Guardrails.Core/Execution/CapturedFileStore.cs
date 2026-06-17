namespace Guardrails.Core.Execution;

/// <summary>
/// Harness-owned baseline store for files a task declares in <c>captureHashes</c> AND opts into with
/// <c>restoreOnRetry: true</c> (issue #51). When such an author task succeeds, the harness already
/// records each captured file's SHA-256 into state (issue #46); this store additionally keeps the
/// file's <b>bytes</b> so the harness can RESTORE it to that authored baseline before a downstream
/// task retries.
///
/// <para><b>Opt-in.</b> Snapshot and restore act ONLY for an author task that set
/// <c>restoreOnRetry: true</c>. A task that declares <c>captureHashes</c> WITHOUT
/// <c>restoreOnRetry</c> is hashed for tamper-detection only — no byte snapshot, no restore. The
/// gating lives in the caller (<see cref="TaskExecutor"/>); this store snapshots/restores exactly
/// the relpaths it is handed.</para>
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
///
/// <para><b>Containment (FIX B).</b> Both snapshot and restore resolve each captured relpath against
/// the plan <b>workspace</b> — the canonical, GR2013-validated base — never a per-task
/// <c>workingDirectory</c>. Before any <see cref="File.Copy(string,string,bool)"/> the resolved
/// workspace target is re-checked with <see cref="WorkspaceContainment"/>; if it escapes the
/// workspace, the harness does NOT write (defense-in-depth, independent of GR2013) and the restore
/// records it as un-restorable so the gap is visible (issue #51 FIX D).</para>
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
    /// Snapshot the authored bytes of each captured file (resolved against the plan
    /// <paramref name="workspace"/>) into the baseline store under <paramref name="authorTaskId"/>.
    /// Called once, when the author task succeeds AND opted into <c>restoreOnRetry</c>. A declared
    /// file that does not exist is skipped — the capture step already fails the attempt on a missing
    /// declared file, so this is only reached for files that are present. An entry whose resolved
    /// target escapes the workspace is skipped (defense-in-depth; GR2013 should have rejected it).
    /// </summary>
    /// <remarks>
    /// The <see cref="File.Copy(string,string,bool)"/> here is intentionally non-atomic, and that is
    /// safe: the author attempt is NOT journaled <c>succeeded</c> until after this snapshot returns
    /// (the merge step runs strictly later), so a crash mid-copy re-runs the author on resume and
    /// re-snapshots from scratch. Do NOT reorder snapshot after the journal write without revisiting
    /// this invariant.
    /// </remarks>
    public void Snapshot(string authorTaskId, IReadOnlyList<string> relativePaths, string workspace)
    {
        foreach (string relativePath in relativePaths)
        {
            if (WorkspaceContainment.Escapes(workspace, relativePath))
            {
                continue; // never read/write outside the workspace — GR2013 should have caught this.
            }

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
    /// current workspace bytes differ (or the file was deleted). Paths resolve against the plan
    /// <paramref name="workspace"/> (FIX B) — the same base GR2013 validated — and each write is
    /// guarded by a <see cref="WorkspaceContainment"/> assert. Byte-for-byte — matching the raw-byte
    /// SHA-256 capture (issue #46), so no normalization drift.
    /// </summary>
    /// <returns>
    /// A <see cref="RestoreOutcome"/> listing the relpaths actually restored (empty when everything
    /// already matched, e.g. the first attempt) and the relpaths that could NOT be restored even
    /// though their bytes differ — a missing baseline, or a containment-skip (issue #51 FIX D).
    /// </returns>
    public RestoreOutcome Restore(string authorTaskId, IReadOnlyList<string> relativePaths, string workspace)
    {
        var restored = new List<string>();
        var unrestorable = new List<UnrestorableFile>();

        foreach (string relativePath in relativePaths)
        {
            if (WorkspaceContainment.Escapes(workspace, relativePath))
            {
                // Defense-in-depth: never write outside the workspace. GR2013 should have rejected
                // this at validate time; record it rather than silently skipping (FIX D).
                unrestorable.Add(new UnrestorableFile(relativePath, "resolved target escapes the workspace"));
                continue;
            }

            string baseline = BaselinePath(authorTaskId, relativePath);
            string target = Path.GetFullPath(Path.Combine(workspace, relativePath));

            if (!File.Exists(baseline))
            {
                // No snapshot recorded. If the workspace file is gone or differs from a (now absent)
                // baseline we cannot make it pristine — surface the gap rather than restore silently.
                if (!File.Exists(target))
                {
                    unrestorable.Add(new UnrestorableFile(relativePath, "no baseline snapshot and the file is missing"));
                }

                continue;
            }

            if (File.Exists(target) && BytesEqual(baseline, target))
            {
                continue; // already pristine
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(baseline, target, overwrite: true);
            restored.Add(relativePath);
        }

        return new RestoreOutcome(restored, unrestorable);
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

/// <summary>The result of a <see cref="CapturedFileStore.Restore"/> batch.</summary>
internal readonly record struct RestoreOutcome(
    IReadOnlyList<string> Restored,
    IReadOnlyList<UnrestorableFile> Unrestorable);

/// <summary>A captured file whose baseline could NOT be restored, with the reason (issue #51 FIX D).</summary>
internal readonly record struct UnrestorableFile(string RelativePath, string Reason);
