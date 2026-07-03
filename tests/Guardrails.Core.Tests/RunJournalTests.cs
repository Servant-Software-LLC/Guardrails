using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="RunJournal"/>: the SSOT §7 resume matrix (every status →
/// expected on reload), crash simulation (<c>running</c> → <c>pending</c> with attempt
/// numbering continuing), and the planHash mismatch warning path.
/// </summary>
public sealed class RunJournalTests : IDisposable
{
    private readonly string _planDir;

    public RunJournalTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    // --- resume matrix ----------------------------------------------------------------

    [Theory]
    [InlineData(JournalTaskStatus.Succeeded, JournalTaskStatus.Succeeded)] // terminal — skipped
    [InlineData(JournalTaskStatus.Pending, JournalTaskStatus.Pending)]
    [InlineData(JournalTaskStatus.Failed, JournalTaskStatus.Pending)]      // fresh budget
    [InlineData(JournalTaskStatus.NeedsHuman, JournalTaskStatus.Pending)]  // fresh budget
    [InlineData(JournalTaskStatus.Blocked, JournalTaskStatus.Pending)]     // fresh budget
    [InlineData(JournalTaskStatus.Running, JournalTaskStatus.Pending)]     // crash → pending
    public void Resume_NormalizesStatusPerSsot(JournalTaskStatus stored, JournalTaskStatus expected)
    {
        PlanDefinition plan = BuildPlan(out string currentHash);
        WriteJournalOnDisk(currentHash, ("01-task", stored, Attempts: 1));

        RunJournal resumed = RunJournal.LoadOrCreate(plan);

        Assert.Equal(expected, resumed.StatusOf("01-task"));
    }

    // --- issue #190 part 2: resume is OUTCOME-AGNOSTIC (documented, deliberately not tightened) -----

    [Theory]
    [InlineData(AttemptOutcome.RateLimited)]        // self-resolving — SHOULD auto-retry
    [InlineData(AttemptOutcome.NeedsHuman)]          // the needsHuman prompt short-circuit — a human decision
    [InlineData(AttemptOutcome.PermissionDenied)]    // a permission wall — needs a config/grant fix first
    [InlineData(AttemptOutcome.TaskPreflightFailed)] // a dependency-delivery gate — needs an upstream fix
    [InlineData(AttemptOutcome.GuardrailFailed)]     // a genuinely exhausted retry budget
    public void Resume_NeedsHuman_ResetsToPending_RegardlessOfLastAttemptOutcome(AttemptOutcome lastOutcome)
    {
        // SSOT section 7 / issue #190 part 2: RunJournal.ResumeStatus is a pure function of the
        // journal STATUS string alone — it never inspects the last recorded AttemptRecord.Outcome. A
        // task halted for a self-resolving reason (rate-limited) and a task halted because a human
        // must actually act (needsHuman / permission-denied / task-preflight-failed / an exhausted
        // guardrail-failure budget) are ALL reset to `pending` with a FRESH retry budget on a plain
        // resume — proving the documented "resume does not distinguish WHY" reality, not just
        // asserting it in prose. A future tightening would make this theory's cases diverge.
        PlanDefinition plan = BuildPlan(out string currentHash);
        WriteJournalOnDisk(currentHash, ("01-task", JournalTaskStatus.NeedsHuman, Attempts: 1, lastOutcome));

        RunJournal resumed = RunJournal.LoadOrCreate(plan);

        Assert.Equal(JournalTaskStatus.Pending, resumed.StatusOf("01-task"));
        // Attempt history (including the reason it halted) is preserved, not erased.
        AttemptRecord preserved = Assert.Single(resumed.Document.Tasks["01-task"].Attempts);
        Assert.Equal(lastOutcome, preserved.Outcome);
    }

    [Fact]
    public void Resume_CrashedRunning_ContinuesAttemptNumbering()
    {
        PlanDefinition plan = BuildPlan(out string currentHash);
        // A task that crashed mid-attempt-2: status running, two attempts recorded.
        WriteJournalOnDisk(currentHash, ("01-task", JournalTaskStatus.Running, Attempts: 2));

        RunJournal resumed = RunJournal.LoadOrCreate(plan);

        Assert.Equal(JournalTaskStatus.Pending, resumed.StatusOf("01-task"));
        // Attempt history preserved, so the NEXT attempt is 3 — numbering continues.
        Assert.Equal(3, resumed.NextAttemptNumber("01-task"));
    }

    [Fact]
    public void FreshJournal_SeedsAllPlanTasksPending_AndNextAttemptIsOne()
    {
        PlanDefinition plan = BuildPlan(out _);

        RunJournal journal = RunJournal.LoadOrCreate(plan);

        Assert.Equal(JournalTaskStatus.Pending, journal.StatusOf("01-task"));
        Assert.Equal(1, journal.NextAttemptNumber("01-task"));
        Assert.False(journal.PlanHashMismatch);
        Assert.True(File.Exists(RunJournal.PathFor(_planDir)));
    }

    [Fact]
    public void PlanHashMismatch_IsFlagged_AndRunContinues()
    {
        PlanDefinition plan = BuildPlan(out _);
        WriteJournalOnDisk("sha256:deadbeef", ("01-task", JournalTaskStatus.Succeeded, Attempts: 1));

        RunJournal resumed = RunJournal.LoadOrCreate(plan);

        Assert.True(resumed.PlanHashMismatch);
        Assert.Equal("sha256:deadbeef", resumed.PreviousPlanHash);
        // Continues: succeeded task is still treated as terminal.
        Assert.Equal(JournalTaskStatus.Succeeded, resumed.StatusOf("01-task"));
    }

    [Fact]
    public void PlanHashMatch_IsNotFlagged()
    {
        PlanDefinition plan = BuildPlan(out string currentHash);
        WriteJournalOnDisk(currentHash, ("01-task", JournalTaskStatus.Succeeded, Attempts: 1));

        RunJournal resumed = RunJournal.LoadOrCreate(plan);

        Assert.False(resumed.PlanHashMismatch);
        Assert.Null(resumed.PreviousPlanHash);
    }

    // --- transitions + counters -------------------------------------------------------

    [Fact]
    public void RecordAttempt_WithMergeSequence_AdvancesCounterMonotonically()
    {
        PlanDefinition plan = BuildPlan(out _);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        long reserved = journal.ReserveMergeSequence();
        Assert.Equal(1, reserved);

        journal.MarkRunning("01-task");
        journal.RecordAttempt("01-task", Attempt(1, AttemptOutcome.Succeeded), JournalTaskStatus.Succeeded, reserved);

        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("01-task"));
        Assert.Equal(2, journal.NextMergeSequence);

        // The merge sequence is durably stored on the task.
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(_planDir));
        Assert.Equal(1, doc.Tasks["01-task"].MergeSequence);
    }

    [Fact]
    public void EveryTransition_IsPersistedAtomically()
    {
        PlanDefinition plan = BuildPlan(out _);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.MarkRunning("01-task");
        // Mid-run read off disk sees the running status (the journal flushes each step).
        JournalDocument midRun = JournalReader.Read(RunJournal.PathFor(_planDir));
        Assert.Equal(JournalTaskStatus.Running, midRun.Tasks["01-task"].Status);

        journal.RecordAttempt("01-task", Attempt(1, AttemptOutcome.GuardrailFailed), JournalTaskStatus.Failed);
        JournalDocument afterFail = JournalReader.Read(RunJournal.PathFor(_planDir));
        Assert.Equal(JournalTaskStatus.Failed, afterFail.Tasks["01-task"].Status);
    }

    [Fact]
    public void Journal_RoundTrips_StatusAndOutcomeStrings()
    {
        // Assert the SSOT §7 kebab-case wire strings, not just enum round-trip.
        PlanDefinition plan = BuildPlan(out _);
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordAttempt("01-task",
            Attempt(1, AttemptOutcome.InvalidFragment), JournalTaskStatus.NeedsHuman);

        string raw = File.ReadAllText(RunJournal.PathFor(_planDir));
        Assert.Contains("\"needs-human\"", raw);
        Assert.Contains("\"invalid-fragment\"", raw);
    }

    // --- helpers ----------------------------------------------------------------------

    private static AttemptRecord Attempt(int n, AttemptOutcome outcome) => new()
    {
        Attempt = n,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        ActionExitCode = 0,
        Outcome = outcome,
        LogDir = $"state/logs/01-task/attempt-{n}"
    };

    /// <summary>Build a minimal one-task plan on disk and return it plus its current plan hash.</summary>
    private PlanDefinition BuildPlan(out string currentHash)
    {
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");
        string taskDir = Path.Combine(_planDir, "tasks", "01-task");
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

        var plan = new PlanDefinition
        {
            PlanDirectory = _planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task],
            Workspace = _planDir
        };

        currentHash = PlanHash.Compute(plan);
        return plan;
    }

    /// <summary>Write a run.json directly to disk (bypassing RunJournal) to set up resume scenarios.</summary>
    private void WriteJournalOnDisk(string planHash, params (string Id, JournalTaskStatus Status, int Attempts)[] tasks) =>
        WriteJournalOnDisk(planHash, tasks.Select(t => (t.Id, t.Status, t.Attempts, AttemptOutcome.GuardrailFailed)).ToArray());

    /// <summary>
    /// As above, but the LAST recorded attempt carries <paramref name="lastOutcome"/> (issue #190 part
    /// 2) instead of the fixed <see cref="AttemptOutcome.GuardrailFailed"/> — lets a test set up a
    /// resume scenario where the halt reason varies (rate-limited vs a genuine needs-human) while the
    /// journal STATUS stays the same string either way.
    /// </summary>
    private void WriteJournalOnDisk(
        string planHash,
        params (string Id, JournalTaskStatus Status, int Attempts, AttemptOutcome LastOutcome)[] tasks)
    {
        var taskMap = new Dictionary<string, TaskJournalEntry>();
        foreach ((string id, JournalTaskStatus status, int attempts, AttemptOutcome lastOutcome) in tasks)
        {
            var records = new List<AttemptRecord>();
            for (int i = 1; i <= attempts; i++)
            {
                records.Add(Attempt(i, i == attempts ? lastOutcome : AttemptOutcome.GuardrailFailed));
            }

            taskMap[id] = new TaskJournalEntry { Status = status, Attempts = records };
        }

        var document = new JournalDocument
        {
            RunId = "test-run",
            PlanHash = planHash,
            NextMergeSequence = 1,
            Tasks = taskMap
        };

        string statePath = Path.Combine(_planDir, "state");
        Directory.CreateDirectory(statePath);
        File.WriteAllText(
            Path.Combine(statePath, "run.json"),
            JsonSerializer.Serialize(document, JournalJson.Options));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
