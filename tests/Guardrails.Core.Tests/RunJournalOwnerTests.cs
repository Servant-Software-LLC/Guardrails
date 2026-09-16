using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #704 — how a run's owner reaches <c>run.json</c> and how its end is recorded: the writes that let
/// <c>guardrails status</c> tell a live run from a dead one.
/// <para>
/// The claim rides <see cref="RunJournal.LoadOrCreateForRun"/>'s OWN write, so it cannot fail separately from the
/// load: a claim that failed quietly on a resume would leave the previous owner — possibly with its end recorded — and
/// the Scheduler would carry that stale owner through every write, so <c>status</c> would call a live run over.
/// </para>
/// <para>
/// The end-of-run write has the hazards <see cref="RunJournalDeliveryTests"/> documents (the CLI's instance is stale
/// by then) plus two of its own: a run must never mark ANOTHER process's claim ended, and must never recreate a
/// journal deleted underneath it.
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

    private static RunOwner? OwnerOnDisk(PlanDefinition plan) =>
        JournalReader.Read(RunJournal.PathFor(plan.PlanDirectory)).Owner;

    /// <summary>
    /// The claim is IN the load's write — nothing else has to succeed for it to be on disk — and it comes back to
    /// the tick, because it is compared against a live process table.
    /// </summary>
    [Fact]
    public void TheRunsOwnLoad_WritesTheClaim_ToTheTick()
    {
        PlanDefinition plan = BuildPlan();
        RunOwner owner = Owner(14168);

        RunJournal.LoadOrCreateForRun(plan, owner);

        RunOwner? onDisk = OwnerOnDisk(plan);
        Assert.Equal(owner, onDisk);
        Assert.Equal(owner.ProcessStartedAt.UtcTicks, onDisk!.ProcessStartedAt.UtcTicks);
    }

    /// <summary>
    /// A resume is a new process claiming a journal whose previous owner ended. The claim replaces that owner and
    /// its end — otherwise a resume that then died with the machine would read as ENDED.
    /// </summary>
    [Fact]
    public void AResumesClaim_ReplacesThePreviousOwnerAndItsEnd()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal.LoadOrCreateForRun(plan, Owner(1111)).RecordOwnerFinished(Owner(1111), DateTimeOffset.UtcNow);

        RunJournal.LoadOrCreateForRun(plan, Owner(2222));

        RunOwner? onDisk = OwnerOnDisk(plan);
        Assert.Equal(2222, onDisk!.Pid);
        Assert.Null(onDisk.FinishedAt);
    }

    /// <summary>
    /// A run that could not read its own identity cannot name itself — but it must not leave the PREVIOUS owner
    /// standing either, or <c>status</c> would report that stale run's verdict for this one. It clears it, so the
    /// honest answer is UNKNOWN.
    /// </summary>
    [Fact]
    public void AClaimWithoutAnIdentity_ClearsThePreviousOwner_RatherThanLeavingItsVerdict()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal.LoadOrCreate(plan);
        string path = RunJournal.PathFor(plan.PlanDirectory);
        JournalDocument stale = JournalReader.Read(path) with
        {
            Owner = Owner(1111) with { FinishedAt = DateTimeOffset.UtcNow }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(stale, JournalJson.Options));

        RunJournal.LoadOrCreateForRun(plan, owner: null);

        Assert.Null(OwnerOnDisk(plan));
    }

    /// <summary>
    /// Every OTHER load — the Scheduler's own, a reset, a supply — keeps the owner, and every write through such an
    /// instance carries it. If the Scheduler's load dropped it, the first settled task would erase the claim.
    /// </summary>
    [Fact]
    public void AnyOtherLoad_CarriesTheOwnerThroughEveryWrite()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal.LoadOrCreateForRun(plan, Owner(14168));

        RunJournal scheduler = RunJournal.LoadOrCreate(plan);
        scheduler.MarkRunning("01-task");
        scheduler.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 1);

        Assert.Equal(Owner(14168), OwnerOnDisk(plan));
    }

    /// <summary>Recording the end from the CLI's stale instance must not revert the run it is recording the end of.</summary>
    [Fact]
    public void RecordingTheEnd_FromAStaleInstance_DoesNotRevertTheRun()
    {
        PlanDefinition plan = BuildPlan();
        RunOwner owner = Owner(14168);
        RunJournal cli = RunJournal.LoadOrCreateForRun(plan, owner);

        RunJournal scheduler = RunJournal.LoadOrCreate(plan);
        scheduler.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 1);

        DateTimeOffset end = new(2026, 9, 13, 5, 2, 11, TimeSpan.Zero);
        cli.RecordOwnerFinished(owner, end);

        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;
        Assert.Equal(TaskStatus.Succeeded, onDisk.Tasks["01-task"].Status);
        Assert.Equal(end, onDisk.Owner!.FinishedAt);
    }

    /// <summary>
    /// A second process claimed the journal while the first was still winding down. The first one's end is not the
    /// second one's, and stamping it would make <c>status</c> call a live run ended.
    /// </summary>
    [Fact]
    public void RecordingTheEnd_MarksOnlyTheOwnerThatClaimedTheRun()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal first = RunJournal.LoadOrCreateForRun(plan, Owner(1111));
        RunJournal.LoadOrCreateForRun(plan, Owner(2222));

        first.RecordOwnerFinished(Owner(1111), DateTimeOffset.UtcNow);

        RunOwner? onDisk = OwnerOnDisk(plan);
        Assert.Equal(2222, onDisk!.Pid);
        Assert.Null(onDisk.FinishedAt);
    }

    /// <summary>The same pid is not the same owner: a different start time is a different process.</summary>
    [Fact]
    public void RecordingTheEnd_ForTheSamePidWithADifferentStartTime_IsANoOp()
    {
        PlanDefinition plan = BuildPlan();
        RunOwner current = Owner(14168);
        RunJournal journal = RunJournal.LoadOrCreateForRun(plan, current);

        journal.RecordOwnerFinished(
            current with { ProcessStartedAt = current.ProcessStartedAt.AddHours(-3) }, DateTimeOffset.UtcNow);

        Assert.Null(OwnerOnDisk(plan)!.FinishedAt);
    }

    /// <summary>
    /// The end is recorded as early as it is known and then left alone: the backstop that runs at method exit must
    /// not move it later, past a log-server drain and a worktree sweep the run's verdict never waited on.
    /// </summary>
    [Fact]
    public void RecordingTheEndTwice_KeepsTheFirstEnd()
    {
        PlanDefinition plan = BuildPlan();
        RunOwner owner = Owner(14168);
        RunJournal journal = RunJournal.LoadOrCreateForRun(plan, owner);
        DateTimeOffset first = new(2026, 9, 13, 5, 2, 11, TimeSpan.Zero);

        journal.RecordOwnerFinished(owner, first);
        journal.RecordOwnerFinished(owner, first.AddMinutes(2));

        Assert.Equal(first, OwnerOnDisk(plan)!.FinishedAt);
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
        RunOwner owner = Owner(14168) with { ProcessStartTicks = 4242, BootId = "boot-a" };
        RunJournal journal = RunJournal.LoadOrCreateForRun(plan, owner);

        string claimed = File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory));
        Assert.Contains("\"owner\"", claimed, StringComparison.Ordinal);
        Assert.Contains("\"pid\": 14168", claimed, StringComparison.Ordinal);
        Assert.Contains("\"processStartedAt\"", claimed, StringComparison.Ordinal);
        Assert.Contains("\"processStartTicks\": 4242", claimed, StringComparison.Ordinal);
        Assert.Contains("\"bootId\": \"boot-a\"", claimed, StringComparison.Ordinal);
        Assert.Contains("\"host\": \"LAPTOP-7\"", claimed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finishedAt\"", claimed, StringComparison.Ordinal);

        journal.RecordOwnerFinished(owner, DateTimeOffset.UtcNow);
        Assert.Contains("\"finishedAt\"", File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)), StringComparison.Ordinal);
    }

    /// <summary>The run's last write must never recreate a journal someone deleted underneath it.</summary>
    [Fact]
    public void RecordingTheEnd_NeverRecreatesADeletedJournal()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreateForRun(plan, Owner(14168));

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
