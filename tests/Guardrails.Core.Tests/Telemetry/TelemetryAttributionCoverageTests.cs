using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The counting pass behind <c>telemetry report</c>'s attribution-coverage block (issue #619).
///
/// <para>These are the DENOMINATOR tests. The rendering is asserted separately in
/// <c>TelemetryAttributionCoverageReportTests</c>; what matters here is that a row lands in exactly one
/// bucket and that the buckets a coverage figure divides by are the ones that could ever have named a
/// model. Getting that wrong is not a cosmetic error — it is how "76% of rows name no usable model"
/// became a headline when 77% of it was correct by construction.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryAttributionCoverageTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static TelemetryRow Row(string? attribution, int attempt = 1) => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "run-1",
        TaskId = "01-example",
        Attempt = attempt,
        StartedAt = At,
        EndedAt = At.AddMinutes(1),
        Outcome = "succeeded",
        Repo = "gr619-test-repo",
        ModelAttribution = attribution
    };

    /// <summary>
    /// The denominator excludes exactly the rows that were never going to name a model. This is the
    /// finding of #577 expressed as an assertion: with 2 attributable rows sitting among 300 that are
    /// correct by construction, coverage is 50% — not 0.7%.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void TheDenominatorCountsOnlyRowsThatCouldHaveNamedAModel()
    {
        var rows = new List<TelemetryRow> { Row(ModelAttribution.Recorded), Row(ModelAttribution.NotRecorded) };
        rows.AddRange(Enumerable.Range(0, 250).Select(_ => Row(ModelAttribution.TaskGrain, attempt: 0)));
        rows.AddRange(Enumerable.Range(0, 50).Select(_ => Row(ModelAttribution.ScriptAction)));

        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(rows);

        Assert.Equal(2, coverage.Attributable);
        Assert.Equal(0.5, coverage.ComparableShare);
        Assert.Equal(302, coverage.TotalRows);
    }

    /// <summary>
    /// <c>cli-default</c> is attributable but NOT comparable. If it ever pooled with <c>recorded</c> the
    /// coverage figure would claim a model identity for rows whose model nobody recorded — the precise
    /// flattering-number trap the token exists to prevent.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void CliDefaultIsInTheDenominatorButIsNotCountedAsComparable()
    {
        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(
        [
            Row(ModelAttribution.Recorded),
            Row(ModelAttribution.CliDefault),
            Row(ModelAttribution.CliDefault),
            Row(ModelAttribution.CliDefault)
        ]);

        Assert.Equal(4, coverage.Attributable);
        Assert.Equal(1, coverage.Recorded);
        Assert.Equal(3, coverage.CliDefault);
        Assert.Equal(0.25, coverage.ComparableShare);
    }

    /// <summary>
    /// A null attribution (a row written before <c>schemaVersion 3</c>) is UNKNOWABLE; the <c>unknown</c>
    /// token means a current writer looked and could not decide. Collapsing the two would lose the one
    /// distinction that lets a reader tell the pre-repair era from a live classification failure — and
    /// would put un-backfillable history into a bucket that reads like a fixable defect.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void APreColumnNullIsNotTheUnknownToken()
    {
        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(
            [Row(attribution: null), Row(ModelAttribution.Unknown)]);

        Assert.Equal(1, coverage.PreColumn);
        Assert.Equal(1, coverage.Unknown);

        // Neither is attributable: one could not be classified, the other predates classification.
        Assert.Equal(0, coverage.Attributable);
    }

    /// <summary>
    /// The corpus is append-only and never rewritten, so a newer harness writing a token this build
    /// predates is a real possibility. SSOT §15.4: record it verbatim rather than folding it into a
    /// neighbour — folding into <c>unknown</c> would understate a future vocabulary, and folding into the
    /// denominator would let an unrecognised token move a coverage figure nobody could explain.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AnUnrecognizedTokenIsRecordedVerbatimAndStaysOutOfTheDenominator()
    {
        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(
            [Row(ModelAttribution.Recorded), Row("some-future-token"), Row("some-future-token")]);

        Assert.Equal(1, coverage.Attributable);
        Assert.Equal(1.0, coverage.ComparableShare);
        Assert.Equal(2, coverage.UnrecognizedTotal);
        Assert.Equal(2, coverage.Unrecognized["some-future-token"]);
        Assert.Equal(3, coverage.TotalRows);
    }

    /// <summary>
    /// Null, not 0.0. A corpus of nothing but script actions has no coverage to report, and rendering 0%
    /// would assert a total failure of attribution where the truth is the question does not yet apply.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void NothingAttributableYieldsNoShareRatherThanZeroPercent()
    {
        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(
            [Row(ModelAttribution.ScriptAction), Row(ModelAttribution.TaskGrain, attempt: 0)]);

        Assert.Equal(0, coverage.Attributable);
        Assert.Null(coverage.ComparableShare);
    }

    /// <summary>
    /// Every bucket sums to the input count, so a reader can always reconcile the rendered block against
    /// the row total. Without this, a category could be silently dropped and the block would still look
    /// internally consistent — the exact shape of failure this whole column exists to break.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void EveryRowLandsInExactlyOneBucket()
    {
        var rows = new List<TelemetryRow>
        {
            Row(ModelAttribution.Recorded),
            Row(ModelAttribution.CliDefault),
            Row(ModelAttribution.NotRecorded),
            Row(ModelAttribution.ScriptAction),
            Row(ModelAttribution.TaskGrain, attempt: 0),
            Row(ModelAttribution.Unknown),
            Row(attribution: null),
            Row("some-future-token")
        };

        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute(rows);

        Assert.Equal(rows.Count, coverage.TotalRows);
        Assert.Equal(1, coverage.Recorded);
        Assert.Equal(1, coverage.CliDefault);
        Assert.Equal(1, coverage.NotRecorded);
        Assert.Equal(1, coverage.ScriptAction);
        Assert.Equal(1, coverage.TaskGrain);
        Assert.Equal(1, coverage.Unknown);
        Assert.Equal(1, coverage.PreColumn);
        Assert.Equal(1, coverage.UnrecognizedTotal);
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AnEmptyCorpusIsCountedRatherThanRefused()
    {
        AttributionCoverage coverage = TelemetryAttributionCoverage.Compute([]);

        Assert.Equal(0, coverage.TotalRows);
        Assert.Null(coverage.ComparableShare);
        Assert.Empty(coverage.Unrecognized);
    }
}
