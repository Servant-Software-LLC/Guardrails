// A COMPLETE, representative CORRECT artifact for 03-pinned-fold-lands-beside-the-disk-form.ps1
// (#468/#302): the wave hasher after stage 9. Two functions - the shipped disk-reading one, unchanged
// and still recomputing per task, and a pinned sibling that folds the load-time captures with the SAME
// framing. Kept complete rather than a fragment; this header names none of the tokens the clauses key on.
using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// The wave-level definition hash. TWO forms, deliberately (plan 32 section 5.4): the DISK form for every
/// READ - the wave-drift compare, the wave-proceed answer key, ReviewMarker's key hash - and the PINNED
/// form for the single WRITE at wave completion. They must produce the same bytes on an unedited tree.
/// </summary>
public static class WaveDefinitionHash
{
    private const char Unit = '\u001F';
    private const char Record = '\u001E';

    /// <summary>
    /// The DISK form. UNCHANGED by plan 32: every READ site depends on it recomputing from the bytes on
    /// disk right now, which is what makes the wave-drift comparison mean anything.
    /// </summary>
    public static string Compute(WaveNode wave)
    {
        var builder = new StringBuilder();
        foreach (TaskNode task in OrderedTasks(wave))
        {
            builder.Append("task:").Append(WaveRelativeId(wave, task)).Append(Unit);
            builder.Append(TaskDefinitionHash.Compute(task)).Append(Record);
        }

        AppendGateFolders(builder, wave);
        return Digest(builder);
    }

    /// <summary>
    /// The PINNED form - what write site W5 stamps at wave completion. Folds each constituent task's
    /// load-time capture and the wave's own, in the SAME order with the SAME labels and separators as the
    /// disk form above, so an unedited tree produces byte-identical output and no completed wave reads as
    /// drifted on the next resume. A null capture folds as null: there is no fallback to disk here.
    /// </summary>
    internal static string ComputeFromPins(WaveNode wave)
    {
        var builder = new StringBuilder();
        foreach (TaskNode task in OrderedTasks(wave))
        {
            builder.Append("task:").Append(WaveRelativeId(wave, task)).Append(Unit);
            builder.Append(task.DefinitionHashAtLoad).Append(Record);
        }

        builder.Append("gates:").Append(Unit).Append(wave.DefinitionHashAtLoad).Append(Record);
        return Digest(builder);
    }

    /// <summary>The wave's own gate folders and brief, as captured at WaveNode construction.</summary>
    internal static string ComputeGateSurface(WaveNode wave)
    {
        var builder = new StringBuilder();
        AppendGateFolders(builder, wave);
        return Digest(builder);
    }

    private static void AppendGateFolders(StringBuilder builder, WaveNode wave)
    {
        HashText.AppendFolder(builder, wave.Directory, "guardrails");
        HashText.AppendFolder(builder, wave.Directory, "preflights");
        HashText.AppendFile(builder, WaveNode.BriefFileName, Path.Combine(wave.Directory, WaveNode.BriefFileName));
    }

    private static IEnumerable<TaskNode> OrderedTasks(WaveNode wave) =>
        wave.Tasks.OrderBy(t => WaveRelativeId(wave, t), StringComparer.Ordinal);

    private static string WaveRelativeId(WaveNode wave, TaskNode task) =>
        task.Id.StartsWith(wave.Dir + "/", StringComparison.Ordinal) ? task.Id[(wave.Dir.Length + 1)..] : task.Id;

    private static string Digest(StringBuilder builder) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
}
