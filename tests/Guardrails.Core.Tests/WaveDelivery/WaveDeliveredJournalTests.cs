using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// The wave delivery record (design 39 §4 "The record, pinned at review" / §5 "Wiring and halts"):
/// <c>run.json</c>'s <c>waves.&lt;dir&gt;.delivered</c>, its status tokens, and its ONE write path,
/// <see cref="RunJournal.RecordWaveDelivery"/>.
///
/// <para><b>Stubs, not wiring (task 09).</b> <see cref="WaveDeliveredRecord"/>'s getters all throw
/// <see cref="NotImplementedException"/>, its two new status tokens
/// (<see cref="WaveDeliveryStatus"/>) and the new <see cref="DeliveryOutcome.TrialGateFailed"/> outcome are
/// unregistered in <see cref="JournalJson"/>, and <see cref="RunJournal.RecordWaveDelivery"/> itself throws.
/// Every test below that touches the record or calls the write path is therefore RED on this tree; task 10
/// makes them green without editing this file. Who WRITES the record around a REAL Scheduler delivery — the
/// running-then-settled sequencing, resume restoration, and the <c>WaveDelivered</c> event — belongs to
/// tasks 28/29, not here.</para>
///
/// <para><see cref="AWaveEntryWithoutADelivery_OmitsTheKey"/> is the one exception: <see cref="WaveJournalEntry.Delivered"/>
/// is a working nullable container in the stub, so a wave entry that never sets it already serializes
/// correctly — green by construction, not coupled to the stubbed record.</para>
/// </summary>
public sealed class WaveDeliveredJournalTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "gr39-wdj-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Delivered_RoundTripsThroughTheJournalJson()
    {
        var startedAt = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 9, 14, 10, 5, 0, TimeSpan.Zero);
        var original = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = startedAt,
            At = at,
            Commit = "deadbeefcafe",
            Outcome = DeliveryOutcome.FastForwarded,
            Covers = ["wave-01-shared-dto", "wave-02-issue-510"]
        };
        var entry = new WaveJournalEntry { Status = WaveStatus.Completed, Delivered = original };

        string json = JsonSerializer.Serialize(entry, JournalJson.Options);

        Assert.Contains("\"delivered\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startedAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"covers\"", json, StringComparison.Ordinal);

        WaveJournalEntry roundTripped = JsonSerializer.Deserialize<WaveJournalEntry>(json, JournalJson.Options)!;
        WaveDeliveredRecord after = roundTripped.Delivered!;

        // Member by member, never Assert.Equal(original, after) — WaveDeliveredRecord's compiler-generated
        // equality would compare Covers by reference, and the deserialized list is never the same instance.
        Assert.Equal(original.Status, after.Status);
        Assert.Equal(original.StartedAt, after.StartedAt);
        Assert.Equal(original.At, after.At);
        Assert.Equal(original.Commit, after.Commit);
        Assert.Equal(original.Outcome, after.Outcome);
        Assert.Equal(original.Detail, after.Detail);
        Assert.Equal(original.Covers, after.Covers);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Delivered_CarriesAtCommitAndCovers()
    {
        var startedAt = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 9, 14, 9, 3, 0, TimeSpan.Zero);
        var record = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = startedAt,
            At = at,
            Commit = "cafebabe1234",
            Outcome = DeliveryOutcome.FastForwarded,
            Covers = ["wave-01-a", "wave-02-b"]
        };
        var entry = new WaveJournalEntry { Status = WaveStatus.Completed, Delivered = record };

        string json = JsonSerializer.Serialize(entry, JournalJson.Options);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement delivered = doc.RootElement.GetProperty("delivered");

        Assert.Equal(at, delivered.GetProperty("at").GetDateTimeOffset());
        Assert.Equal("cafebabe1234", delivered.GetProperty("commit").GetString());
        Assert.Equal("fast-forwarded", delivered.GetProperty("outcome").GetString());

        JsonElement covers = delivered.GetProperty("covers");
        Assert.Equal(2, covers.GetArrayLength());
        Assert.Equal("wave-01-a", covers[0].GetString());
        Assert.Equal("wave-02-b", covers[1].GetString());
    }

    /// <summary>
    /// Rejects System.Text.Json's default numeric enum output (no converter is registered for
    /// <see cref="WaveDeliveryStatus"/> until task 10) and a converter that only writes, never reads.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void EveryStatusToken_RoundTrips()
    {
        (WaveDeliveryStatus Status, string Token)[] cases =
        [
            (WaveDeliveryStatus.Running, "running"),
            (WaveDeliveryStatus.Delivered, "delivered"),
            (WaveDeliveryStatus.Refused, "refused"),
            (WaveDeliveryStatus.Suppressed, "suppressed")
        ];

        foreach ((WaveDeliveryStatus status, string token) in cases)
        {
            string json = JsonSerializer.Serialize(status, JournalJson.Options);
            Assert.Equal($"\"{token}\"", json);

            WaveDeliveryStatus roundTripped = JsonSerializer.Deserialize<WaveDeliveryStatus>(json, JournalJson.Options);
            Assert.Equal(status, roundTripped);
        }
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RecordWaveDelivery_ReplacesTheRecordAndPersists()
    {
        const string waveDir = "wave-01-scaffold";
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordWaveEntry(waveDir, new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.Passed,
            PlanHash = "sha256:aaa",
            EvaluatedAt = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)
        });

        var running = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Running,
            StartedAt = new DateTimeOffset(2026, 9, 14, 8, 5, 0, TimeSpan.Zero),
            Covers = [waveDir]
        };
        journal.RecordWaveDelivery(waveDir, running);

        var delivered = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = running.StartedAt,
            At = new DateTimeOffset(2026, 9, 14, 8, 6, 0, TimeSpan.Zero),
            Commit = "deadbeefcafe",
            Outcome = DeliveryOutcome.FastForwarded,
            Covers = [waveDir]
        };
        journal.RecordWaveDelivery(waveDir, delivered);

        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;
        WaveJournalEntry waveEntry = onDisk.Waves![waveDir];

        // Exactly the DELIVERED record is there — not the superseded running one, and not both.
        WaveDeliveredRecord persisted = waveEntry.Delivered!;
        Assert.Equal(delivered.Status, persisted.Status);
        Assert.Equal(delivered.StartedAt, persisted.StartedAt);
        Assert.Equal(delivered.At, persisted.At);
        Assert.Equal(delivered.Commit, persisted.Commit);
        Assert.Equal(delivered.Outcome, persisted.Outcome);
        Assert.Equal(delivered.Detail, persisted.Detail);
        Assert.Equal(delivered.Covers, persisted.Covers);

        // The wave's status and entry marker, set by RecordWaveEntry before either delivery write, are
        // untouched by RecordWaveDelivery.
        Assert.Equal(WaveStatus.Running, waveEntry.Status);
        Assert.NotNull(waveEntry.Entry);
        Assert.Equal(PlanPhaseStatus.Passed, waveEntry.Entry!.Status);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void AWaveEntryWithoutADelivery_OmitsTheKey()
    {
        var entry = new WaveJournalEntry { Status = WaveStatus.Completed };

        string json = JsonSerializer.Serialize(entry, JournalJson.Options);
        Assert.DoesNotContain("\"delivered\"", json, StringComparison.Ordinal);

        WaveJournalEntry roundTripped = JsonSerializer.Deserialize<WaveJournalEntry>(json, JournalJson.Options)!;
        Assert.Null(roundTripped.Delivered);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ARunningRecord_WritesNoSettledKeys()
    {
        var startedAt = new DateTimeOffset(2026, 9, 14, 7, 0, 0, TimeSpan.Zero);
        var record = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Running,
            StartedAt = startedAt,
            Covers = ["wave-01-a"]
        };
        var entry = new WaveJournalEntry { Status = WaveStatus.Running, Delivered = record };

        string json = JsonSerializer.Serialize(entry, JournalJson.Options);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement delivered = doc.RootElement.GetProperty("delivered");

        Assert.Equal("running", delivered.GetProperty("status").GetString());
        Assert.Equal(startedAt, delivered.GetProperty("startedAt").GetDateTimeOffset());
        JsonElement covers = delivered.GetProperty("covers");
        Assert.Equal(1, covers.GetArrayLength());
        Assert.Equal("wave-01-a", covers[0].GetString());

        // Absent, not present-with-null: the four settled-only members carry no key at all.
        Assert.False(delivered.TryGetProperty("at", out _));
        Assert.False(delivered.TryGetProperty("commit", out _));
        Assert.False(delivered.TryGetProperty("outcome", out _));
        Assert.False(delivered.TryGetProperty("detail", out _));
    }

    /// <summary>
    /// Rejects System.Text.Json's numeric output, a drifting spelling, and a token added to the writer
    /// only — <c>JournalJson</c>'s converter throws on an unregistered member in BOTH directions, so the
    /// next resume that reads this token back would throw.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void TheTrialGateFailedOutcome_RoundTrips()
    {
        string json = JsonSerializer.Serialize(DeliveryOutcome.TrialGateFailed, JournalJson.Options);
        Assert.Equal("\"trial-gate-failed\"", json);

        DeliveryOutcome roundTripped = JsonSerializer.Deserialize<DeliveryOutcome>(json, JournalJson.Options);
        Assert.Equal(DeliveryOutcome.TrialGateFailed, roundTripped);
    }

    /// <summary>
    /// Review round 5, <c>d39-rewind-delivered-wave</c>: a delivery cannot be undone, so a rewind must not
    /// forget it. Rejects today's <see cref="RunJournal.ResetWaveToPending"/>, which replaces the whole
    /// entry with a bare pending one and would make already-shipped work read as held.
    /// </summary>
    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ResettingADeliveredWave_KeepsItsDeliveryRecord()
    {
        const string waveDir = "wave-01-scaffold";
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordWaveEntry(waveDir, new PlanPreflightsSection
        {
            Status = PlanPhaseStatus.Passed,
            PlanHash = "sha256:aaa",
            EvaluatedAt = new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero)
        });
        journal.RecordWaveExit(waveDir, new PlanGuardrailsSection
        {
            Status = PlanPhaseStatus.Passed,
            PlanHash = "sha256:aaa"
        });
        journal.RecordWaveCompleted(waveDir, "sha256:bbb", "deadbeefsha");

        var delivered = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = new DateTimeOffset(2026, 9, 14, 6, 10, 0, TimeSpan.Zero),
            At = new DateTimeOffset(2026, 9, 14, 6, 11, 0, TimeSpan.Zero),
            Commit = "cafebabe5678",
            Outcome = DeliveryOutcome.FastForwarded,
            Covers = [waveDir]
        };
        journal.RecordWaveDelivery(waveDir, delivered);

        journal.ResetWaveToPending(waveDir);

        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;
        WaveJournalEntry waveEntry = onDisk.Waves![waveDir];

        Assert.Equal(WaveStatus.Pending, waveEntry.Status);
        Assert.Null(waveEntry.DefinitionHash);
        Assert.Null(waveEntry.MarkerSha);
        Assert.Null(waveEntry.Entry);
        Assert.Null(waveEntry.Exit);

        // Member by member, never Assert.Equal(delivered, preserved) — see
        // Delivered_RoundTripsThroughTheJournalJson's comment on why Covers cannot use record equality.
        WaveDeliveredRecord? preserved = waveEntry.Delivered;
        Assert.NotNull(preserved);
        Assert.Equal(delivered.Status, preserved!.Status);
        Assert.Equal(delivered.StartedAt, preserved.StartedAt);
        Assert.Equal(delivered.At, preserved.At);
        Assert.Equal(delivered.Commit, preserved.Commit);
        Assert.Equal(delivered.Outcome, preserved.Outcome);
        Assert.Equal(delivered.Detail, preserved.Detail);
        Assert.Equal(delivered.Covers, preserved.Covers);
    }

    private PlanDefinition BuildPlan()
    {
        string planDir = Path.Combine(_tempDir, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        var task = new TaskNode
        {
            Id = "01-task",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "x", Kind = ActionKind.Script }]
        };

        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task],
            Workspace = planDir
        };
    }
}
