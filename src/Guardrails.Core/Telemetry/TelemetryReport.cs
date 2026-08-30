namespace Guardrails.Core.Telemetry;

/// <summary>
/// The stratified corpus report (charter §5 "the honesty rules", §6 "the metrics",
/// <c>model-evidence-and-graduation</c> #535, task 07/08) — the surface the whole plan exists to
/// produce, and the one where a rosier-than-justified number is invisible. A plain data structure
/// (<see cref="TelemetryReport"/> and its rows) plus one deterministic formatter
/// (<see cref="Build"/>); text rendering for the console is the CLI task's concern, not this one's.
///
/// <para><b>Input is NOT the raw corpus row.</b> <see cref="TelemetryRow"/> (task 01's frozen schema) has
/// no dedicated fields for a model's true identity or a task's fingerprint bucket, and this task's write
/// scope excludes that file. <see cref="TelemetryReportSample"/> is therefore a SEPARATE, already-resolved
/// fact: whatever maps a run's corpus rows onto samples is responsible for supplying
/// <see cref="TelemetryReportSample.ModelFingerprint"/> and <see cref="TelemetryReportSample.FingerprintBucket"/>
/// pre-computed. <see cref="Build"/> only groups and aggregates what it is given — it never derives a
/// bucket from a task's name or any other text (charter §4.2: a task fingerprint is a fact about the
/// task, not an opinion read off its label).</para>
///
/// <para><b>Stratification is the grouping key, not a display convenience.</b> Charter §5's "big one":
/// models are routed BY DECLARED TIER, so a per-model figure that is not also split by tier and
/// fingerprint bucket compares the weak model's easy work against the strong model's hard work. Rows
/// group by the exact triple (<see cref="TelemetryReportSample.ModelFingerprint"/>,
/// <see cref="TelemetryReportSample.Tier"/>, <see cref="TelemetryReportSample.FingerprintBucket"/>) —
/// there is no code path that produces a figure pooled across any one of those three axes.</para>
///
/// <para><b>Model fingerprint, not model tag.</b> <see cref="TelemetryReportSample.Model"/> is the
/// display tag a human reads (e.g. <c>llama3:latest</c>); <see cref="TelemetryReportSample.ModelFingerprint"/>
/// is the identity the report actually groups on. Charter §5 "model drift": an <c>ollama pull</c> can
/// silently replace a model under a stable tag, so two samples can carry the SAME <c>Model</c> string and
/// still be different models — pooling on the tag would hide exactly the swap the corpus exists to catch.</para>
///
/// <para><b>"Insufficient evidence" is a value, not a missing value.</b> A stratum below <see cref="Build"/>'s
/// minimum sample size renders as
/// <see cref="InsufficientEvidenceReportRow"/>, which carries <see cref="TelemetryReportRow.SampleSize"/>
/// and NOTHING else — there is no property on that type a caller could misread as an earned number.
/// Every row, sufficient or not, carries its <see cref="TelemetryReportRow.SampleSize"/>: charter §5's
/// non-determinism rule ("a single data point is never evidence") only works if <c>n</c> travels with the
/// figure, not just with the rows that cleared the bar.</para>
///
/// <para><b>Attempts-to-green is paired with abandonment in the TYPE.</b> Charter §5's survivorship
/// warning: averaging attempts-to-green over successes only flatters exactly the model that gives up on
/// hard work. <see cref="SufficientEvidenceReportRow.AttemptsToGreen"/> is a single required
/// <see cref="TelemetryAttemptsToGreen"/> object whose <see cref="TelemetryAttemptsToGreen.AbandonmentRate"/>
/// is computed over the SAME denominator (the stratum's whole <see cref="TelemetryReportRow.SampleSize"/>,
/// not just the samples that ever went green) — there is no accessor that returns median/p90 without it.</para>
///
/// <para><b>Cost is null-or-earned, never defaulted to zero.</b> <see cref="SufficientEvidenceReportRow.CostUsd"/>
/// is <c>decimal?</c>; a stratum where no sample ever reported a cost (a costless local provider) renders
/// <c>null</c>, the same null-versus-zero distinction <c>Guardrails.Core.Journal.JournalTierSpend</c>
/// already draws — reported zero and never-reported are different claims, and "$0" invites a conclusion
/// the data does not support.</para>
/// </summary>
public sealed record TelemetryReport
{
    /// <summary>
    /// The default floor below which a stratum renders <see cref="InsufficientEvidenceReportRow"/>
    /// instead of a verdict (charter §5: "minimum n before any verdict renders").
    /// </summary>
    public const int DefaultMinimumSampleSize = 5;

    /// <summary>One row per distinct (model fingerprint × tier × fingerprint bucket) stratum observed in the input.</summary>
    public required IReadOnlyList<TelemetryReportRow> Rows { get; init; }

    /// <summary>
    /// Groups <paramref name="samples"/> by (<see cref="TelemetryReportSample.ModelFingerprint"/>,
    /// <see cref="TelemetryReportSample.Tier"/>, <see cref="TelemetryReportSample.FingerprintBucket"/>)
    /// and computes charter §6's metrics per stratum — a stratum with fewer than
    /// <paramref name="minimumSampleSize"/> samples renders <see cref="InsufficientEvidenceReportRow"/>;
    /// every other stratum renders <see cref="SufficientEvidenceReportRow"/>.
    /// </summary>
    public static TelemetryReport Build(
        IReadOnlyList<TelemetryReportSample> samples,
        int minimumSampleSize = DefaultMinimumSampleSize)
    {
        List<TelemetryReportRow> rows = [];

        foreach (IGrouping<(string ModelFingerprint, string Tier, string FingerprintBucket), TelemetryReportSample> stratum
                 in samples.GroupBy(s => (s.ModelFingerprint, s.Tier, s.FingerprintBucket)))
        {
            List<TelemetryReportSample> strataSamples = stratum.ToList();
            int sampleSize = strataSamples.Count;

            rows.Add(sampleSize < minimumSampleSize
                ? new InsufficientEvidenceReportRow
                {
                    ModelFingerprint = stratum.Key.ModelFingerprint,
                    Tier = stratum.Key.Tier,
                    FingerprintBucket = stratum.Key.FingerprintBucket,
                    SampleSize = sampleSize
                }
                : BuildSufficientRow(stratum.Key, strataSamples, sampleSize));
        }

        return new TelemetryReport { Rows = rows };
    }

    /// <summary>
    /// Charter §6's metrics for one stratum that cleared the evidence floor. First-attempt pass rate and
    /// cost are summed/averaged over every sample; attempts-to-green and abandonment share the single
    /// <see cref="TelemetryAttemptsToGreen"/> denominator described on the class doc.
    /// </summary>
    private static SufficientEvidenceReportRow BuildSufficientRow(
        (string ModelFingerprint, string Tier, string FingerprintBucket) key,
        List<TelemetryReportSample> strataSamples,
        int sampleSize)
    {
        double firstAttemptPassRate = strataSamples.Count(s => s.FirstAttemptSucceeded) / (double)sampleSize;

        // Only the samples that ever went green have an attempts-to-green figure to contribute; the
        // abandonment rate below still divides by the WHOLE stratum, not this narrower count (charter §5
        // survivorship).
        List<int> attemptsAmongGreen = strataSamples
            .Where(s => s.AttemptsToGreen is not null)
            .Select(s => s.AttemptsToGreen!.Value)
            .Order()
            .ToList();

        double abandonmentRate = (sampleSize - attemptsAmongGreen.Count) / (double)sampleSize;

        // Same null-versus-zero convention as JournalTierSpend: a cost is summed only across samples that
        // actually reported one, and the stratum renders null (not 0) when NOT ONE of them did.
        decimal costUsd = 0m;
        bool anyCost = false;
        foreach (TelemetryReportSample sample in strataSamples)
        {
            if (sample.CostUsd is { } cost)
            {
                costUsd += cost;
                anyCost = true;
            }
        }

        return new SufficientEvidenceReportRow
        {
            ModelFingerprint = key.ModelFingerprint,
            Tier = key.Tier,
            FingerprintBucket = key.FingerprintBucket,
            SampleSize = sampleSize,
            FirstAttemptPassRate = firstAttemptPassRate,
            AttemptsToGreen = new TelemetryAttemptsToGreen
            {
                MedianAttempts = Median(attemptsAmongGreen),
                P90Attempts = Percentile(attemptsAmongGreen, 0.9),
                AbandonmentRate = abandonmentRate
            },
            CostUsd = anyCost ? costUsd : null
        };
    }

    /// <summary>The middle of <paramref name="sortedValues"/> (mean of the two middle values on an even count), or <c>0</c> when nothing ever went green.</summary>
    private static double Median(IReadOnlyList<int> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0.0;
        }

        int mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2.0
            : sortedValues[mid];
    }

    /// <summary>Nearest-rank percentile of <paramref name="sortedValues"/> — the value at position <c>ceil(p * n)</c>, or <c>0</c> when nothing ever went green.</summary>
    private static double Percentile(IReadOnlyList<int> sortedValues, double p)
    {
        if (sortedValues.Count == 0)
        {
            return 0.0;
        }

        int rank = (int)Math.Ceiling(p * sortedValues.Count);
        int index = Math.Clamp(rank - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}

/// <summary>
/// One already-resolved fact the report aggregates over — one task's realized outcome under one model,
/// with its stratification identity already settled (see <see cref="TelemetryReport"/>'s class doc for
/// why this is not <see cref="TelemetryRow"/>). Charter §6's <c>n</c> counts these.
/// </summary>
public sealed record TelemetryReportSample
{
    /// <summary>The display tag a human reads (e.g. <c>llama3:latest</c>, <c>claude-sonnet-5</c>). NEVER the grouping key — see <see cref="ModelFingerprint"/>.</summary>
    public required string Model { get; init; }

    /// <summary>The model's true identity — digest/quantization-qualified. The actual stratification key; two samples with an equal <see cref="Model"/> but different <see cref="ModelFingerprint"/> are DIFFERENT models (charter §5 "model drift") and are never pooled.</summary>
    public required string ModelFingerprint { get; init; }

    /// <summary>The resolver's declared tier for this task (charter §4.1) — the confound stratification exists to neutralize.</summary>
    public required string Tier { get; init; }

    /// <summary>The task-fingerprint bucket (charter §4.2) this task's observable features fall into. Opaque here: supplied already computed, never derived from a task's name.</summary>
    public required string FingerprintBucket { get; init; }

    /// <summary>Whether this task went green on its very first attempt — the input to charter §6's headline first-attempt pass rate.</summary>
    public required bool FirstAttemptSucceeded { get; init; }

    /// <summary>Attempts consumed before this task went green, or <c>null</c> if it never did — an abandoned task, not a zero (charter §5 survivorship).</summary>
    public int? AttemptsToGreen { get; init; }

    /// <summary>What this task cost, or <c>null</c> when nothing about it ever reported a cost — never defaulted to <c>0</c>.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>
/// One row of a <see cref="TelemetryReport"/> — one (model fingerprint × tier × fingerprint bucket)
/// stratum. Every row carries <see cref="SampleSize"/>; whether it carries a verdict at all is the
/// closed choice between <see cref="InsufficientEvidenceReportRow"/> and <see cref="SufficientEvidenceReportRow"/>.
/// </summary>
public abstract record TelemetryReportRow
{
    /// <summary>The stratum's model identity — see <see cref="TelemetryReportSample.ModelFingerprint"/>.</summary>
    public required string ModelFingerprint { get; init; }

    /// <summary>The stratum's declared tier.</summary>
    public required string Tier { get; init; }

    /// <summary>The stratum's task-fingerprint bucket.</summary>
    public required string FingerprintBucket { get; init; }

    /// <summary>The stratum's sample count, <c>n</c> — present on EVERY row, verdict or not (charter §5).</summary>
    public required int SampleSize { get; init; }
}

/// <summary>
/// A stratum below the minimum sample size (charter §5: "insufficient evidence as a first-class output,
/// not a blank cell"). Deliberately carries no verdict field of any kind — there is nothing on this type
/// a caller could misread as an earned number.
/// </summary>
public sealed record InsufficientEvidenceReportRow : TelemetryReportRow;

/// <summary>A stratum with enough samples to render charter §6's metrics.</summary>
public sealed record SufficientEvidenceReportRow : TelemetryReportRow
{
    /// <summary>The headline metric: the fraction of this stratum's tasks that went green on their first attempt.</summary>
    public required double FirstAttemptPassRate { get; init; }

    /// <summary>Attempts-to-green, inseparably paired with abandonment rate — see the class doc on <see cref="TelemetryReport"/>.</summary>
    public required TelemetryAttemptsToGreen AttemptsToGreen { get; init; }

    /// <summary><c>null</c> when no sample in this stratum ever reported a cost — a costless provider, not a <c>$0</c> one.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>
/// Attempts-to-green and abandonment rate as ONE fact, computed over the same denominator (charter §5
/// survivorship warning) — there is no accessor that reads one without the other.
/// </summary>
public sealed record TelemetryAttemptsToGreen
{
    /// <summary>Median attempts-to-green, over the samples that ever went green.</summary>
    public required double MedianAttempts { get; init; }

    /// <summary>P90 attempts-to-green, over the samples that ever went green — the number a mean alone would hide (charter §5 non-determinism).</summary>
    public required double P90Attempts { get; init; }

    /// <summary>The fraction of the stratum's WHOLE sample count that never went green — same denominator as <see cref="TelemetryReportRow.SampleSize"/>, not just the attempted-green subset.</summary>
    public required double AbandonmentRate { get; init; }
}
