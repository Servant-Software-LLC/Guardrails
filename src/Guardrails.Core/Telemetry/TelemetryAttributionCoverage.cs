namespace Guardrails.Core.Telemetry;

/// <summary>
/// How much of a set of corpus rows can participate in a model comparison, read straight off each row's
/// <see cref="TelemetryRow.ModelAttribution"/> column (issue #619, SSOT §15.2b).
///
/// <para><b>Why this exists next to <see cref="TelemetryAttributionCensus"/> rather than inside it.</b>
/// The census answers the same question by JOINING the corpus back to the plan folders on disk, which is
/// what it took to establish #577's split in the first place. That join is expensive, and for any row
/// whose plan folder has since been deleted it is impossible — 41 such rows in the operator corpus at the
/// time of writing. Once the answer is written ON the row at ingest time, the question becomes a counting
/// pass over a column. This type is that pass: no I/O, no plan folder, no possibility of being unable to
/// answer for an old row.</para>
///
/// <para><b>The denominator is the point.</b> <see cref="Attributable"/> counts only the rows that COULD
/// have named a model (<see cref="ModelAttribution.AttributableTokens"/>). Dividing by every row instead
/// would fold in the once-per-task sentinel and script actions — rows that were never going to name a
/// model — and understate coverage by exactly the margin that made "76% of rows name no usable model"
/// read as a catastrophe when 77% of it was correct by construction. A coverage figure that flatters or
/// alarms by choice of denominator is the failure #577 exists to prevent, one layer out.</para>
///
/// <para><b>Comparable is narrower than attributable.</b> Only <see cref="Recorded"/> rows carry a usable
/// comparison key. <see cref="CliDefault"/> is honest and not a defect, but the sentinel is not a model
/// identity, so pooling it with a named model would attribute its cost and outcomes to a model nobody
/// recorded. The two figures are reported separately so an analysis has to decide what to do with the
/// middle group rather than inheriting it by accident.</para>
/// </summary>
public static class TelemetryAttributionCoverage
{
    /// <summary>
    /// Count <paramref name="rows"/> by attribution token. Every row lands in exactly one bucket, and the
    /// buckets sum to the input count — a reader can always reconcile the block against the row total,
    /// which is what stops a category being quietly dropped.
    /// </summary>
    public static AttributionCoverage Compute(IEnumerable<TelemetryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int recorded = 0, cliDefault = 0, notRecorded = 0;
        int scriptAction = 0, taskGrain = 0, unknown = 0, preColumn = 0;
        var unrecognized = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (TelemetryRow row in rows)
        {
            switch (row.ModelAttribution)
            {
                // Null is NOT the same as `unknown`, and conflating them would undo this column's whole
                // purpose. Null means the row was written before the column existed (schemaVersion < 3),
                // so its attribution is unknowABLE; `unknown` means a current writer looked and could not
                // decide. One is a fact about the corpus's history, the other about a specific task.
                case null:
                    preColumn++;
                    break;
                case ModelAttribution.Recorded:
                    recorded++;
                    break;
                case ModelAttribution.CliDefault:
                    cliDefault++;
                    break;
                case ModelAttribution.NotRecorded:
                    notRecorded++;
                    break;
                case ModelAttribution.ScriptAction:
                    scriptAction++;
                    break;
                case ModelAttribution.TaskGrain:
                    taskGrain++;
                    break;
                case ModelAttribution.Unknown:
                    unknown++;
                    break;

                // A token this build does not define. The corpus is append-only and never rewritten, so a
                // newer harness writing a token this one predates is a real possibility rather than a
                // defensive fiction. SSOT §15.4's standing rule applies: record it verbatim, never fold it
                // into a neighbour. Folding it into `unknown` would understate a future vocabulary, and
                // folding it into the denominator would let an unrecognised token move a coverage figure
                // nobody could explain.
                default:
                    unrecognized.TryGetValue(row.ModelAttribution, out int seen);
                    unrecognized[row.ModelAttribution] = seen + 1;
                    break;
            }
        }

        return new AttributionCoverage
        {
            Recorded = recorded,
            CliDefault = cliDefault,
            NotRecorded = notRecorded,
            ScriptAction = scriptAction,
            TaskGrain = taskGrain,
            Unknown = unknown,
            PreColumn = preColumn,
            Unrecognized = unrecognized
        };
    }
}

/// <summary>
/// The counting pass's result. Every field is a row count; the seven scalars plus
/// <see cref="UnrecognizedTotal"/> sum to <see cref="TotalRows"/>.
/// </summary>
public sealed record AttributionCoverage
{
    /// <summary>Rows naming a real, fully resolved model — the only comparable ones.</summary>
    public required int Recorded { get; init; }

    /// <summary>
    /// Rows that ran a model under no named route. Attributable, deliberately not comparable.
    /// </summary>
    public required int CliDefault { get; init; }

    /// <summary>Rows that should name a model and do not — <b>the defect count</b>.</summary>
    public required int NotRecorded { get; init; }

    /// <summary>Script actions: no model to record. Outside the denominator by construction.</summary>
    public required int ScriptAction { get; init; }

    /// <summary>Once-per-task sentinels: no single route to record. Outside the denominator.</summary>
    public required int TaskGrain { get; init; }

    /// <summary>Rows whose action kind could not be decided. Neither correct nor a defect (SSOT §15.4).</summary>
    public required int Unknown { get; init; }

    /// <summary>
    /// Rows written before the attribution column existed (<c>schemaVersion &lt; 3</c>). Their attribution
    /// is unknowable rather than unknown, and no backfill can recover it — script-vs-prompt is read from a
    /// task folder that may no longer exist.
    /// </summary>
    public required int PreColumn { get; init; }

    /// <summary>Tokens this build does not define, verbatim, with their counts. Normally empty.</summary>
    public IReadOnlyDictionary<string, int> Unrecognized { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Total rows carrying a token this build does not define.</summary>
    public int UnrecognizedTotal => Unrecognized.Values.Sum();

    /// <summary>
    /// Rows that COULD have named a model — the honest denominator for <see cref="ComparableShare"/>.
    /// </summary>
    public int Attributable => Recorded + CliDefault + NotRecorded;

    /// <summary>Every row counted.</summary>
    public int TotalRows =>
        Attributable + ScriptAction + TaskGrain + Unknown + PreColumn + UnrecognizedTotal;

    /// <summary>
    /// The share of attributable rows that name a real model, or <c>null</c> when nothing is attributable
    /// at all. Null rather than 0.0 deliberately: a corpus with no attributable row has no coverage to
    /// report, and rendering that as 0% would assert a total failure of attribution where the truth is
    /// that the question does not yet apply.
    /// </summary>
    public double? ComparableShare => Attributable == 0 ? null : (double)Recorded / Attributable;
}
