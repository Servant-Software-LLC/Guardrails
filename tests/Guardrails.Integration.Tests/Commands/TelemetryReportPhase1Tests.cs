using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Telemetry;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// Plan 30 §3.2/§3.3, task 21 — the failing half of the report's Phase-1 rendering, written against the
/// tree BEFORE task 22 (<c>22-render-the-bucket-digest-and-era-boundary</c>) lands. Copies the
/// <c>InvokeAsync</c>/<c>TempDir</c> idiom from <see cref="TelemetryCommandTests"/> rather than
/// reintroducing it, and writes corpus rows directly through <see cref="TelemetryCorpusStore.Append"/> —
/// the <c>Purge_EmptiesTheCorpus</c> idiom — because the subject under test is the REPORT, not the
/// journal ETL.
///
/// <para><b>This is a RUNTIME red, not a compile red.</b> Every assertion below reads the report's
/// rendered stdout, never a constant task 22 has not written yet, so this file compiles against the tree
/// exactly as it stands today (<c>04a-extend-the-corpus-row-shape</c> already shipped the
/// <see cref="TelemetryRow.Bucket"/>/<see cref="TelemetryRow.ModelDigest"/> columns this file constructs
/// rows with). Four of the five facts below are genuinely unrendered today; the fifth
/// (<see cref="AnUnbucketedLegacyRow_StillRendersUnbucketed"/>) is a pinned EXEMPTION — see its own doc.</para>
///
/// <para><b>The era boundary is <c>2026-08-31T00:00:00Z</c></b> — the first UTC midnight after both the
/// provenance fix (#532, <c>3129919</c>, 2026-08-30 17:58 UTC) and the corpus-isolation fix (#547,
/// <c>6229643</c>, 2026-08-30 18:06 UTC) were on master. A row's <see cref="TelemetryRow.StartedAt"/>
/// before that instant is pre-fix era and must not reach the stratified table (plan 30 §3, the DECIDED
/// paragraph — a documented boundary, never a backfill or a silent mix of eras).</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryReportPhase1Tests
{
    /// <summary>
    /// The first UTC midnight after both #532 and #547 landed on master (plan 30 §3) — literal, not a
    /// constant read off <c>TelemetryCommand</c>, so this file never depends on task 22's symbol existing.
    /// </summary>
    private static readonly DateTimeOffset EraBoundary = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset PreBoundary = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PostBoundary = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const string TestRepo = "gr30-test-repo";

    private static async Task<(int Exit, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(TelemetryCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    // --- behaviour 1 -----------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task ABucketedRow_RendersItsBucket_NotUnbucketed()
    {
        using var corpus = new TempDir();
        const string modelTag = "gr30-bucketed-model";

        var store = new TelemetryCorpusStore(corpus.Path);
        store.Append(TaskGrainRow("run-bucketed-1", "01-example", PostBoundary, bucket: "implementation"));
        store.Append(AttemptRow("run-bucketed-1", "01-example", PostBoundary, modelTag));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        string line = LineContaining(output, modelTag);
        Assert.Contains("implementation", line, StringComparison.Ordinal);
        Assert.DoesNotContain("(unbucketed)", line, StringComparison.Ordinal);
    }

    // --- behaviour 2 -------------------------------------------------------------------------------

    /// <summary>
    /// <b>Pinned exemption — this test is already GREEN, and that is correct.</b> The corpus is
    /// append-only and never rewritten, so a row written after the §3.1 provenance fix but before §3.2's
    /// bucket landed is honestly POST-boundary AND unbucketed — not a regression, and it keeps rendering
    /// the <c>(unbucketed)</c> sentinel forever. Today's report already renders that sentinel for every
    /// row (it has no bucket column input at all yet), so this passes before task 22 lands; task 22 must
    /// not delete the <c>(unbucketed)</c> case while it rewrites this rendering line. Using a PRE-boundary
    /// row here would be the trap: behaviour 5's filter would remove it and this test would pass for the
    /// wrong reason forever.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task AnUnbucketedLegacyRow_StillRendersUnbucketed()
    {
        using var corpus = new TempDir();
        const string modelTag = "gr30-legacy-model";

        var store = new TelemetryCorpusStore(corpus.Path);
        store.Append(TaskGrainRow("run-legacy-1", "01-example", PostBoundary, bucket: null));
        store.Append(AttemptRow("run-legacy-1", "01-example", PostBoundary, modelTag));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        string line = LineContaining(output, modelTag);
        Assert.Contains("(unbucketed)", line, StringComparison.Ordinal);
    }

    // --- behaviour 3 -------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task TwoDigestsUnderOneModelTag_DoNotPool()
    {
        using var corpus = new TempDir();
        const string sharedModelTag = "gr30-shared-model";
        const string digestA = "gr30-digest-aaaa";
        const string digestB = "gr30-digest-bbbb";

        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-digest-a", "01-example", PostBoundary));
        store.Append(AttemptRow("run-digest-a", "01-example", PostBoundary, sharedModelTag, digest: digestA));

        store.Append(TaskGrainRow("run-digest-b", "01-example", PostBoundary));
        store.Append(AttemptRow("run-digest-b", "01-example", PostBoundary, sharedModelTag, digest: digestB));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(digestA, output, StringComparison.Ordinal);
        Assert.Contains(digestB, output, StringComparison.Ordinal);
        Assert.Equal(2, CountLinesContaining(output, sharedModelTag));
    }

    // --- behaviour 4 -------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task TheReportStatesTheEraBoundaryDate()
    {
        using var corpus = new TempDir();

        var store = new TelemetryCorpusStore(corpus.Path);
        store.Append(TaskGrainRow("run-boundary-legend-1", "01-example", PostBoundary));
        store.Append(AttemptRow("run-boundary-legend-1", "01-example", PostBoundary, "gr30-legend-model"));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("2026-08-31", output, StringComparison.Ordinal);
        Assert.Contains("BOUNDARY", output, StringComparison.Ordinal);
    }

    // --- behaviour 5 -------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task RowsBeforeTheEraBoundary_AreExcludedFromTheStratifiedTable()
    {
        using var corpus = new TempDir();
        const string preBoundaryTag = "gr30-preboundary-model";
        const string postBoundaryTag = "gr30-postboundary-model";

        var store = new TelemetryCorpusStore(corpus.Path);

        store.Append(TaskGrainRow("run-pre-1", "01-example", PreBoundary));
        store.Append(AttemptRow("run-pre-1", "01-example", PreBoundary, preBoundaryTag));

        store.Append(TaskGrainRow("run-post-1", "01-example", PostBoundary));
        store.Append(AttemptRow("run-post-1", "01-example", PostBoundary, postBoundaryTag));

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain(preBoundaryTag, output, StringComparison.Ordinal);
        Assert.Contains(postBoundaryTag, output, StringComparison.Ordinal);
    }

    // --- fixtures --------------------------------------------------------------------------------------

    /// <summary>
    /// The task-grain sentinel row (<c>Attempt == 0</c>) — where <see cref="TelemetryRow.Bucket"/> rides
    /// (plan 30 §3.2). Without a paired attempt row, <c>TelemetryCommand.ToSamples</c> contributes nothing
    /// for this <c>(runId, taskId)</c> group, so every fixture task in this file also gets an
    /// <see cref="AttemptRow"/>.
    /// </summary>
    private static TelemetryRow TaskGrainRow(string runId, string taskId, DateTimeOffset startedAt, string? bucket = null) => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = runId,
        TaskId = taskId,
        Attempt = 0,
        StartedAt = startedAt,
        EndedAt = startedAt,
        Outcome = "succeeded",
        Repo = TestRepo,
        Bucket = bucket
    };

    /// <summary>
    /// An <c>Attempt == 1</c> row — where <see cref="TelemetryRow.Model"/>/<see cref="TelemetryRow.Runner"/>/
    /// <see cref="TelemetryRow.Kind"/>/<see cref="TelemetryRow.ModelDigest"/> ride. <paramref name="model"/>
    /// is always a distinctive, test-only <c>gr30-…</c> tag — never a real model name — so a substring match
    /// in rendered output can only be explained by this row flowing through the real pipeline.
    /// </summary>
    private static TelemetryRow AttemptRow(
        string runId, string taskId, DateTimeOffset startedAt, string model, string? digest = null) => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = runId,
        TaskId = taskId,
        Attempt = 1,
        StartedAt = startedAt,
        EndedAt = startedAt.AddMinutes(1),
        Outcome = "succeeded",
        Model = model,
        Runner = "default",
        Kind = "claude",
        ModelDigest = digest,
        Repo = TestRepo
    };

    /// <summary>The one output line containing <paramref name="needle"/> — fails loudly if there is not exactly one.</summary>
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

    private static int CountLinesContaining(string output, string needle) =>
        output.Split('\n').Count(line => line.Contains(needle, StringComparison.Ordinal));

    /// <summary>A fresh temp directory, deleted on <see cref="Dispose"/>. Never <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-telemetryphase1-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
