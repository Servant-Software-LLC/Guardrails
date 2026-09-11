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
/// <b>STUB (task 05-author-tests-provenance): no behaviour.</b> Every member currently throws
/// <see cref="NotImplementedException"/> unconditionally, so any test that constructs or reads a
/// <see cref="SuppliedRecord"/> is expected to FAIL against this tree. Filled in by
/// <c>06-implement-provenance</c>.
/// </para>
/// </summary>
public sealed record SuppliedRecord
{
    /// <summary>UTC time the supply happened (ISO-8601).</summary>
    public required DateTimeOffset At
    {
        get => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
        init => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
    }

    /// <summary>The commit sha the supply landed as.</summary>
    public required string Commit
    {
        get => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
        init => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
    }

    /// <summary>The workspace-relative paths supplied, in the order given.</summary>
    public required IReadOnlyList<string> Paths
    {
        get => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
        init => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
    }

    /// <summary>Total bytes written across <see cref="Paths"/>.</summary>
    public required long Bytes
    {
        get => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
        init => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
    }

    /// <summary>
    /// WHO supplied — <c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c> (design 40 §4,
    /// the fifth field added by review). The commit trailer's <c>Supplied-By: &lt;by&gt;</c> is derived
    /// from this value rather than a hard-coded constant, and §3's overwatcher auto-resolve at
    /// <c>dial:critical</c> is unimplementable without it.
    /// </summary>
    public required string By
    {
        get => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
        init => throw new NotImplementedException("SuppliedRecord is a stub (design 40 §4); implemented by 06-implement-provenance.");
    }
}
