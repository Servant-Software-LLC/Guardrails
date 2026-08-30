using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The journal-to-corpus ETL (charter §3.1 "two grains, both recorded",
/// <c>model-evidence-and-graduation</c>, #535): turns a <see cref="JournalDocument"/> into corpus rows
/// through <see cref="TelemetryCorpusStore"/>. Six behaviours, each pinned to an exact method name the
/// red-census guardrail binds to.
///
/// <para><b>TDD red.</b> Every test here calls <see cref="TelemetryIngest.Ingest"/>, which throws
/// <see cref="NotImplementedException"/> until <c>06-implement-journal-etl</c> fills it — so the whole
/// file is red, and none of it can be green by coincidence with a stub's default.</para>
///
/// <para><b>Two grains, one row shape.</b> <see cref="TelemetryRow"/> has no dedicated task-grain
/// columns (task 01 scoped it to the attempt grain), so the ETL writes BOTH grains as
/// <see cref="TelemetryRow"/> instances and the tests tell them apart the same way:
/// <see cref="TelemetryRow.Attempt"/> <c>== 0</c> is the one task row per task per run; <c>&gt;= 1</c> is
/// a real attempt row. A guardrail-failed attempt's classified kind rides the existing
/// <see cref="TelemetryRow.Outcome"/> token rather than a new column — see
/// <see cref="Ingest_GuardrailFailedRows_CarryTheClassifiedFailureKind"/>.</para>
///
/// <para>Every test points the store at its OWN fresh temp directory and deletes it afterwards, and
/// writes any <c>feedback.md</c> fixture under its own fresh temp log root (never a real run's
/// <c>logs/</c>, which may be pruned).</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryIngestTests : IDisposable
{
    private readonly string corpusRoot =
        Path.Combine(Path.GetTempPath(), "guardrails-telemetry-ingest-tests", Guid.NewGuid().ToString("N"));

    private readonly string logRoot =
        Path.Combine(Path.GetTempPath(), "guardrails-telemetry-ingest-tests-logs", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(corpusRoot))
        {
            Directory.Delete(corpusRoot, recursive: true);
        }

        if (Directory.Exists(logRoot))
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    // --- 1. one task row per task per run -----------------------------------------------------------

    /// <summary>
    /// Two tasks in the same run each produce exactly one task row (<see cref="TelemetryRow.Attempt"/>
    /// <c>== 0</c>), carrying that task's terminal outcome and its DECLARED tier + origin — sourced from
    /// the task's first attempt's provenance, since <c>run.json</c> journals tier/tierSource only per
    /// attempt. Two different tiers/origins on the two tasks guards against a naive implementation that
    /// copies one task's route onto every task row.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_EmitsOneTaskRowPerTaskPerRun()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-multi-task", new Dictionary<string, TaskJournalEntry>
        {
            ["01-a"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-a",
                Attempt(1, AttemptOutcome.Succeeded, started,
                    provenance: new AttemptProvenance { Tier = "easy", TierSource = TierSource.Task })),
            ["02-b"] = TaskEntry(
                JournalTaskStatus.Failed,
                "sha256:task-b",
                Attempt(1, AttemptOutcome.ActionFailed, started,
                    provenance: new AttemptProvenance { Tier = "hard", TierSource = TierSource.PlanDefault }))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow[] taskRows = AllRows(corpusRoot).Where(r => r.Attempt == 0).ToArray();
        Assert.Equal(2, taskRows.Length);

        TelemetryRow rowA = Assert.Single(taskRows, r => r.TaskId == "01-a");
        Assert.Equal("run-multi-task", rowA.RunId);
        Assert.Equal("succeeded", rowA.Outcome);
        Assert.Equal("easy", rowA.Tier);
        Assert.Equal(JournalJson.TierSourceToken(TierSource.Task), rowA.TierSource);
        Assert.Equal("guardrails", rowA.Repo);

        TelemetryRow rowB = Assert.Single(taskRows, r => r.TaskId == "02-b");
        Assert.Equal("failed", rowB.Outcome);
        Assert.Equal("hard", rowB.Tier);
        Assert.Equal(JournalJson.TierSourceToken(TierSource.PlanDefault), rowB.TierSource);
    }

    // --- 2. one attempt row per attempt, retries included -------------------------------------------

    /// <summary>
    /// A task retried once produces TWO attempt rows, not one — folding down to the final (successful)
    /// attempt would under-report by exactly the retry spend, which is the spend this corpus most needs
    /// to see. The task itself still yields exactly one task row.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_EmitsOneAttemptRowPerAttempt_RetriesIncluded()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-retries", new Dictionary<string, TaskJournalEntry>
        {
            ["03-retried"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-c",
                Attempt(1, AttemptOutcome.GuardrailFailed, started,
                    failedGuardrails: [new FailedGuardrail { Name = "02-tests-pass", Reason = "1 test failed" }]),
                Attempt(2, AttemptOutcome.Succeeded, started.AddHours(1)))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow[] attemptRows = AllRows(corpusRoot)
            .Where(r => r.TaskId == "03-retried" && r.Attempt != 0)
            .OrderBy(r => r.Attempt)
            .ToArray();

        Assert.Equal(2, attemptRows.Length);
        Assert.Equal(1, attemptRows[0].Attempt);
        Assert.Equal("guardrail-failed", attemptRows[0].Outcome);
        Assert.Equal(2, attemptRows[1].Attempt);
        Assert.Equal(JournalJson.OutcomeToken(AttemptOutcome.Succeeded), attemptRows[1].Outcome);

        Assert.Single(AllRows(corpusRoot), r => r.TaskId == "03-retried" && r.Attempt == 0);
    }

    // --- 3. route provenance carried onto the attempt row -------------------------------------------

    /// <summary>
    /// The attempt's resolved route — model, runner, kind, tier, tierSource, effort — lands on its
    /// attempt row verbatim.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_CarriesRouteProvenanceOntoTheAttemptRow()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero);

        var provenance = new AttemptProvenance
        {
            Model = "claude-sonnet-5",
            RequestedModel = "claude-opus-5",
            Runner = "default",
            Kind = "claude",
            Tier = "hard",
            TierSource = TierSource.PlanDefault,
            Effort = "high"
        };

        JournalDocument journal = Journal("run-route", new Dictionary<string, TaskJournalEntry>
        {
            ["04-route"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-d",
                Attempt(1, AttemptOutcome.Succeeded, started, provenance: provenance))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow row = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "04-route" && r.Attempt == 1);

        Assert.Equal("claude-sonnet-5", row.Model);
        Assert.Equal("default", row.Runner);
        Assert.Equal("claude", row.Kind);
        Assert.Equal("hard", row.Tier);
        Assert.Equal(JournalJson.TierSourceToken(TierSource.PlanDefault), row.TierSource);
        Assert.Equal("high", row.Effort);
    }

    // --- 4. unreported cost and tokens stay null, not zero ------------------------------------------

    /// <summary>
    /// An attempt whose runner reported no cost and no usage lands as <c>null</c> in both fields, never
    /// <c>0</c> — the same null-versus-zero distinction <c>JournalTierSpend</c> already draws. Paired
    /// with a sibling attempt that DID report cost/usage, so a naive <c>?? 0</c> implementation and a
    /// correct nullable-passthrough implementation disagree.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_UnreportedCostAndTokens_StayNull_NotZero()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-cost", new Dictionary<string, TaskJournalEntry>
        {
            ["05-unreported"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-e",
                Attempt(1, AttemptOutcome.Succeeded, started, costUsd: null, usage: null)),
            ["06-reported"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-f",
                Attempt(1, AttemptOutcome.Succeeded, started,
                    costUsd: 2.50m, usage: new AttemptUsage { InputTokens = 1000, OutputTokens = 200 }))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow unreported = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "05-unreported" && r.Attempt == 1);
        Assert.Null(unreported.CostUsd);
        Assert.Null(unreported.InputTokens);
        Assert.Null(unreported.OutputTokens);

        TelemetryRow reported = Assert.Single(AllRows(corpusRoot), r => r.TaskId == "06-reported" && r.Attempt == 1);
        Assert.Equal(2.50m, reported.CostUsd);
        Assert.Equal(1000, reported.InputTokens);
        Assert.Equal(200, reported.OutputTokens);
    }

    // --- 5. re-ingesting the same run adds no rows --------------------------------------------------

    /// <summary>
    /// Ingesting the identical journal twice leaves the corpus unchanged — what makes backfilling a
    /// directory of plans safe to re-run. Relies on nothing but the store's own <c>(runId, taskId,
    /// attempt)</c> idempotency, for both grains.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_SameRunTwice_AddsNoDuplicateRows()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero);

        JournalDocument journal = Journal("run-reingest", new Dictionary<string, TaskJournalEntry>
        {
            ["07-idem"] = TaskEntry(
                JournalTaskStatus.Succeeded,
                "sha256:task-g",
                Attempt(1, AttemptOutcome.GuardrailFailed, started,
                    failedGuardrails: [new FailedGuardrail { Name = "x", Reason = "y" }]),
                Attempt(2, AttemptOutcome.Succeeded, started.AddHours(1)))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");
        int firstCount = AllRows(corpusRoot).Length;
        Assert.True(firstCount > 0);

        TelemetryIngest.Ingest(journal, store, "guardrails");
        int secondCount = AllRows(corpusRoot).Length;

        Assert.Equal(firstCount, secondCount);
    }

    // --- 6. guardrail-failed rows carry the classified kind ------------------------------------------

    /// <summary>
    /// Three attempts, three verdicts: a REAL guardrail failure (non-empty <c>failedGuardrails</c>)
    /// stays the bare <c>guardrail-failed</c> token; a write-scope-violation <c>feedback.md</c>
    /// classifies to a refined <c>guardrail-failed:write-scope-violation</c> token; a missing log site
    /// classifies to <c>guardrail-failed:undifferentiated</c> — the honest "we don't know", never
    /// guessed as one of the named kinds.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void Ingest_GuardrailFailedRows_CarryTheClassifiedFailureKind()
    {
        var store = new TelemetryCorpusStore(corpusRoot);
        var started = new DateTimeOffset(2026, 5, 6, 9, 0, 0, TimeSpan.Zero);

        string writeScopeLogDir = WriteFeedback(
            "# Task 'demo' failed its guardrails\n\n" +
            "## Write-scope violation\n\n" +
            "The following path(s) were modified but fall OUTSIDE this task's declared writeScope:\n\n" +
            "- `docs/oops.md`\n\n" +
            "The harness has already reverted those files to their pre-attempt state.\n");
        string undifferentiatedLogDir = Path.Combine(logRoot, Guid.NewGuid().ToString("N")); // never created

        JournalDocument journal = Journal("run-classify", new Dictionary<string, TaskJournalEntry>
        {
            ["08-real"] = TaskEntry(
                JournalTaskStatus.NeedsHuman,
                "sha256:task-h",
                Attempt(1, AttemptOutcome.GuardrailFailed, started,
                    failedGuardrails: [new FailedGuardrail { Name = "02-tests-pass", Reason = "1 test failed" }])),
            ["09-writescope"] = TaskEntry(
                JournalTaskStatus.NeedsHuman,
                "sha256:task-i",
                Attempt(1, AttemptOutcome.GuardrailFailed, started, logDir: writeScopeLogDir)),
            ["10-undifferentiated"] = TaskEntry(
                JournalTaskStatus.NeedsHuman,
                "sha256:task-j",
                Attempt(1, AttemptOutcome.GuardrailFailed, started, logDir: undifferentiatedLogDir))
        });

        TelemetryIngest.Ingest(journal, store, "guardrails");

        TelemetryRow[] rows = AllRows(corpusRoot).Where(r => r.Attempt == 1).ToArray();

        Assert.Equal("guardrail-failed", Assert.Single(rows, r => r.TaskId == "08-real").Outcome);
        Assert.Equal("guardrail-failed:write-scope-violation", Assert.Single(rows, r => r.TaskId == "09-writescope").Outcome);
        Assert.Equal("guardrail-failed:undifferentiated", Assert.Single(rows, r => r.TaskId == "10-undifferentiated").Outcome);
    }

    // --- fixtures --------------------------------------------------------------------------------

    private static JournalDocument Journal(string runId, Dictionary<string, TaskJournalEntry> tasks) =>
        new()
        {
            RunId = runId,
            PlanHash = "sha256:plan-hash",
            Tasks = tasks
        };

    private static TaskJournalEntry TaskEntry(JournalTaskStatus status, string? definitionHash, params AttemptRecord[] attempts) =>
        new()
        {
            Status = status,
            DefinitionHash = definitionHash,
            Attempts = attempts
        };

    private static AttemptRecord Attempt(
        int attempt,
        AttemptOutcome outcome,
        DateTimeOffset startedAt,
        string logDir = "",
        AttemptProvenance? provenance = null,
        decimal? costUsd = null,
        AttemptUsage? usage = null,
        IReadOnlyList<FailedGuardrail>? failedGuardrails = null) =>
        new()
        {
            Attempt = attempt,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(5),
            Outcome = outcome,
            LogDir = logDir,
            Provenance = provenance,
            CostUsd = costUsd,
            Usage = usage,
            FailedGuardrails = failedGuardrails ?? []
        };

    /// <summary>Creates a fresh temp log dir under <see cref="logRoot"/> containing the given <c>feedback.md</c> text.</summary>
    private string WriteFeedback(string feedback)
    {
        string dir = Path.Combine(logRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "feedback.md"), feedback);
        return dir;
    }

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
