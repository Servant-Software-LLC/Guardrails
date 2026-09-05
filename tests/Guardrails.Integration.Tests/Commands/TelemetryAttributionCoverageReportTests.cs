using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Telemetry;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// Issue #619 — <c>telemetry report</c> must say what share of the corpus its figures rest on.
///
/// <para><b>The defect this pins.</b> A stratified table renders identically over a corpus that attributes
/// 95% of its rows and one that attributes 20%, and nothing in the table says which. An analyst reading
/// the report alone therefore trusts numbers whose coverage they have never seen — the same
/// silent-in-the-flattering-direction shape #577 was filed about, moved one layer out. It matters on a
/// deadline: the Mac Studio comparison (#570 Phase C) reads this report during bring-up week.</para>
///
/// <para><b>Not a member of <see cref="TelemetryEnvironmentCollection"/>, deliberately.</b> Like
/// <c>TelemetryReportPhase1Tests</c>, this class constructs its own <see cref="TelemetryCorpusStore"/> and
/// never lets the opt-out come from the environment, so it is immune to the process-global hazard that
/// collection exists for. Adding it there would cost the concurrency coverage that catches the next defect
/// of that shape.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryAttributionCoverageReportTests
{
    /// <summary>Post-boundary, so nothing here is filtered by the 2026-08-31 era boundary.</summary>
    private static readonly DateTimeOffset PostBoundary = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const string TestRepo = "gr619-test-repo";

    private static async Task<(int Exit, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(TelemetryCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// The headline figure, and the whole point of the issue: the denominator is the ATTRIBUTABLE rows,
    /// not every row in the file. Two attributable rows among a crowd of task-grain sentinels is 50%
    /// coverage — a report that said 2 of 12 would be repeating #577's original miscount in the one place
    /// an analyst actually reads.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task TheReportStatesCoverageAgainstTheAttributableDenominator()
    {
        using var corpus = new TempDir();
        var store = new TelemetryCorpusStore(corpus.Path);

        // Two attributable attempt rows: one comparable, one the defect.
        store.Append(TaskGrainRow("run-a", "01-example"));
        store.Append(AttemptRow("run-a", "01-example", "gr619-real-model", ModelAttribution.Recorded));
        store.Append(TaskGrainRow("run-b", "02-example"));
        store.Append(AttemptRow("run-b", "02-example", model: null, ModelAttribution.NotRecorded));

        // Eight more task-grain sentinels, which must NOT dilute the denominator.
        for (int i = 0; i < 8; i++)
        {
            store.Append(TaskGrainRow($"run-filler-{i}", "03-example"));
        }

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        string line = LineContaining(output, "attributable row(s) name a real model");
        Assert.Contains("1 of 2", line, StringComparison.Ordinal);
        Assert.Contains("50% comparable", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The excluded groups are NAMED with their counts rather than summed into an "other". Three different
    /// facts sharing one number is the defect #577 was filed about; a coverage block that re-merges them
    /// would reintroduce it in the reporting layer.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task RowsOutsideTheDenominatorAreNamedIndividually()
    {
        using var corpus = new TempDir();
        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-a", "01-example"));
        store.Append(AttemptRow("run-a", "01-example", "gr619-real-model", ModelAttribution.Recorded));
        store.Append(AttemptRow("run-a", "02-script", model: null, ModelAttribution.ScriptAction));
        store.Append(AttemptRow("run-a", "03-murky", model: null, ModelAttribution.Unknown));
        store.Append(AttemptRow("run-a", "04-legacy", model: null, attribution: null));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        string line = LineContaining(output, "outside the denominator:");
        Assert.Contains("task-grain 1", line, StringComparison.Ordinal);
        Assert.Contains("script-action 1", line, StringComparison.Ordinal);
        Assert.Contains("unknown 1", line, StringComparison.Ordinal);
        Assert.Contains("pre-column 1", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect count gets its own sentence when it is non-zero. A number sitting in a breakdown line is
    /// easy to read past; the thing that must not be read past is "some rows cannot say what they ran on".
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task ALiveRecordingGapIsCalledOutRatherThanLeftInTheBreakdown()
    {
        using var corpus = new TempDir();
        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-a", "01-example"));
        store.Append(AttemptRow("run-a", "01-example", "gr619-real-model", ModelAttribution.Recorded));
        store.Append(AttemptRow("run-a", "02-example", model: null, ModelAttribution.NotRecorded));
        store.Append(AttemptRow("run-a", "03-example", model: null, ModelAttribution.NotRecorded));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("2 row(s) SHOULD name a model and do not", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean corpus still prints the block. A section that appears only when something is wrong trains
    /// the reader to equate its absence with "not measured" — and the first time it is genuinely absent
    /// because of a bug, nobody notices. It must also NOT raise the defect sentence at zero.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task PerfectCoverageStillRendersTheBlock_AndRaisesNoDefectSentence()
    {
        using var corpus = new TempDir();
        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-a", "01-example"));
        store.Append(AttemptRow("run-a", "01-example", "gr619-real-model", ModelAttribution.Recorded));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Attribution coverage", output, StringComparison.Ordinal);
        Assert.Contains("1 of 1", LineContaining(output, "attributable row(s) name a real model"), StringComparison.Ordinal);
        Assert.DoesNotContain("SHOULD name a model and do not", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coverage is reported BEFORE the table it qualifies. A reader who stops at the first table must
    /// already have met the caveat; putting it below would let the numbers be read and acted on first.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task CoverageIsRenderedAboveTheStratifiedTable()
    {
        using var corpus = new TempDir();
        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-a", "01-example"));
        store.Append(AttemptRow("run-a", "01-example", "gr619-real-model", ModelAttribution.Recorded));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        int coverageAt = output.IndexOf("Attribution coverage", StringComparison.Ordinal);
        int modelAt = output.IndexOf("gr619-real-model", StringComparison.Ordinal);

        Assert.True(coverageAt >= 0, $"no coverage block in output:\n{output}");
        Assert.True(modelAt >= 0, $"no stratum row in output:\n{output}");
        Assert.True(
            coverageAt < modelAt,
            $"coverage block rendered AFTER the table it qualifies. Output:\n{output}");
    }

    // --- helpers ---------------------------------------------------------------------------------------

    private static TelemetryRow TaskGrainRow(string runId, string taskId) => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = runId,
        TaskId = taskId,
        Attempt = 0,
        StartedAt = PostBoundary,
        EndedAt = PostBoundary,
        Outcome = "succeeded",
        Repo = TestRepo,
        ModelAttribution = ModelAttribution.TaskGrain
    };

    private static TelemetryRow AttemptRow(
        string runId, string taskId, string? model, string? attribution) => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = runId,
        TaskId = taskId,
        Attempt = 1,
        StartedAt = PostBoundary,
        EndedAt = PostBoundary.AddMinutes(1),
        Outcome = "succeeded",
        Model = model,
        Runner = "default",
        Kind = "claude",
        Repo = TestRepo,
        ModelAttribution = attribution
    };

    /// <summary>The one output line containing <paramref name="needle"/> — fails loudly if not exactly one.</summary>
    private static string LineContaining(string output, string needle)
    {
        string[] matches = output
            .Split('\n')
            .Where(line => line.Contains(needle, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            $"expected exactly one output line containing '{needle}', found {matches.Length}. Output:\n{output}");

        return matches[0];
    }

    /// <summary>A fresh temp directory, deleted on dispose. Never <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "gr619-coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp dir that outlives the test is not a test failure */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
    }
}
