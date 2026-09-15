namespace Guardrails.Core.Journal;

/// <summary>
/// The ONE reader that answers "what is in this tree that no task authored?" (design 39 §1c "How a refresh
/// is recorded"). Reads BOTH <see cref="JournalDocument.Supplied"/> (design 40 §4) and
/// <see cref="JournalDocument.Refreshed"/> (design 39 §1c/§5) — a consumer that reads only one of them is
/// the defect this type exists to prevent.
/// <para>
/// <c>Scheduler.BuildGateHalt</c> appends <see cref="HeadlineSuffix"/> to a wave entry/exit gate halt's
/// headline (§5), so console, journal and log site all carry the same disclosure with no <c>RunHalt</c>
/// schema change. A run with neither section renders byte-identically to today.
/// </para>
/// <para>
/// STUB (task 24): both members throw <see cref="NotImplementedException"/> until task 25 implements this
/// reader for real.
/// </para>
/// </summary>
public static class UnauthoredContentNote
{
    /// <summary>
    /// The halt-headline suffix naming every unauthored-content record, oldest first across both sections —
    /// a refresh as <c>refresh from '&lt;from&gt;' at &lt;upstream, 10 chars&gt;</c>, a supply as
    /// <c>supplied by &lt;by&gt; at &lt;commit, 10 chars&gt;</c> — or <c>null</c> when neither section has an
    /// entry, so a run with no unauthored content keeps a byte-identical halt headline.
    /// </summary>
    public static string? HeadlineSuffix(JournalDocument document) => throw new NotImplementedException();

    /// <summary>One detail line per record across both sections, oldest first.</summary>
    public static IReadOnlyList<string> DetailLines(JournalDocument document) => throw new NotImplementedException();
}
