using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// The <c>guardrails telemetry</c> verb's own behaviour (charter §9, <c>model-evidence-and-graduation</c>
/// #535, task 09): drives <see cref="TelemetryCommand.Create"/> directly, attached to a hand-built
/// <see cref="RootCommand"/> — the <see cref="SamplesCommandTests"/> idiom — so this class goes green in
/// task 10, before the verb is registered in the real composition root at all.
/// <see cref="TelemetryCommandWiringTests"/> is the separate proof that registration itself happened.
///
/// <para><b>TDD red.</b> Every test here drives an action that currently throws
/// <see cref="NotImplementedException"/> (<c>TelemetryCommand</c>'s stub), so the whole file is red and
/// none of it can be green by coincidence with the stub's default.</para>
///
/// <para>Every test points the corpus at its OWN fresh temp directory (never
/// <c>~/.guardrails/telemetry/</c> — a test that wrote there would poison the very data this plan exists
/// to collect) and deletes it afterwards. The one exception is
/// <see cref="DefaultCorpusRoot_ResolvesUnderTheUserProfile"/>, which takes no override and performs no
/// I/O at all — it only asserts the resolved PATH STRING.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
[Collection(TelemetryEnvironmentCollection.Name)]
public sealed class TelemetryCommandTests
{
    /// <summary>A distinctive, test-only model tag — never a real model name — so a substring match in
    /// rendered output can only be explained by the real corpus row flowing through, not by coincidence.</summary>
    private const string TestModelTag = "gr535-test-model";

    private static Task<(int Exit, string Output)> InvokeAsync(params string[] args) =>
        InvokeAsync(collectionEnabled: null, args);

    /// <summary>
    /// <paramref name="collectionEnabled"/> injects the opt-out decision instead of setting the
    /// process-wide <c>GUARDRAILS_TELEMETRY</c> variable — see
    /// <c>Ingest_WhenOptedOut_WritesNothing</c>. Null takes the production default (the environment).
    /// </summary>
    private static async Task<(int Exit, string Output)> InvokeAsync(
        Func<bool>? collectionEnabled, params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(TelemetryCommand.Create(io, collectionEnabled));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    // --- ingest ------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Ingest_WritesRowsFromAPlanJournal()
    {
        using var plan = new TempDir();
        using var corpus = new TempDir();

        const string runId = "run-ingest-1";
        const string taskId = "01-example";
        WriteJournal(plan.Path, runId, taskId, DateTimeOffset.UtcNow);

        (int exit, _) = await InvokeAsync("telemetry", "ingest", plan.Path, "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);

        List<TelemetryRow> rows = ReadRows(corpus.Path);

        // Two grains, one row shape (task 05/06): the task row rides the reserved Attempt == 0 sentinel;
        // the real attempt rides Attempt == 1.
        Assert.Contains(rows, r => r.RunId == runId && r.TaskId == taskId && r.Attempt == 0);
        Assert.Contains(rows, r =>
            r.RunId == runId && r.TaskId == taskId && r.Attempt == 1 && r.Model == TestModelTag);
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Ingest_WhenOptedOut_WritesNothing()
    {
        using var plan = new TempDir();
        using var corpus = new TempDir();

        WriteJournal(plan.Path, "run-optout-1", "01-example", DateTimeOffset.UtcNow);

        // The opt-out is INJECTED, not set process-wide. Setting GUARDRAILS_TELEMETRY=off around this
        // await used to silently suppress writes in every test running concurrently in this process — the
        // measured six-test failure documented on TelemetryCollectionSwitchTests. What this test owns is
        // that the VERB honours the decision; that the environment produces the decision is proven
        // separately as a pure function.
        (int exit, _) = await InvokeAsync(
            collectionEnabled: () => false, "telemetry", "ingest", plan.Path, "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        AssertNoFilesUnder(corpus.Path);
    }

    // --- report --------------------------------------------------------------------------------------

    /// <summary>
    /// One ingested task (one attempt, succeeded) is a stratum of sample size 1 — below
    /// <see cref="TelemetryReport.DefaultMinimumSampleSize"/> (5) regardless of how the verb's row→sample
    /// mapping derives its stratification keys, so this test does not need to guess that derivation. It
    /// asserts two things the REAL pipeline (not a hardcoded table) must produce: the literal
    /// <see cref="TestModelTag"/> from the ingested row, and the words "insufficient evidence"
    /// (case-insensitive) that <c>InsufficientEvidenceReportRow</c>'s own contract requires (see
    /// <c>TelemetryCommand</c>'s class doc).
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Report_PrintsTheStratifiedTable()
    {
        using var plan = new TempDir();
        using var corpus = new TempDir();

        WriteJournal(plan.Path, "run-report-1", "01-example", DateTimeOffset.UtcNow);
        (int ingestExit, _) = await InvokeAsync("telemetry", "ingest", plan.Path, "--corpus-root", corpus.Path);
        Assert.Equal(ExitCodes.Success, ingestExit);

        (int exit, string output) = await InvokeAsync("telemetry", "report", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(TestModelTag, output, StringComparison.Ordinal);
        Assert.Contains("insufficient evidence", output, StringComparison.OrdinalIgnoreCase);
    }

    // --- purge ---------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Purge_EmptiesTheCorpus()
    {
        using var corpus = new TempDir();

        new TelemetryCorpusStore(corpus.Path).Append(SampleRow());
        Assert.NotEmpty(Directory.GetFiles(corpus.Path, "*.jsonl", SearchOption.AllDirectories));

        (int exit, _) = await InvokeAsync("telemetry", "purge", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        AssertNoFilesUnder(corpus.Path);
    }

    // --- default corpus root --------------------------------------------------------------------------

    /// <summary>
    /// The ONE test that must NOT take the override and must perform NO I/O — a pure assertion on the
    /// resolved path string. Without this, every other test supplies <c>--corpus-root</c> and an
    /// implementation that never resolves a real default would pass the whole suite while shipping a verb
    /// that does not work for an actual user.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void DefaultCorpusRoot_ResolvesUnderTheUserProfile()
    {
        string resolved = TelemetryCommand.ResolveCorpusRoot(null);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expectedSuffix = Path.Combine(".guardrails", "telemetry");

        Assert.StartsWith(userProfile, resolved, StringComparison.Ordinal);
        Assert.EndsWith(expectedSuffix, resolved, StringComparison.Ordinal);
    }

    // --- fixtures --------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a minimal but real <c>state/run.json</c> — one task, one attempt that SUCCEEDED — using the
    /// SAME <see cref="JournalJson.Options"/> production serialization writes with, so
    /// <c>TelemetryIngest.IngestPlanFolder</c>'s <see cref="JournalReader"/> read-back is exercised for
    /// real rather than assumed.
    /// </summary>
    private static void WriteJournal(string planDir, string runId, string taskId, DateTimeOffset startedAt)
    {
        var attempt = new AttemptRecord
        {
            Attempt = 1,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(5),
            ActionExitCode = 0,
            Outcome = AttemptOutcome.Succeeded,
            CostUsd = 1.23m,
            LogDir = $"logs/{runId}/{taskId}/attempt-1",
            Provenance = new AttemptProvenance
            {
                Model = TestModelTag,
                Runner = "default",
                Kind = "claude",
                Tier = "medium",
                TierSource = TierSource.Task
            }
        };

        var journal = new JournalDocument
        {
            RunId = runId,
            PlanHash = "sha256:" + new string('a', 64),
            NextMergeSequence = 1,
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                [taskId] = new TaskJournalEntry
                {
                    Status = JournalTaskStatus.Succeeded,
                    Attempts = [attempt]
                }
            }
        };

        string journalPath = RunJournal.PathFor(planDir);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, JournalJson.Options));
    }

    private static TelemetryRow SampleRow() => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "run-purge-fixture",
        TaskId = "01-purge-fixture",
        Attempt = 1,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        Outcome = "succeeded",
        Repo = "guardrails"
    };

    /// <summary>
    /// Reads every row already on disk under <paramref name="corpusRoot"/>, deserialized back into
    /// <see cref="TelemetryRow"/> via the corpus store's OWN wire options
    /// (<see cref="TelemetryCorpusStore.JsonOptions"/>) rather than a second ad hoc set — the same
    /// round-trip discipline <c>TelemetryCorpusStoreTests</c> uses.
    /// </summary>
    private static List<TelemetryRow> ReadRows(string corpusRoot)
    {
        var rows = new List<TelemetryRow>();
        if (!Directory.Exists(corpusRoot))
        {
            return rows;
        }

        foreach (string file in Directory.GetFiles(corpusRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                TelemetryRow row = JsonSerializer.Deserialize<TelemetryRow>(line, TelemetryCorpusStore.JsonOptions)
                    ?? throw new InvalidOperationException("row did not round-trip to a TelemetryRow");
                rows.Add(row);
            }
        }

        return rows;
    }

    private static void AssertNoFilesUnder(string root) => Assert.True(
        !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(),
        $"expected no files under '{root}'");

    /// <summary>A fresh temp directory, deleted on <see cref="Dispose"/>. Never <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-telemetrycmd-" + Guid.NewGuid().ToString("N"));

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
