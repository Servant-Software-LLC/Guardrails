using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// Computes one wave's <c>WaveDefinitionHash</c> (SSOT §7.2/§7.3/§14.5, issue #254): a SHA-256 that sits
/// BETWEEN <see cref="PlanDefinitionHash"/> and <see cref="TaskDefinitionHash"/> in the nesting
/// (<c>PlanDefinitionHash ⊇ WaveDefinitionHash ⊇ TaskDefinitionHash</c>). It FOLDS, in order:
/// <list type="number">
///   <item>each constituent task's <see cref="TaskDefinitionHash"/> VALUE (in wave-relative task-folder
///     ordinal order) — folding the child hash, NOT re-reading the task files, so the wave hash changes
///     iff a constituent task hash changes; the levels cannot drift apart;</item>
///   <item>every file under the wave's <c>guardrails/**</c> (exit/terminal gate), recursive, sorted,
///     newline-normalized;</item>
///   <item>every file under the wave's <c>preflights/**</c> (entry gate), recursive, sorted, newline-normalized;</item>
///   <item>the wave's OPTIONAL human-authored <c>brief.md</c> (SSOT §14.10, #360), folded ONLY when present
///     — a changed / added / removed brief on a COMPLETED wave is legitimate drift (the wave was broken
///     down against a different intent and may need re-breaking). It is EXCLUDED from
///     <see cref="PlanDefinitionHash"/> (breakdown INPUT, not reviewed output). Appending it only when the
///     file exists keeps a briefless wave's hash identical to before the convention existed.</item>
/// </list>
///
/// <para>The shared <c>guardrails.json</c> is DELIBERATELY EXCLUDED (Open Decision C, SSOT §7.2): a
/// config edit must not re-stale every already-run upstream wave. Same discipline as the other plan-hash
/// family members (labeled segments, newline-normalized bytes, deterministic order, <c>sha256:</c> prefix).</para>
///
/// <para><b>TWO forms, deliberately (plan 32-executed-definition-hash §5.4, issue #556).</b>
/// <see cref="Compute(WaveNode)"/> is the DISK form and is UNCHANGED: every wave-level READ depends on it
/// recomputing from the bytes on disk right now — the wave-drift compare, the JIT checkpoint's and the
/// review gate's escalation records, the wave-proceed answer key, and
/// <see cref="Review.ReviewMarker"/>'s key hash (which is what keeps every existing marker valid, §5.5).
/// <see cref="ComputeFromPins(WaveNode)"/> is the PINNED twin, for the single WRITE at wave completion, and
/// folds the load-time captures instead. Both share <see cref="GateDefinitionOf"/> and
/// <see cref="WaveRelativeFolder"/> so their framing cannot drift apart: on an unedited tree they are
/// byte-identical, which is what lets the WRITE be pinned while the resume COMPARE stays on disk, and is
/// why this plan owes no migration wave.</para>
/// </summary>
public static class WaveDefinitionHash
{
    private const string Prefix = "sha256:";
    private const string GuardrailsDirName = "guardrails";
    private const string PreflightsDirName = "preflights";

    /// <summary>
    /// Compute the <c>sha256:</c>-prefixed definition hash for a single loaded wave, from CURRENT DISK.
    /// The READ form, and plan 32 (§5.4) leaves it exactly as it was: every wave-level read compares
    /// against the bytes that are on disk right now, which is the only thing that makes a wave-drift
    /// comparison mean anything.
    /// </summary>
    public static string Compute(WaveNode wave)
    {
        ArgumentNullException.ThrowIfNull(wave);

        var builder = new StringBuilder();

        // 1. Fold each constituent task's TaskDefinitionHash value, in wave-relative folder-name order.
        foreach (TaskNode task in wave.Tasks.OrderBy(WaveRelativeFolder, StringComparer.Ordinal))
        {
            builder.Append("task:").Append(WaveRelativeFolder(task)).Append(HashText.UnitSeparator);
            builder.Append(TaskDefinitionHash.Compute(task));
            builder.Append(HashText.RecordSeparator);
        }

        // 2-4. The wave's own gate folders and optional brief, read from CURRENT DISK.
        builder.Append(GateDefinitionOf(wave.Directory));

        return Digest(builder);
    }

    /// <summary>
    /// The PINNED twin of <see cref="Compute(WaveNode)"/> (plan 32-executed-definition-hash §5.4, issue
    /// #556) — what write site W5, the wave-completion stamp, records into <c>waves[].definitionHash</c> and
    /// into the <c>Guardrails-Wave:</c> marker commit. Folds each constituent task's
    /// <see cref="TaskNode.DefinitionHashAtLoad"/> and then the wave's own
    /// <see cref="WaveNode.DefinitionHashAtLoad"/>, so what is certified is the definition the wave actually
    /// RAN against rather than whatever is on disk at settle. Shipping only the task level would make SSOT
    /// §14.5's <i>"the wave hash changes iff a constituent task hash changes"</i> FALSE: on an edited run
    /// each task's stamped hash would describe the pre-edit bytes while the wave's described the post-edit
    /// ones, and the two levels would disagree about the same tasks in the same journal.
    ///
    /// <para><b>Byte-identical to <see cref="Compute(WaveNode)"/> on an unedited tree, and that is a
    /// requirement rather than a nicety.</b> Only the WRITE is pinned; the wave-drift COMPARE on the next
    /// resume still recomputes from disk. Any framing difference — a different label, a different separator,
    /// a different order, or a digest folded where the disk form folds file BODIES — would make every
    /// completed wave read as drifted on the very next resume, which under the default policy is an
    /// unauthorized wave-drift halt. That is why the two forms share
    /// <see cref="WaveRelativeFolder"/> and <see cref="GateDefinitionOf"/> rather than restating them, and
    /// why <see cref="WaveNode.DefinitionHashAtLoad"/> holds the gate segment's TEXT.</para>
    ///
    /// <para><b>No fallback to disk.</b> A null capture folds as nothing, exactly as a null task pin records
    /// a null hash at the task level (§5.2): a <c>?? Compute(…)</c> tail would be indistinguishable in
    /// production — where the loader is the only constructor — while silently restoring the defect for any
    /// node built another way.</para>
    /// </summary>
    internal static string ComputeFromPins(WaveNode wave)
    {
        ArgumentNullException.ThrowIfNull(wave);

        var builder = new StringBuilder();

        // 1. The same framing as the disk form's task fold — same label, same separators, same order —
        //    over the LOAD-TIME pin instead of a settle-time recompute.
        foreach (TaskNode task in wave.Tasks.OrderBy(WaveRelativeFolder, StringComparer.Ordinal))
        {
            builder.Append("task:").Append(WaveRelativeFolder(task)).Append(HashText.UnitSeparator);
            builder.Append(task.DefinitionHashAtLoad);
            builder.Append(HashText.RecordSeparator);
        }

        // 2-4. The wave's own gate folders and optional brief, as the loader captured them — the same
        //      labeled segments GateDefinitionOf produces, held verbatim rather than re-walked.
        builder.Append(wave.DefinitionHashAtLoad);

        return Digest(builder);
    }

    /// <summary>
    /// Segments 2-4 of the fold as raw labeled-segment TEXT: the wave-level exit/terminal-gate folder
    /// (<c>guardrails/**</c>), then the wave-level entry-preflight folder (<c>preflights/**</c>), then the
    /// OPTIONAL human-authored wave brief (SSOT §14.10, #360) — folded ONLY when present, so a briefless
    /// wave's hash is unchanged from before this convention existed; adding, editing, or removing a
    /// <c>brief.md</c> each moves the hash (drift on a completed wave). EXCLUDED from
    /// <see cref="PlanDefinitionHash"/> (breakdown INPUT, not reviewed output).
    ///
    /// <para>Returned as TEXT rather than as a digest because it is what the loader captures into
    /// <see cref="WaveNode.DefinitionHashAtLoad"/>: <see cref="ComputeFromPins(WaveNode)"/> appends that
    /// capture into the very position <see cref="Compute(WaveNode)"/> fills with these bytes, so the two
    /// forms agree byte-for-byte on an unedited tree (§5.4). Takes the directory rather than the node so the
    /// loader can capture it at the single <c>new WaveNode</c> expression.</para>
    /// </summary>
    internal static string GateDefinitionOf(string waveDirectory)
    {
        var builder = new StringBuilder();

        AppendFolder(builder, waveDirectory, GuardrailsDirName);
        AppendFolder(builder, waveDirectory, PreflightsDirName);

        string briefPath = Path.Combine(waveDirectory, WaveNode.BriefFileName);
        if (File.Exists(briefPath))
        {
            HashText.AppendFile(builder, WaveNode.BriefFileName, briefPath);
        }

        return builder.ToString();
    }

    /// <summary>SHA-256 over the builder's UTF-8 bytes, in the family's <c>sha256:</c>+lowercase-hex framing.</summary>
    private static string Digest(StringBuilder builder)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendFolder(StringBuilder builder, string waveDirectory, string folderName)
    {
        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(waveDirectory, Path.Combine(waveDirectory, folderName)))
        {
            HashText.AppendFile(builder, file.Label, file.AbsolutePath);
        }
    }

    /// <summary>The task's wave-relative folder name (the segment of its wave-qualified id after the wave dir).</summary>
    private static string WaveRelativeFolder(TaskNode task) =>
        task.WaveDir is { } wave && task.Id.StartsWith(wave + "/", StringComparison.Ordinal)
            ? task.Id[(wave.Length + 1)..]
            : task.Id;
}
