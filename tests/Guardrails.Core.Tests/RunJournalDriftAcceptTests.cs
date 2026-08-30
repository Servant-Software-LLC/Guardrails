using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #545 — the accept-and-continue half of the definition-drift prompt. Found on the #535 dogfood:
/// a 14-task plan ran 12/14 green, someone edited task 01's prompt mid-run, and the resume offered
/// exactly two options — rewind 12 commits and re-run all 14 (~$26 / ~2 hours to gain one test, with
/// ~$4 / ~15 minutes of work left), or decline, which exited having done nothing and said nothing about
/// how to get moving again.
///
/// <para><b>What is tested here, and what is not.</b> The three-way prompt itself is unreachable from a
/// test: it is guarded on <c>Console.IsInputRedirected</c>, which is always true under the test runner,
/// which is why the existing <c>DefinitionDriftCliTests</c> only ever exercise the non-interactive halt.
/// So the prompt WORDING is verified by reading, not by execution, and that is stated in the review
/// rather than left implied. What IS tested is the part that can silently do the wrong thing:
/// <see cref="RunJournal.RecordDriftAccepted"/>, whose whole contract is *move one field and touch
/// nothing else*.</para>
///
/// <para>The dangerous failure is not that it fails to re-baseline — that would be caught immediately by
/// the run halting again. It is that it re-baselines AND quietly resets something else, so the operator
/// who chose "do not re-run this task" gets it re-run, or gets its attempt history rewritten, and the
/// journal stops describing what actually happened.</para>
/// </summary>
public sealed class RunJournalDriftAcceptTests : IDisposable
{
    private readonly string _tempDir;

    public RunJournalDriftAcceptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gr-545-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The whole point: the recorded definition hash moves to the new one, and the task is STILL recorded
    /// as succeeded with its merge sequence intact. An accept that reset the status would silently re-run
    /// the very task the operator chose not to re-run.
    /// </summary>
    [Fact]
    public void RecordDriftAccepted_RebaselinesTheHash_AndTouchesNothingElse()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 7, definitionHash: "sha256:old");

        journal.RecordDriftAccepted("01-task", "sha256:new");

        // Read from DISK, not from the instance that made the change - the journal's job is to be durable,
        // and an in-memory assertion would pass over a write that never landed.
        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;
        TaskJournalEntry entry = onDisk.Tasks["01-task"];

        Assert.Equal("sha256:new", entry.DefinitionHash);
        Assert.Equal(TaskStatus.Succeeded, entry.Status);
        Assert.Equal(7, entry.MergeSequence);
    }

    /// <summary>
    /// The attempt history is the record of what the run actually did, and accepting a drift changes
    /// nothing about that — the task really was attempted, really did succeed, and really was built
    /// against the OLD definition. Dropping the attempts would erase the only evidence of the cost that
    /// was already paid.
    /// </summary>
    [Fact]
    public void RecordDriftAccepted_LeavesTheAttemptHistoryIntact()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordAttempt("01-task", Attempt(1, AttemptOutcome.GuardrailFailed), TaskStatus.Running);
        journal.RecordAttempt("01-task", Attempt(2, AttemptOutcome.Succeeded), TaskStatus.Succeeded,
            definitionHash: "sha256:old");

        journal.RecordDriftAccepted("01-task", "sha256:new");

        TaskJournalEntry entry = RunJournal.LoadOrCreate(plan).Document.Tasks["01-task"];
        Assert.Equal(2, entry.Attempts.Count);
        Assert.Equal("sha256:new", entry.DefinitionHash);
    }

    /// <summary>
    /// A task with no journal entry has no baseline to move, so the call is a no-op rather than an
    /// invention. Creating an entry here would fabricate a success the run never had — the corpus of
    /// failures this repo keeps meeting is full of mechanisms that helpfully filled in a blank.
    /// </summary>
    [Fact]
    public void RecordDriftAccepted_ForAnUnknownTask_InventsNothing()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDriftAccepted("99-never-existed", "sha256:new");

        Assert.DoesNotContain("99-never-existed", RunJournal.LoadOrCreate(plan).Document.Tasks.Keys);
    }

    /// <summary>
    /// Accepting a drift is a trade — the delivered artifact predates its own definition — and after the
    /// re-baseline NOTHING in <c>tasks{}</c> says so: the hashes now match and the task reads as cleanly
    /// green. The <c>decisions[]</c> entry is therefore the ONLY durable record that the trade happened,
    /// which is why the caller appends one and why this test pins that it survives to disk.
    /// </summary>
    [Fact]
    public void TheDriftAcceptedDecision_IsTheOnlyDurableRecordOfTheTrade()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordSettle("01-task", TaskStatus.Succeeded, definitionHash: "sha256:old");

        journal.RecordDriftAccepted("01-task", "sha256:new");
        journal.RecordDecision(new DecisionEntry
        {
            Boundary = "drift",
            Policy = "prompt",
            Decision = DecisionTokens.DriftAccepted,
            Subject = "01-task",
            Headline = "Definition drift ACCEPTED (not re-run)",
        });

        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;

        // The task itself is now indistinguishable from one built against the current definition...
        Assert.Equal("sha256:new", onDisk.Tasks["01-task"].DefinitionHash);

        // ...so the decision entry is the whole audit trail.
        DecisionEntry decision = Assert.Single(onDisk.Decisions ?? []);
        Assert.Equal(DecisionTokens.DriftAccepted, decision.Decision);
        Assert.Equal("01-task", decision.Subject);
    }


    private static AttemptRecord Attempt(int n, AttemptOutcome outcome) => new()
    {
        Attempt = n,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow,
        Outcome = outcome,
        LogDir = "logs/x"
    };

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
