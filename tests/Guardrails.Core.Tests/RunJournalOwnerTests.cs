using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #704 — <see cref="RunJournal.RecordOwner"/> and <see cref="RunJournal.RecordOwnerFinished"/>, the two
/// writes that let <c>guardrails status</c> tell a live run from a dead one.
/// <para>
/// The hazards are the ones <see cref="RunJournalDeliveryTests"/> documents for every end-of-run write: the CLI's
/// journal instance is stale by the time the run ends, and a careless write reverts the run it describes. Two more
/// are specific to ownership: a run must never mark ANOTHER process's claim finished (that would call a live run
/// over), and a resume's claim must clear the previous run's end (or a dead resume would read as finished).
/// </para>
/// </summary>
public sealed class RunJournalOwnerTests : IDisposable
{
    private readonly string _tempDir;

    public RunJournalOwnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gr-704-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static RunOwner Owner(int pid) => new()
    {
        Pid = pid,
        ProcessStartedAt = new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456 + pid),
        Host = "LAPTOP-7"
    };

    /// <summary>
    /// The start time is compared against the live process table, so it must come back from disk to the TICK — a
    /// round trip that dropped sub-second precision would make every live owner on Windows look like a reused pid.
    /// </summary>
    [Fact]
    public void TheOwner_SurvivesAReload_ToTheTick()
    {
        PlanDefinition plan = BuildPlan();
        RunOwner owner = Owner(14168);

        RunJournal.LoadOrCreate(plan).RecordOwner(owner);

        RunOwner? reloaded = RunJournal.LoadOrCreate(plan).Document.Owner;
        Assert.Equal(owner, reloaded);
        Assert.Equal(owner.ProcessStartedAt.UtcTicks, reloaded!.ProcessStartedAt.UtcTicks);
    }

    /// <summary>
    /// The CLI claims the run BEFORE the Scheduler opens its own instance with a second <c>LoadOrCreate</c>, and
    /// every Scheduler write persists that instance's whole document — so the owner must be carried by that load,
    /// or the first settled task would erase it.
    /// </summary>
    [Fact]
    public void TheSchedulersLaterLoad_CarriesTheOwnerThroughEveryWrite()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal cli = RunJournal.LoadOrCreate(plan);
        cli.RecordOwner(Owner(14168));

        RunJournal scheduler = RunJournal.LoadOrCreate(plan);
        scheduler.MarkRunning("01-task");
        scheduler.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 1);

        Assert.Equal(Owner(14168), RunJournal.LoadOrCreate(plan).Document.Owner);
    }

    /// <summary>Recording the end from the CLI's stale instance must not revert the run it is recording the end of.</summary>
    [Fact]
    public void RecordingTheEnd_FromAStaleInstance_DoesNotRevertTheRun()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal cli = RunJournal.LoadOrCreate(plan);
        RunOwner owner = Owner(14168);
        cli.RecordOwner(owner);

        RunJournal scheduler = RunJournal.LoadOrCreate(plan);
        scheduler.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 1);

        DateTimeOffset end = new(2026, 9, 13, 5, 2, 11, TimeSpan.Zero);
        cli.RecordOwnerFinished(owner, end);

        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;
        Assert.Equal(TaskStatus.Succeeded, onDisk.Tasks["01-task"].Status);
        Assert.Equal(end, onDisk.Owner!.FinishedAt);
    }

    /// <summary>
    /// A second process claimed the journal while the first was still winding down. The first one's end is not
    /// the second one's, and stamping it would make <c>status</c> call a live run finished.
    /// </summary>
    [Fact]
    public void RecordingTheEnd_MarksOnlyTheOwnerThatClaimedTheRun()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal first = RunJournal.LoadOrCreate(plan);
        first.RecordOwner(Owner(1111));

        RunJournal second = RunJournal.LoadOrCreate(plan);
        second.RecordOwner(Owner(2222));

        first.RecordOwnerFinished(Owner(1111), DateTimeOffset.UtcNow);

        RunOwner? onDisk = RunJournal.LoadOrCreate(plan).Document.Owner;
        Assert.Equal(2222, onDisk!.Pid);
        Assert.Null(onDisk.FinishedAt);
    }

    /// <summary>
    /// The same pid is not the same owner: a different start time is a different process, so an end recorded by
    /// the pid's previous holder never lands on the current one.
    /// </summary>
    [Fact]
    public void RecordingTheEnd_ForTheSamePidWithADifferentStartTime_IsANoOp()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunOwner current = Owner(14168);
        journal.RecordOwner(current);

        journal.RecordOwnerFinished(current with { ProcessStartedAt = current.ProcessStartedAt.AddHours(-3) }, DateTimeOffset.UtcNow);

        Assert.Null(RunJournal.LoadOrCreate(plan).Document.Owner!.FinishedAt);
    }

    /// <summary>
    /// A resume is a new process claiming a journal whose previous owner finished. If the claim kept that end, a
    /// resume that then died with the machine would read as FINISHED — the exact misreport this issue exists for.
    /// </summary>
    [Fact]
    public void ANewClaim_ClearsThePreviousRunsEnd()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordOwner(Owner(1111));
        journal.RecordOwnerFinished(Owner(1111), DateTimeOffset.UtcNow);

        RunJournal.LoadOrCreate(plan).RecordOwner(Owner(2222));

        RunOwner? onDisk = RunJournal.LoadOrCreate(plan).Document.Owner;
        Assert.Equal(2222, onDisk!.Pid);
        Assert.Null(onDisk.FinishedAt);
    }

    /// <summary>A journal no run has claimed carries no <c>owner</c> key at all — absent, never <c>null</c> noise.</summary>
    [Fact]
    public void AJournalNoRunClaimed_WritesNoOwnerKey()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal.LoadOrCreate(plan);

        Assert.DoesNotContain("\"owner\"", File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)), StringComparison.Ordinal);
    }

    /// <summary>A claimed run writes the SSOT §7 wire names, and no <c>finishedAt</c> until the run has ended.</summary>
    [Fact]
    public void AClaimedRun_WritesTheWireNames_AndNoFinishedAtUntilItEnds()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordOwner(Owner(14168));

        string claimed = File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory));
        Assert.Contains("\"owner\"", claimed, StringComparison.Ordinal);
        Assert.Contains("\"pid\": 14168", claimed, StringComparison.Ordinal);
        Assert.Contains("\"processStartedAt\"", claimed, StringComparison.Ordinal);
        Assert.Contains("\"host\": \"LAPTOP-7\"", claimed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finishedAt\"", claimed, StringComparison.Ordinal);

        journal.RecordOwnerFinished(Owner(14168), DateTimeOffset.UtcNow);
        Assert.Contains("\"finishedAt\"", File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)), StringComparison.Ordinal);
    }

    /// <summary>The run's last write must never recreate a journal someone deleted underneath it.</summary>
    [Fact]
    public void RecordingTheEnd_NeverRecreatesADeletedJournal()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        journal.RecordOwner(Owner(14168));

        File.Delete(RunJournal.PathFor(plan.PlanDirectory));
        journal.RecordOwnerFinished(Owner(14168), DateTimeOffset.UtcNow);

        Assert.False(File.Exists(RunJournal.PathFor(plan.PlanDirectory)));
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
