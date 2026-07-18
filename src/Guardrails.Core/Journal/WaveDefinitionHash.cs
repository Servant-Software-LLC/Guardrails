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
/// </summary>
public static class WaveDefinitionHash
{
    private const string Prefix = "sha256:";
    private const string GuardrailsDirName = "guardrails";
    private const string PreflightsDirName = "preflights";

    /// <summary>Compute the <c>sha256:</c>-prefixed definition hash for a single loaded wave.</summary>
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

        // 2. Wave-level exit/terminal-gate folder, then 3. wave-level entry-preflight folder.
        AppendFolder(builder, wave.Directory, GuardrailsDirName);
        AppendFolder(builder, wave.Directory, PreflightsDirName);

        // 4. The OPTIONAL human-authored wave brief (SSOT §14.10, #360): folded ONLY when present so a
        //    briefless wave's hash is unchanged from before this convention existed; adding, editing, or
        //    removing a brief.md each moves the hash (drift on a completed wave). EXCLUDED from
        //    PlanDefinitionHash (breakdown INPUT, not reviewed output).
        string briefPath = Path.Combine(wave.Directory, WaveNode.BriefFileName);
        if (File.Exists(briefPath))
        {
            HashText.AppendFile(builder, WaveNode.BriefFileName, briefPath);
        }

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
