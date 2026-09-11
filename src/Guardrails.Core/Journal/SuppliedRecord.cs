namespace Guardrails.Core.Journal;

/// <summary>
/// One entry in <c>run.json</c>'s <c>supplied[]</c> (design 40 §4) — provenance for a file an operator
/// (or another authorized supplier) injected into a running plan's checkout via <c>guardrails supply</c>,
/// so the run's own output is never confusable with what was handed to it.
/// <para>
/// <b>The FIVE fields §4 names</b>, per the review that added the fifth: <c>at</c>, <c>commit</c>,
/// <c>paths</c>, <c>bytes</c>, and <c>by</c> (<c>operator</c> | <c>overwatcher</c> | <c>task:&lt;folder&gt;</c>).
/// <c>By</c> is a plain string rather than a fixed enum because the third case is a template embedding
/// the calling task's own folder name, which no closed set of enum members can name in advance.
/// </para>
/// <para>
/// Plain auto-implemented properties — <see cref="JournalJson"/> is reflection-based (no source-gen
/// context to register against) and every field here is a primitive or a list of strings, so the default
/// <c>System.Text.Json</c> reflection reader/writer round-trips it in both directions without a custom
/// converter. Appended to <see cref="JournalDocument.Supplied"/> by <see cref="RunJournal.RecordSupplied"/>.
/// </para>
/// </summary>
public sealed record SuppliedRecord
{
    /// <summary>UTC time the supply happened (ISO-8601).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The commit sha the supply landed as.</summary>
    public required string Commit { get; init; }

    /// <summary>The workspace-relative paths supplied, in the order given.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>Total bytes written across <see cref="Paths"/>.</summary>
    public required long Bytes { get; init; }

    /// <summary>
    /// WHO supplied — <c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c> (design 40 §4,
    /// the fifth field added by review). The commit trailer's <c>Supplied-By: &lt;by&gt;</c> is derived
    /// from this value rather than a hard-coded constant, and §3's overwatcher auto-resolve at
    /// <c>dial:critical</c> is unimplementable without it.
    /// </summary>
    public required string By { get; init; }
}
