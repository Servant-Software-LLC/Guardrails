using System.Text.Json.Nodes;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal` — see TaskBucketJournalTests for why.
namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #798 — <c>run.json</c>'s <c>tasks.&lt;id&gt;.inFlightAttempt</c> marker (SSOT §7). Every test drives a
/// REAL <see cref="RunJournal"/> over a temp plan directory and reads the PERSISTED file back, because the marker
/// exists for readers of the file (<c>guardrails status</c>, the operator), not for the in-memory document.
/// </summary>
[Trait("Category", "Journal")]
public sealed class InFlightAttemptJournalTests : IDisposable
{
    private const string TaskId = "01-task";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr798-inflight-" + Guid.NewGuid().ToString("N"));

    public InFlightAttemptJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void MarkingAnAttempt_PersistsTheMarker_WithoutTouchingStatus()
    {
        RunJournal journal = Load();
        journal.MarkRunning(TaskId);

        journal.MarkAttemptInFlight(TaskId, 3, InFlightPhase.Action);

        TaskJournalEntry entry = ReadBack().Tasks[TaskId];
        Assert.Equal(JournalTaskStatus.Running, entry.Status);
        Assert.Equal(3, entry.InFlightAttempt!.Attempt);
        Assert.Equal(InFlightPhase.Action, entry.InFlightAttempt.Phase);

        // The wire shape the SSOT documents: camelCase key, the phase as its token.
        JsonNode marker = JsonNode.Parse(File.ReadAllText(journal.JournalPath))!["tasks"]![TaskId]!["inFlightAttempt"]!;
        Assert.Equal(3, marker["attempt"]!.GetValue<int>());
        Assert.Equal("action", marker["phase"]!.GetValue<string>());
        Assert.NotNull(marker["startedAt"]);
    }

    /// <summary>
    /// A phase change (and a transient pause's re-run) re-marks the SAME attempt: its startedAt is the first
    /// launch's, not the latest re-mark's. A different number is a different attempt and replaces the marker.
    /// </summary>
    [Fact]
    public void ReMarkingTheSameAttempt_KeepsItsStartedAt_AndADifferentNumberReplacesIt()
    {
        RunJournal journal = Load();
        var first = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 9, 1, 8, 5, 0, TimeSpan.Zero);
        var next = new DateTimeOffset(2026, 9, 1, 8, 9, 0, TimeSpan.Zero);

        journal.MarkAttemptInFlight(TaskId, 3, InFlightPhase.Action, now: first);
        journal.MarkAttemptInFlight(TaskId, 3, InFlightPhase.Guardrails, now: later);
        InFlightAttemptRecord same = ReadBack().Tasks[TaskId].InFlightAttempt!;
        Assert.Equal(3, same.Attempt);
        Assert.Equal(first, same.StartedAt);
        Assert.Equal(InFlightPhase.Guardrails, same.Phase);

        journal.MarkAttemptInFlight(TaskId, 4, InFlightPhase.Action, now: next);
        InFlightAttemptRecord replaced = ReadBack().Tasks[TaskId].InFlightAttempt!;
        Assert.Equal(4, replaced.Attempt);
        Assert.Equal(next, replaced.StartedAt);
        Assert.Equal(InFlightPhase.Action, replaced.Phase);
    }

    /// <summary>
    /// W2: a failed marker write returns its reason instead of throwing, and KEEPS the in-memory marker, so the
    /// journal's next successful persist carries it to disk.
    /// </summary>
    [Fact]
    public void AFailedMarkerPersist_ReturnsTheReason_AndTheNextPersistCarriesTheMarker()
    {
        RunJournal journal = Load();
        journal.BeforeMarkerPersist = () => throw new UnauthorizedAccessException("denied");

        string? failure = journal.MarkAttemptInFlight(TaskId, 2, InFlightPhase.Action);

        Assert.Equal("denied", failure);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);
        Assert.Equal(2, journal.Document.Tasks[TaskId].InFlightAttempt!.Attempt);

        journal.MarkRunning(TaskId);
        Assert.Equal(2, ReadBack().Tasks[TaskId].InFlightAttempt!.Attempt);
    }

    [Fact]
    public void SettlingTheAttempt_RemovesTheMarker_FromTheFile()
    {
        RunJournal journal = Load();
        journal.MarkAttemptInFlight(TaskId, 1, InFlightPhase.Settling);

        journal.RecordAttempt(TaskId, Attempt(1), JournalTaskStatus.Succeeded);

        TaskJournalEntry entry = ReadBack().Tasks[TaskId];
        Assert.Null(entry.InFlightAttempt);
        Assert.Single(entry.Attempts);
        // ABSENT, never `null` noise.
        Assert.False(((JsonObject)JsonNode.Parse(File.ReadAllText(journal.JournalPath))!["tasks"]![TaskId]!)
            .ContainsKey("inFlightAttempt"));
    }

    [Fact]
    public void TheWorktreeSettlePaths_AlsoRemoveTheMarker()
    {
        RunJournal journal = Load();

        journal.MarkAttemptInFlight(TaskId, 1, InFlightPhase.Settling);
        journal.RecordSettleWithAttempt(TaskId, Attempt(1), JournalTaskStatus.Succeeded, mergeSequence: 1);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);

        journal.MarkAttemptInFlight(TaskId, 2, InFlightPhase.Settling);
        journal.RecordSettle(TaskId, JournalTaskStatus.NeedsHuman);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);
    }

    [Fact]
    public void ResetAndBlock_RemoveTheMarker()
    {
        RunJournal journal = Load();

        journal.MarkAttemptInFlight(TaskId, 2, InFlightPhase.Action);
        journal.ResetTask(TaskId);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);

        journal.MarkAttemptInFlight(TaskId, 2, InFlightPhase.Action);
        journal.MarkBlocked(TaskId);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);
    }

    /// <summary>
    /// A marker left by a crashed process describes an attempt nobody is running; a resume's load drops it (and
    /// the resume re-runs the task under the same number, since the crashed attempt never recorded).
    /// </summary>
    [Fact]
    public void AResumeLoad_DropsAMarkerLeftByTheCrashedProcess()
    {
        RunJournal crashed = Load();
        crashed.MarkRunning(TaskId);
        crashed.MarkAttemptInFlight(TaskId, 5, InFlightPhase.Guardrails);

        RunJournal resumed = Load();

        Assert.Null(resumed.Document.Tasks[TaskId].InFlightAttempt);
        Assert.Null(ReadBack().Tasks[TaskId].InFlightAttempt);
    }

    /// <summary>Backward compatibility: a <c>run.json</c> written before #798 carries no marker and still loads.</summary>
    [Fact]
    public void AJournalWrittenBeforeTheField_StillLoads_WithNoMarker()
    {
        RunJournal journal = Load();
        journal.RecordAttempt(TaskId, Attempt(1), JournalTaskStatus.NeedsHuman);
        JsonNode doc = JsonNode.Parse(File.ReadAllText(journal.JournalPath))!;
        Assert.False(((JsonObject)doc["tasks"]![TaskId]!).ContainsKey("inFlightAttempt"));

        JournalDocument read = ReadBack();
        Assert.Null(read.Tasks[TaskId].InFlightAttempt);
        Assert.Equal(2, Load().NextAttemptNumber(TaskId));
    }

    private RunJournal Load() => RunJournal.LoadOrCreate(new PlanDefinition
    {
        PlanDirectory = _root,
        Workspace = _root,
        Config = new RunConfig { Version = 1 },
        Tasks =
        [
            new TaskNode
            {
                Id = TaskId,
                Directory = Path.Combine(_root, "tasks", TaskId),
                Description = "fixture task for InFlightAttemptJournalTests",
                Action = new ActionDefinition { Path = Path.Combine(_root, "tasks", TaskId, "action.sh"), Kind = ActionKind.Script },
                Guardrails = []
            }
        ]
    });

    private JournalDocument ReadBack() => JournalReader.Read(RunJournal.PathFor(_root));

    private static AttemptRecord Attempt(int n) => new()
    {
        Attempt = n,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = AttemptOutcome.Succeeded,
        LogDir = $"logs/run/{TaskId}/attempt-{n}"
    };
}
