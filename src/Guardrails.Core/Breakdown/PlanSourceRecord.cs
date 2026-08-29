namespace Guardrails.Core.Breakdown;

/// <summary>
/// Provenance captured from the source <c>plan.md</c> at breakdown time (issue #505, plan-of-record
/// <c>docs/plans/24-plan-source-provenance.md</c> §3). Written to <c>&lt;plan&gt;/state/plan-source.json</c>
/// — deliberately under <c>state/</c>, which every hash in <see cref="Journal.PlanHash"/> and
/// <see cref="Journal.PlanDefinitionHash"/> excludes, so recording this can never de-attest an
/// already-reviewed plan (GR2025).
/// </summary>
public sealed record PlanSourceRecord
{
    /// <summary>The plan-relative path of the source markdown that was captured.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The byte length actually read from <see cref="SourcePath"/>.</summary>
    public required int SourceBytes { get; init; }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the file's raw bytes, exactly as read — never decoded text, so a
    /// UTF-8 BOM or any other encoding round-trip changes it.
    /// </summary>
    public required string SourceSha256 { get; init; }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the same bytes with CRLF and a lone CR normalized to LF, so a
    /// line-ending-only checkout difference (e.g. <c>core.autocrlf</c>) does not read as tampering.
    /// </summary>
    public required string SourceSha256Lf { get; init; }

    /// <summary>
    /// The integer from a <c>DECISIONS DELEGATED TO YOU: (\d+)**</c> line, or 0 when the line is absent
    /// — Charter emits the line whenever the count is &gt;= 1 and never when it is 0.
    /// </summary>
    public required int DeclaredDelegatedDecisions { get; init; }

    /// <summary>
    /// An OPEN map of every <c>&lt;!-- charter: key=value --&gt;</c> comment found, keyed by <c>key</c>.
    /// Empty (never null) when the plan carries none.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Stamps { get; init; }

    /// <summary>
    /// Keys for which more than one <c>charter:</c> comment was found. <see cref="Stamps"/> keeps the
    /// FIRST value seen for such a key; this reports that a duplicate existed at all.
    /// </summary>
    public required IReadOnlyList<string> DuplicateStampKeys { get; init; }

    /// <summary>Read <paramref name="planPath"/> and capture its provenance.</summary>
    public static PlanSourceRecord Capture(string planPath) => throw new NotImplementedException();

    /// <summary>
    /// Serialize this record as JSON and write it to <c>&lt;planDirectory&gt;/state/plan-source.json</c>.
    /// Lives under <c>state/</c> so no hash in <see cref="Journal.PlanHash"/> or
    /// <see cref="Journal.PlanDefinitionHash"/> ever walks it.
    /// </summary>
    public void WriteTo(string planDirectory) => throw new NotImplementedException();
}
