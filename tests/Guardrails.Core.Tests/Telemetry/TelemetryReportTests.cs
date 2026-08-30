using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The stratified corpus report (charter §5 "the honesty rules", <c>model-evidence-and-graduation</c>
/// #535) — the surface the whole plan exists to produce, and the one where a rosier-than-justified
/// number is invisible. Six behaviours, each pinned to an exact method name the red-census guardrail
/// binds to, and each one IS a §5 honesty rule rather than an assertion about one particular sample.
///
/// <para><b>TDD red.</b> Every test here calls <see cref="TelemetryReport.Build"/>, which throws
/// <see cref="NotImplementedException"/> until <c>08-implement-corpus-report</c> fills it — so the whole
/// file is red, and none of it can be green by coincidence with a stub's default.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryReportTests
{
    // --- 1. stratified by model, tier and fingerprint bucket -----------------------------------------

    /// <summary>
    /// Charter §5's "big one": models are routed BY DECLARED TIER, so a per-model figure that is not
    /// also split by tier and fingerprint bucket compares the weak model's easy work against the strong
    /// model's hard work. The SAME model fingerprint appears under two tiers (100% vs 0%) and under two
    /// buckets within one tier (100% vs 0%) — an implementation that groups on any subset of the three
    /// keys blends one of these pairs into a number neither stratum actually earned.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_StratifiesByModelAndTierAndBucket()
    {
        List<TelemetryReportSample> samples =
        [
            .. Samples(5, "weak", "fp-weak", "easy", "std", firstAttemptSucceeded: true),    // weak/easy/std -> 100%
            .. Samples(5, "weak", "fp-weak", "hard", "std", firstAttemptSucceeded: false),    // weak/hard/std -> 0%
            .. Samples(5, "strong", "fp-strong", "hard", "std", firstAttemptSucceeded: true), // strong/hard/std -> 100%
            .. Samples(5, "weak", "fp-weak", "easy", "alt", firstAttemptSucceeded: false)     // weak/easy/alt -> 0%
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        Assert.Equal(4, report.Rows.Count);
        AssertPassRate(report, "fp-weak", "easy", "std", 1.0);
        AssertPassRate(report, "fp-weak", "hard", "std", 0.0);
        AssertPassRate(report, "fp-strong", "hard", "std", 1.0);
        AssertPassRate(report, "fp-weak", "easy", "alt", 0.0);
    }

    // --- 2. below minimum n renders insufficient evidence ---------------------------------------------

    /// <summary>
    /// A stratum one sample short of the floor renders <see cref="InsufficientEvidenceReportRow"/> —
    /// which carries no verdict property at all, so there is nothing here a caller could misread as an
    /// earned number. A sibling stratum exactly AT the floor renders a real verdict, pinning the boundary.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_BelowMinimumSample_RendersInsufficientEvidence()
    {
        int min = TelemetryReport.DefaultMinimumSampleSize;

        List<TelemetryReportSample> samples =
        [
            .. Samples(min - 1, "m1", "fp-below", "easy", "std", firstAttemptSucceeded: true),
            .. Samples(min, "m2", "fp-at-floor", "easy", "std", firstAttemptSucceeded: true)
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        InsufficientEvidenceReportRow belowRow = Assert.IsType<InsufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-below"));
        Assert.Equal(min - 1, belowRow.SampleSize);

        SufficientEvidenceReportRow atFloorRow = Assert.IsType<SufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-at-floor"));
        Assert.Equal(min, atFloorRow.SampleSize);
        Assert.Equal(1.0, atFloorRow.FirstAttemptPassRate, precision: 6);
    }

    // --- 3. attempts-to-green never without abandonment rate -----------------------------------------

    /// <summary>
    /// Charter §5's survivorship warning: averaging attempts-to-green over successes only flatters
    /// exactly the model that gives up. Two of five samples never went green, so an implementation that
    /// scopes abandonment rate over the "attempted green" subset instead of the whole stratum would
    /// report 2/3 here, not the correct 2/5 — and <see cref="SufficientEvidenceReportRow.AttemptsToGreen"/>
    /// is a single required object, so there is no accessor that returns median/p90 without it.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_AttemptsToGreen_AlwaysAccompaniedByAbandonmentRate()
    {
        List<TelemetryReportSample> samples =
        [
            Sample("m1", "fp-m1", "medium", "std", firstAttemptSucceeded: true, attemptsToGreen: 1),
            Sample("m1", "fp-m1", "medium", "std", firstAttemptSucceeded: false, attemptsToGreen: 2),
            Sample("m1", "fp-m1", "medium", "std", firstAttemptSucceeded: false, attemptsToGreen: 3),
            Sample("m1", "fp-m1", "medium", "std", firstAttemptSucceeded: false, attemptsToGreen: null),
            Sample("m1", "fp-m1", "medium", "std", firstAttemptSucceeded: false, attemptsToGreen: null)
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        SufficientEvidenceReportRow row = Assert.IsType<SufficientEvidenceReportRow>(Assert.Single(report.Rows));
        Assert.Equal(5, row.SampleSize);

        TelemetryAttemptsToGreen attemptsToGreen = row.AttemptsToGreen;
        Assert.Equal(0.4, attemptsToGreen.AbandonmentRate, precision: 6);
        Assert.Equal(2.0, attemptsToGreen.MedianAttempts, precision: 6);
        Assert.InRange(attemptsToGreen.P90Attempts, attemptsToGreen.MedianAttempts, 3.0);
    }

    // --- 4. every row carries its sample size ---------------------------------------------------------

    /// <summary>
    /// <c>n</c> is on the abstract base row, present whether or not the stratum cleared the evidence
    /// floor — an insufficient-evidence stratum still tells a reader exactly how far short it fell.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_EveryRowCarriesItsSampleSize()
    {
        int min = TelemetryReport.DefaultMinimumSampleSize;

        List<TelemetryReportSample> samples =
        [
            .. Samples(min - 2, "m1", "fp-insufficient", "easy", "std", firstAttemptSucceeded: true),
            .. Samples(min + 3, "m2", "fp-sufficient", "hard", "std", firstAttemptSucceeded: true)
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, row => Assert.True(row.SampleSize > 0));
        Assert.Equal(min - 2, Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-insufficient").SampleSize);
        Assert.Equal(min + 3, Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-sufficient").SampleSize);
    }

    // --- 5. a costless provider reports no money, not zero ---------------------------------------------

    /// <summary>
    /// A stratum where no sample ever reported a cost (a local provider) renders <c>null</c>, never
    /// <c>0</c> — paired with a sibling stratum whose samples DID report a cost, so a hard-coded null and
    /// a correct nullable pass-through disagree.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_CostlessProvider_ReportsNoMoney_NotZero()
    {
        int min = TelemetryReport.DefaultMinimumSampleSize;

        List<TelemetryReportSample> samples =
        [
            .. Samples(min, "local-model", "fp-local", "easy", "std", firstAttemptSucceeded: true, costUsd: null),
            .. Samples(min, "hosted-model", "fp-hosted", "easy", "std", firstAttemptSucceeded: true, costUsd: 0.02m)
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        SufficientEvidenceReportRow costless = Assert.IsType<SufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-local"));
        Assert.Null(costless.CostUsd);

        SufficientEvidenceReportRow hosted = Assert.IsType<SufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "fp-hosted"));
        Assert.NotNull(hosted.CostUsd);
    }

    // --- 6. different model fingerprints are never pooled -----------------------------------------------

    /// <summary>
    /// Charter §5 "model drift": an <c>ollama pull</c> can silently replace a model under a stable tag.
    /// Both sample sets share the identical DISPLAY tag <c>llama3:latest</c> but carry different resolved
    /// fingerprints — grouping on the tag instead of the fingerprint would blend a 100%-pass stratum and
    /// a 0%-pass stratum into one misleading 50% figure.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Report_DifferentModelFingerprints_AreNeverPooled()
    {
        List<TelemetryReportSample> samples =
        [
            .. Samples(5, "llama3:latest", "sha256:aaaa", "easy", "std", firstAttemptSucceeded: true),
            .. Samples(5, "llama3:latest", "sha256:bbbb", "easy", "std", firstAttemptSucceeded: false)
        ];

        TelemetryReport report = TelemetryReport.Build(samples);

        Assert.Equal(2, report.Rows.Count);

        SufficientEvidenceReportRow oldDigest = Assert.IsType<SufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "sha256:aaaa"));
        Assert.Equal(1.0, oldDigest.FirstAttemptPassRate, precision: 6);

        SufficientEvidenceReportRow newDigest = Assert.IsType<SufficientEvidenceReportRow>(
            Assert.Single(report.Rows, r => r.ModelFingerprint == "sha256:bbbb"));
        Assert.Equal(0.0, newDigest.FirstAttemptPassRate, precision: 6);
    }

    // --- fixtures ----------------------------------------------------------------------------------

    private static TelemetryReportSample Sample(
        string model,
        string modelFingerprint,
        string tier,
        string fingerprintBucket,
        bool firstAttemptSucceeded,
        int? attemptsToGreen = null,
        decimal? costUsd = null) =>
        new()
        {
            Model = model,
            ModelFingerprint = modelFingerprint,
            Tier = tier,
            FingerprintBucket = fingerprintBucket,
            FirstAttemptSucceeded = firstAttemptSucceeded,
            AttemptsToGreen = attemptsToGreen,
            CostUsd = costUsd
        };

    private static IEnumerable<TelemetryReportSample> Samples(
        int count,
        string model,
        string modelFingerprint,
        string tier,
        string fingerprintBucket,
        bool firstAttemptSucceeded,
        int? attemptsToGreen = null,
        decimal? costUsd = null) =>
        Enumerable.Range(0, count).Select(_ =>
            Sample(model, modelFingerprint, tier, fingerprintBucket, firstAttemptSucceeded, attemptsToGreen, costUsd));

    // --- assertions ----------------------------------------------------------------------------------

    private static void AssertPassRate(TelemetryReport report, string modelFingerprint, string tier, string fingerprintBucket, double expected)
    {
        TelemetryReportRow row = Assert.Single(report.Rows, r =>
            r.ModelFingerprint == modelFingerprint && r.Tier == tier && r.FingerprintBucket == fingerprintBucket);
        SufficientEvidenceReportRow sufficient = Assert.IsType<SufficientEvidenceReportRow>(row);
        Assert.Equal(expected, sufficient.FirstAttemptPassRate, precision: 6);
    }
}
