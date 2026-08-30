using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #542 — <see cref="RunJournal.RecordDelivery"/>, and specifically the destructive trap it has to
/// avoid.
/// <para>
/// <b>Why this class exists at all.</b> Every other <c>Record*</c> on <see cref="RunJournal"/> is called
/// DURING the run by the component that owns the instance, so that instance's in-memory document is
/// current. The delivery is different: it is recorded by the CLI at the very END, from a journal instance
/// created BEFORE the run started, while tasks settle through the Scheduler's own journal. That instance is
/// therefore STALE — still all <c>pending</c> — and <c>Persist()</c> serializes the whole document. The
/// first cut of <c>RecordDelivery</c> did the obvious <c>_document with { Delivery = … }</c> and wrote that
/// stale document over the real one, reverting every task on disk to <c>pending</c>. It failed 26
/// integration tests, which is the only reason it was noticed rather than shipped.
/// </para>
/// <para>
/// So the load-bearing assertion here is not "the delivery is recorded" — it is "recording the delivery
/// does not destroy the run". A journal write that silently reverts a finished run is far worse than the
/// missing record #542 set out to add.
/// </para>
/// </summary>
public sealed class RunJournalDeliveryTests : IDisposable
{
    private readonly string _tempDir;

    public RunJournalDeliveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gr-542-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// THE regression test. Two instances over the same file, exactly as the real run has them: the CLI's
    /// (opened first, never told about the run's progress) and the run's (which settles the tasks). The
    /// stale instance must not be able to undo the other's work.
    /// </summary>
    [Fact]
    public void RecordingDelivery_FromAStaleInstance_DoesNotRevertTheRunItIsRecordingAbout()
    {
        PlanDefinition plan = BuildPlan();

        // The CLI's instance, opened at run start — and then never updated again.
        RunJournal cli = RunJournal.LoadOrCreate(plan);
        Assert.Equal(TaskStatus.Pending, cli.Document.Tasks["01-task"].Status);

        // The run settles the task through its own instance over the same file.
        RunJournal run = RunJournal.LoadOrCreate(plan);
        run.RecordSettle("01-task", TaskStatus.Succeeded, mergeSequence: 1);

        // The CLI's view is now stale, which is the whole hazard.
        Assert.Equal(TaskStatus.Pending, cli.Document.Tasks["01-task"].Status);

        cli.RecordDelivery(new DeliverySection
        {
            Delivered = false,
            Outcome = DeliveryOutcome.NotAttempted,
            Reason = "mergeOnSuccess resolved off",
            PlanBranch = "guardrails/plan",
        });

        // What is ON DISK is the authority — re-read it rather than trusting either instance's memory.
        JournalDocument onDisk = RunJournal.LoadOrCreate(plan).Document;

        Assert.Equal(TaskStatus.Succeeded, onDisk.Tasks["01-task"].Status);
        Assert.Equal(1, onDisk.Tasks["01-task"].MergeSequence);
        Assert.NotNull(onDisk.Delivery);
        Assert.False(onDisk.Delivery!.Delivered);
        Assert.Equal("guardrails/plan", onDisk.Delivery.PlanBranch);
    }

    /// <summary>
    /// The ordinary path still has to work: the record round-trips to disk and survives a reload, which is
    /// the entire point of adding it (a record only readable in the process that wrote it answers nothing
    /// after the terminal is closed).
    /// </summary>
    [Fact]
    public void TheDeliveryRecord_SurvivesAReload_SoItAnswersTheQuestionAfterTheRunIsOver()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDelivery(new DeliverySection
        {
            Delivered = true,
            Outcome = DeliveryOutcome.FastForwarded,
            DeliveredToBranch = "master",
        });

        DeliverySection? reloaded = RunJournal.LoadOrCreate(plan).Document.Delivery;

        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Delivered);
        Assert.Equal(DeliveryOutcome.FastForwarded, reloaded.Outcome);
        Assert.Equal("master", reloaded.DeliveredToBranch);
    }

    /// <summary>
    /// A run delivers once, and a later write is the authority — the same rule <c>RecordHalt</c> follows. A
    /// resume that gets further must not be shadowed by the earlier run's "not delivered".
    /// </summary>
    [Fact]
    public void ALaterRecord_Overwrites_SoAResumeThatDeliversIsNotShadowedByTheRunThatDidNot()
    {
        PlanDefinition plan = BuildPlan();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        journal.RecordDelivery(new DeliverySection
        {
            Delivered = false,
            Outcome = DeliveryOutcome.NotAttempted,
            Reason = "mergeOnSuccess resolved off",
            PlanBranch = "guardrails/plan",
        });
        journal.RecordDelivery(new DeliverySection
        {
            Delivered = true,
            Outcome = DeliveryOutcome.Merged,
            DeliveredToBranch = "master",
        });

        DeliverySection reloaded = RunJournal.LoadOrCreate(plan).Document.Delivery!;

        Assert.True(reloaded.Delivered);
        Assert.Equal(DeliveryOutcome.Merged, reloaded.Outcome);

        // The superseded explanation must be GONE, not merged in — a stale "the work is on
        // 'guardrails/plan'" beside delivered:true would send a reader after a branch already merged.
        Assert.Null(reloaded.Reason);
        Assert.Null(reloaded.PlanBranch);
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
