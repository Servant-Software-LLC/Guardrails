using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// Plan 30 §3.2/§3.3/§3.4 — the Phase-1 journal facts (task 03) reaching the corpus row (task 04a's
/// thirteen columns) through the journal-to-corpus ETL (<see cref="TelemetryIngest.Ingest"/>, task
/// <c>20-carry-phase1-facts-into-the-corpus-row</c>). Eight behaviours, each built the way
/// <see cref="TelemetryIngestTests"/> already is: a real <see cref="JournalDocument"/> through a real
/// <see cref="TelemetryCorpusStore"/> over a fresh temp directory, read back off disk — never a
/// <see cref="TelemetryRow"/> constructed and asserted on directly, which would pass whatever the ETL
/// does.
///
/// <para><b>Six of eight are TDD red</b> against the current <see cref="TelemetryIngest"/>, which maps
/// none of the thirteen Phase-1 columns yet. The other two —
/// <see cref="TheSchemaVersionSaysTheRowShapeChanged"/> and
/// <see cref="AnUnreportedPhase1Fact_StaysNull_NotZero"/> — are declared exemptions and correctly green
/// today: the schema version is already stamped at both construction sites, and an unmapped column
/// already reads null by omission. Neither duplicates task 04a's <c>CorpusRowShapeTests</c>, which
/// assert the columns exist and are nullable on the record itself; these assert the ETL actually behaves
/// that way on the rows it emits.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class Phase1TelemetryRowTests : IDisposable
{
    private readonly string corpusRoot =
        Path.Combine(Path.GetTempPath(), "guardrails-phase1-row-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(corpusRoot))
        {
            Directory.Delete(corpusRoot, recursive: true);
        }
    }

    // --- 1. the bucket reaches the attempt row -------------------------------------------------

    [Fact]
    public void TheAttemptRowCarriesTheBucket()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-bucket", new Dictionary<string, TaskJournalEntry>
        {
            ["01-impl"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: "implementation",
                Attempt(1, AttemptOutcome.Succeeded, started))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow attemptRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-impl" && r.Attempt == 1);
        Assert.Equal("implementation", attemptRow.Bucket);
    }

    // --- 2. the Attempt == 0 task-grain sentinel row carries the bucket too --------------------

    [Fact]
    public void TheTaskGrainRowCarriesTheBucketToo()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-bucket-task", new Dictionary<string, TaskJournalEntry>
        {
            ["01-impl"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: "implementation",
                Attempt(1, AttemptOutcome.Succeeded, started))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow taskRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-impl" && r.Attempt == 0);
        Assert.Equal("implementation", taskRow.Bucket);
    }

    // --- 3. the model digest reaches the attempt row --------------------------------------------

    [Fact]
    public void TheAttemptRowCarriesTheModelDigest()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-digest", new Dictionary<string, TaskJournalEntry>
        {
            ["01-local"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started,
                    provenance: new AttemptProvenance { ModelDigest = "sha256:digest-xyz" }))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow attemptRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-local" && r.Attempt == 1);
        Assert.Equal("sha256:digest-xyz", attemptRow.ModelDigest);
    }

    // --- 4. turns and both segment halves reach the attempt row, flattened ---------------------

    [Fact]
    public void TheAttemptRowCarriesTurnsAndSegments()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-segments", new Dictionary<string, TaskJournalEntry>
        {
            ["01-segmented"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started,
                    turns: 12,
                    segments: new AttemptSegments { ActionMs = 1500, GuardrailMs = 500 }))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow attemptRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-segmented" && r.Attempt == 1);
        Assert.Equal(12, attemptRow.Turns);
        Assert.Equal(1500, attemptRow.ActionMs);
        Assert.Equal(500, attemptRow.GuardrailMs);
    }

    // --- 5. RouteWarm reaches the attempt row, both polarities ----------------------------------

    /// <summary>
    /// Two attempts in the same journal, one warm and one cold, asserted with <c>Assert.Equal</c> rather
    /// than <c>Assert.True</c>/<c>Assert.False</c> — so a failure message distinguishes an unmapped
    /// (null) column from a genuinely wrong polarity instead of collapsing both into "not true"/"not
    /// false".
    /// </summary>
    [Fact]
    public void TheAttemptRowCarriesRouteWarmth()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-warmth", new Dictionary<string, TaskJournalEntry>
        {
            ["01-warm"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started,
                    provenance: new AttemptProvenance { RouteWarm = true })),
            ["02-cold"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started,
                    provenance: new AttemptProvenance { RouteWarm = false }))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow warmRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-warm" && r.Attempt == 1);
        TelemetryRow coldRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "02-cold" && r.Attempt == 1);

        Assert.Equal(true, warmRow.RouteWarm);
        Assert.Equal(false, coldRow.RouteWarm);
    }

    // --- 6. the run environment reaches every row of both grains --------------------------------

    /// <summary>
    /// One retried task (two attempts) plus one single-attempt task: five rows total across both
    /// grains — two task-grain sentinels and three attempt rows. Asserted in a loop over every row
    /// rather than the first, since a half-mapped grain (e.g. attempt rows get it, task rows do not) is
    /// exactly what a spot check would miss.
    /// </summary>
    [Fact]
    public void EveryRowCarriesTheRunEnvironment()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        var environment = new RunEnvironment
        {
            Host = "dave-mac-studio",
            Os = "macOS 15",
            CpuCount = 16,
            TotalMemoryBytes = 68_719_476_736,
            MaxParallelism = 4,
            HarnessVersion = "1.0.0-preview.40",
            SkillVersion = "plan-breakdown@3"
        };

        JournalDocument journal = Journal("run-environment", new Dictionary<string, TaskJournalEntry>
        {
            ["01-retried"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.GuardrailFailed, started),
                Attempt(2, AttemptOutcome.Succeeded, started.AddHours(1))),
            ["02-single"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started))
        }, environment: environment);

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow[] rows = AllRows(corpusRoot);
        Assert.Equal(5, rows.Length); // 2 task-grain sentinels + 3 attempt rows (2 retries + 1 single)

        foreach (TelemetryRow row in rows)
        {
            Assert.Equal("dave-mac-studio", row.Host);
            Assert.Equal("macOS 15", row.Os);
            Assert.Equal(16, row.CpuCount);
            Assert.Equal(68_719_476_736, row.TotalMemoryBytes);
            Assert.Equal(4, row.MaxParallelism);
            Assert.Equal("1.0.0-preview.40", row.HarnessVersion);
            Assert.Equal("plan-breakdown@3", row.SkillVersion);
        }
    }

    // --- 7. the ETL stamps the schema version on every row it writes ---------------------------

    /// <summary>
    /// DECLARED EXEMPTION: green today. Task 04a already bumped
    /// <see cref="TelemetryRow.CurrentSchemaVersion"/> past 1, and both of
    /// <see cref="TelemetryIngest.Ingest"/>'s construction sites have stamped it since Phase 0. Asserted
    /// SYMBOLICALLY, never against the literal <c>2</c>, so this survives the next bump —
    /// <c>TelemetryCorpusStoreTests.Append_EveryRowCarriesSchemaVersion</c> (line 130) is the precedent.
    /// </summary>
    [Fact]
    public void TheSchemaVersionSaysTheRowShapeChanged()
    {
        Assert.True(
            TelemetryRow.CurrentSchemaVersion > 1,
            $"expected CurrentSchemaVersion > 1, was {TelemetryRow.CurrentSchemaVersion}");

        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-schema", new Dictionary<string, TaskJournalEntry>
        {
            ["01-a"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow[] rows = AllRows(corpusRoot);
        Assert.NotEmpty(rows);
        foreach (TelemetryRow row in rows)
        {
            Assert.Equal(TelemetryRow.CurrentSchemaVersion, row.SchemaVersion);
        }
    }

    // --- 8. an unreported Phase-1 attempt fact stays null, never 0 or false --------------------

    /// <summary>
    /// DECLARED EXEMPTION: green today — every one of these five columns already reads null on any row,
    /// because the ETL maps none of them yet (the same emptiness
    /// <see cref="TheSchemaVersionSaysTheRowShapeChanged"/> also happens to hold against). After task 20
    /// this becomes the check that stops a coalescing implementation (<c>?? 0</c>, <c>?? false</c>) from
    /// turning an unreported fact into a fabricated measurement (§15.2) — the same null-versus-zero rule
    /// <see cref="TelemetryRow.CostUsd"/>'s doc comment already draws.
    /// </summary>
    [Fact]
    public void AnUnreportedPhase1Fact_StaysNull_NotZero()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-unreported", new Dictionary<string, TaskJournalEntry>
        {
            ["01-bare"] = TaskEntry(JournalTaskStatus.Succeeded, bucket: null,
                Attempt(1, AttemptOutcome.Succeeded, started))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow attemptRow = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "01-bare" && r.Attempt == 1);

        Assert.Null(attemptRow.Turns);
        Assert.Null(attemptRow.ActionMs);
        Assert.Null(attemptRow.GuardrailMs);
        Assert.Null(attemptRow.ModelDigest);
        Assert.Null(attemptRow.RouteWarm);
    }

    // --- fixtures --------------------------------------------------------------------------------

    private static JournalDocument Journal(
        string runId,
        Dictionary<string, TaskJournalEntry> tasks,
        RunEnvironment? environment = null) =>
        new()
        {
            RunId = runId,
            PlanHash = "sha256:plan-hash",
            Tasks = tasks,
            Environment = environment
        };

    private static TaskJournalEntry TaskEntry(JournalTaskStatus status, string? bucket, params AttemptRecord[] attempts) =>
        new()
        {
            Status = status,
            Bucket = bucket,
            Attempts = attempts
        };

    private static AttemptRecord Attempt(
        int attempt,
        AttemptOutcome outcome,
        DateTimeOffset startedAt,
        AttemptProvenance? provenance = null,
        int? turns = null,
        AttemptSegments? segments = null) =>
        new()
        {
            Attempt = attempt,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(5),
            Outcome = outcome,
            LogDir = "",
            Provenance = provenance,
            Turns = turns,
            Segments = segments
        };

    // --- assertions ------------------------------------------------------------------------------

    private static TelemetryRow[] AllRows(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<TelemetryRow>(line, TelemetryCorpusStore.JsonOptions)
                    ?? throw new InvalidOperationException("row did not round-trip to a TelemetryRow"))
                .ToArray()
            : [];
}
