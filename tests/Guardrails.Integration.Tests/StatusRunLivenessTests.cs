using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #704 — <c>guardrails status</c> could not say whether a run was ALIVE, and got it wrong in both
/// directions.
/// <para>
/// <b>Dead, reported as live.</b> A laptop slept (then, the second time, rebooted) under an unattended run. On
/// waking, <c>status</c> printed a table byte-for-byte identical to a healthy run partway through — and for the
/// reboot, a task still <c>running</c>, which asserts that something is happening.
/// </para>
/// <para>
/// <b>Live, reported as leftover.</b> On a run that was alive and between attempts, the same command listed the
/// in-flight task under "A resume will RE-RUN these — the status above is the last run's outcome", describing live
/// work as state a resume would discard.
/// </para>
/// <para>
/// These tests drive the REAL command. Liveness comes from a fake process table where the test must control the
/// answer, and from the production table where the test's own process is the owner — never from sleep, suspend, or
/// timing.
/// </para>
/// </summary>
public sealed class StatusRunLivenessTests
{
    /// <summary>The 39-character id that overflowed plan 40's status table (issue #704, second comment).</summary>
    private const string LongId = "19-author-tests-overwatcher-autoresolve";

    private const string RunId = "2026-09-13T04-15-59Z-a1b2";

    private static readonly DateTimeOffset AnyStart =
        new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456);

    private static async Task<string> StatusAsync(string planDir, IProcessProbe? processProbe = null)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(processProbe is null ? StatusCommand.Create(io) : StatusCommand.Create(io, processProbe));
        Assert.Equal(ExitCodes.Success, await root.Parse(["status", planDir]).InvokeAsync());
        return io.OutText;
    }

    private static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// A journal as a run leaves it mid-attempt: task 01 settled, task 02 <c>running</c>, and an owner that never
    /// recorded an end. Whether that owner is alive is left to the process table the test supplies.
    /// </summary>
    private static RunOwner WriteJournalMidAttempt(StatePlanBuilder plan, int pid, DateTimeOffset processStartedAt)
    {
        var owner = new RunOwner { Pid = pid, ProcessStartedAt = processStartedAt, Host = RunLiveness.ThisHost() };
        var document = new JournalDocument
        {
            RunId = RunId,
            PlanHash = "sha256:test",
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                ["01-first"] = new() { Status = JournalTaskStatus.Succeeded },
                ["02-second"] = new() { Status = JournalTaskStatus.Running }
            },
            Owner = owner
        };

        string stateDir = Path.Combine(plan.PlanDir, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "run.json"), JsonSerializer.Serialize(document, JournalJson.Options));
        return owner;
    }

    private static StatePlanBuilder TwoTaskPlan() =>
        new StatePlanBuilder().AddTask("01-first").AddTask("02-second", dependsOn: "01-first");

    private static string[] Lines(string output) =>
        output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>The table row for <paramref name="taskId"/> — the first line naming it, since the table precedes any footer.</summary>
    private static string Row(string output, string taskId) =>
        Lines(output).First(line => line.StartsWith($"  {taskId} ", StringComparison.Ordinal));

    // ── The two misreport directions, and the third answer ───────────────────────────────────────

    /// <summary>
    /// The reboot occurrence. The owner is gone, so the run is not in progress, and its <c>running</c> task is not
    /// either: the table must not print the word that asserts it is, and the resume prediction must say what that
    /// task actually is — without a closing sentence that contradicts the list it closes.
    /// </summary>
    [Fact]
    public async Task ADeadRun_IsReportedExited_AndItsRunningTaskIsNotShownAsInProgress()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.NotRunning));

        Assert.Contains("Run state: EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Run state: RUNNING", output, StringComparison.Ordinal);

        string row = Row(output, "02-second");
        Assert.Contains(" interrupted ", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" running ", row, StringComparison.Ordinal);

        Assert.Contains("A resume will RE-RUN these", output, StringComparison.Ordinal);
        Assert.Contains("02-second (interrupted)", output, StringComparison.Ordinal);
        Assert.Contains(
            "Only 'succeeded' survives a resume; every task listed above becomes pending.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("running all become pending", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live occurrence. The owner is alive, so its in-flight task IS in flight: shown as <c>running</c>, and not
    /// listed as leftover state that a resume will discard.
    /// </summary>
    [Fact]
    public async Task ALiveRun_IsReportedRunning_AndItsInFlightTaskIsNotCalledLeftoverState()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));

        Assert.Contains("Run state: RUNNING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);

        Assert.Contains(" running ", Row(output, "02-second"), StringComparison.Ordinal);
        Assert.DoesNotContain("A resume will RE-RUN", output, StringComparison.Ordinal);
        Assert.DoesNotContain("02-second (running)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The process table cannot say (an unreadable start time, say). That is UNKNOWN, not EXITED: no resume command,
    /// and the journal's own word stands in the table because nothing disproves it.
    /// </summary>
    [Fact]
    public async Task AnOwnerThatCannotBeChecked_IsUnknown_AndTheJournalsWordStands()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.CannotTell));

        Assert.Contains("Run state: UNKNOWN — owner process 14168 could not be checked from here", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Resume with", output, StringComparison.Ordinal);
        Assert.Contains(" running ", Row(output, "02-second"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control. Without it, a status that never consulted the process table and printed one fixed verdict would
    /// pass one of the tests above. The table must be asked, once, about the recorded owner.
    /// </summary>
    [Fact]
    public async Task Status_AsksTheProcessTableAboutTheRecordedOwner()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        RunOwner owner = WriteJournalMidAttempt(plan, pid: 14168, AnyStart);
        var table = new FakeProcessTable(ProcessCheck.Running);

        await StatusAsync(plan.PlanDir, table);

        Assert.Equal(owner, Assert.Single(table.Asked));
    }

    /// <summary>
    /// The production wiring, with no fake: <c>StatusCommand.Create(io)</c> is exactly what the CLI's command
    /// factory builds. This test process is a real live owner, and a pid no process can have is a real gone one.
    /// </summary>
    [Fact]
    public async Task TheProductionProcessTable_TellsALiveOwnerFromAGoneOne()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        RunOwner self = RunLiveness.OwnerForThisProcess()!;

        WriteJournal(plan, self);
        Assert.Contains("Run state: RUNNING", await StatusAsync(plan.PlanDir), StringComparison.Ordinal);

        WriteJournal(plan, self with { Pid = 999_999_999 });
        Assert.Contains("Run state: EXITED WITHOUT FINISHING", await StatusAsync(plan.PlanDir), StringComparison.Ordinal);
    }

    private static void WriteJournal(StatePlanBuilder plan, RunOwner owner)
    {
        WriteJournalMidAttempt(plan, owner.Pid, owner.ProcessStartedAt);
        string path = RunJournal.PathFor(plan.PlanDir);
        JournalDocument document = JournalReader.Read(path) with { Owner = owner };
        File.WriteAllText(path, JsonSerializer.Serialize(document, JournalJson.Options));
    }

    // ── What a real run records ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A real run records its owner and, on its way out, its end. This is also the test that proves the end is
    /// stamped at all: the run executes in THIS test process, which is still alive when <c>status</c> checks — so
    /// without a recorded end the verdict would be RUNNING.
    /// </summary>
    [Fact]
    public async Task AfterAGreenRun_TheJournalNamesItsOwnerAndItsEnd_AndStatusSaysEndedWithTheOutcome()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        Assert.Equal(ExitCodes.Success, (await RunAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        RunOwner? owner = JournalReader.Read(RunJournal.PathFor(plan.PlanDir)).Owner;
        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.NotNull(owner.FinishedAt);

        string output = await StatusAsync(plan.PlanDir);
        Assert.Contains("Run state: ENDED at ", output, StringComparison.Ordinal);
        Assert.Contains(" — all 1 task(s) succeeded; nothing is running.", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A halted run ended too — by its own decision — and the line says WHERE it stopped, because the operator is
    /// told to read this line first. Its table is then a last outcome, so the #639 resume prediction still prints.
    /// </summary>
    [Fact]
    public async Task AfterAHaltedRun_StatusSaysEndedAtTheHaltedTask_AndStillPredictsTheResume()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", guardrailBody: StatePlanBuilder.Fail("not yet"))
            .AddTask("02-second", dependsOn: "01-first");
        Assert.Equal(ExitCodes.TaskFailed, (await RunAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        string output = await StatusAsync(plan.PlanDir);

        Assert.Contains("Run state: ENDED at ", output, StringComparison.Ordinal);
        Assert.Contains(" — halted at 01-first (needs-human); nothing is running.", output, StringComparison.Ordinal);
        Assert.Contains("A resume will RE-RUN these", output, StringComparison.Ordinal);
        Assert.Contains("02-second (blocked)", output, StringComparison.Ordinal);
    }

    // ── Last activity: display-only, and newest of three sources ─────────────────────────────────

    /// <summary>
    /// <c>run.json</c> moves only at task transitions, so on a long attempt its age says nothing about whether the run
    /// is moving. The line reads the newest of the journal, the run's event stream, and the RUNNING task's newest
    /// attempt logs — and never a settled task's logs, which are not this run making progress.
    /// </summary>
    [Fact]
    public async Task TheLastActivity_IsTheNewestOfTheJournalTheEventStreamAndTheRunningAttemptsLogs()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string runLogs = Path.Combine(plan.PlanDir, "logs", RunId);
        string events = Touch(Path.Combine(runLogs, "events.jsonl"));
        string runningAttemptStream = Touch(Path.Combine(runLogs, "02-second", "attempt-1", "claude-stream.jsonl"));
        string settledTaskLog = Touch(Path.Combine(runLogs, "01-first", "attempt-1", "stdout.log"));

        File.SetLastWriteTimeUtc(RunJournal.PathFor(plan.PlanDir), At(0));
        File.SetLastWriteTimeUtc(events, At(10));
        File.SetLastWriteTimeUtc(runningAttemptStream, At(16, 3));
        File.SetLastWriteTimeUtc(settledTaskLog, At(50));

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));
        Assert.Contains("Last activity 2026-09-13T00:16:03Z", output, StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(runningAttemptStream, At(5));
        output = await StatusAsync(plan.PlanDir, new FakeProcessTable(ProcessCheck.Running));
        Assert.Contains("Last activity 2026-09-13T00:10:00Z", output, StringComparison.Ordinal);

        static DateTime At(int minute, int second = 0) => new(2026, 9, 13, 0, minute, second, DateTimeKind.Utc);
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    // ── The rendered line ────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset LastActivity = new(2026, 9, 13, 0, 16, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = LastActivity.AddMinutes(13).AddSeconds(49);

    private static RunOwner LineOwner(DateTimeOffset? finishedAt = null) => new()
    {
        Pid = 14168,
        ProcessStartedAt = AnyStart,
        Host = "BUILD-BOX",
        FinishedAt = finishedAt
    };

    private static string Line(RunLivenessState state, RunOwner? owner, string folder = "plans/40", string endedSummary = "") =>
        StatusCommand.LivenessLine(state, owner, endedSummary, LastActivity, Now, folder);

    /// <summary>A live run gets the next step for the one case "alive" does not cover: alive and stuck (#722).</summary>
    [Fact]
    public void TheLine_ForARunningRun_NamesTheNextStepIfItIsNotProgressing()
    {
        Assert.Equal(
            "Run state: RUNNING — owner process 14168 is alive. Last activity 2026-09-13T00:16:03Z (13m49s ago). "
            + "If it is not progressing, stop process 14168, then resume with: guardrails run plans/40",
            Line(RunLivenessState.Running, LineOwner()));
    }

    [Fact]
    public void TheLine_ForARunThatExitedWithoutFinishing_CarriesTheResumeCommand()
    {
        Assert.Equal(
            "Run state: EXITED WITHOUT FINISHING — owner process 14168 is gone and never recorded an end, so nothing "
            + "is running. Last activity 2026-09-13T00:16:03Z (13m49s ago). Resume with: guardrails run plans/40",
            Line(RunLivenessState.ExitedWithoutFinishing, LineOwner()));
    }

    /// <summary>The resume command exists to be pasted, so a folder containing a space arrives quoted.</summary>
    [Fact]
    public void TheLine_ForARunThatExitedWithoutFinishing_QuotesAFolderWithASpace()
    {
        Assert.EndsWith(
            "Resume with: guardrails run \"C:\\Dev AI\\plans\\40\"",
            Line(RunLivenessState.ExitedWithoutFinishing, LineOwner(), folder: "C:\\Dev AI\\plans\\40"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheLine_ForAnEndedRun_CarriesTheOutcome()
    {
        Assert.Equal(
            "Run state: ENDED at 2026-09-13T05:02:11Z — halted at 18-implement-resume-shorthand (needs-human); "
            + "nothing is running.",
            Line(
                RunLivenessState.Ended,
                LineOwner(new DateTimeOffset(2026, 9, 13, 5, 2, 11, TimeSpan.Zero)),
                endedSummary: "halted at 18-implement-resume-shorthand (needs-human)"));
    }

    [Fact]
    public void TheLine_ForAnOwnerOnAnotherHost()
    {
        Assert.Equal(
            "Run state: UNKNOWN — owner process 14168 ran on host 'BUILD-BOX', and its liveness can only be checked "
            + "there. Last activity 2026-09-13T00:16:03Z (13m49s ago).",
            Line(RunLivenessState.OnAnotherHost, LineOwner()));
    }

    [Fact]
    public void TheLine_ForAnOwnerThatCannotBeChecked()
    {
        Assert.Equal(
            "Run state: UNKNOWN — owner process 14168 could not be checked from here, so a live run and a dead one "
            + "look the same. Last activity 2026-09-13T00:16:03Z (13m49s ago).",
            Line(RunLivenessState.CannotCheck, LineOwner()));
    }

    [Fact]
    public void TheLine_ForAJournalThatNamesNoOwner()
    {
        Assert.Equal(
            "Run state: UNKNOWN — this journal names no owner process (it predates #704, or its run could not read "
            + "its own identity), so a live run and a dead one look the same here. Last activity "
            + "2026-09-13T00:16:03Z (13m49s ago).",
            Line(RunLivenessState.NotRecorded, owner: null));
    }

    // ── The ENDED outcome, from what the journal already records ─────────────────────────────────

    private static readonly string[] Ids = ["01-first", "02-second"];
    private static readonly DateTimeOffset T = new(2026, 9, 13, 5, 2, 11, TimeSpan.Zero);

    private static TaskJournalEntry Entry(JournalTaskStatus status, AttemptOutcome? lastAttempt = null) => new()
    {
        Status = status,
        Attempts = lastAttempt is { } outcome
            ? [new AttemptRecord { Attempt = 1, StartedAt = T, EndedAt = T, Outcome = outcome, LogDir = "logs/r/t/attempt-1" }]
            : []
    };

    private static JournalDocument Journal(
        (string Id, TaskJournalEntry Entry)[] tasks,
        DeliverySection? delivery = null,
        RunHalt? halt = null,
        IReadOnlyList<DecisionEntry>? decisions = null) => new()
    {
        RunId = RunId,
        PlanHash = "sha256:test",
        Tasks = tasks.ToDictionary(task => task.Id, task => task.Entry, StringComparer.Ordinal),
        Delivery = delivery,
        Halt = halt,
        Decisions = decisions
    };

    private static DecisionEntry Decision(string token, string subject, string boundary = "task") => new()
    {
        Boundary = boundary,
        Policy = "auto",
        Decision = token,
        At = T,
        Subject = subject,
        Headline = $"{token} at {subject}"
    };

    private static (string Id, TaskJournalEntry Entry)[] BothSucceeded() =>
        [("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Succeeded))];

    private static DeliverySection Stranded() => new()
    {
        Delivered = false,
        Outcome = DeliveryOutcome.NotAttempted,
        Reason = "mergeOnSuccess is off (set by --no-merge-on-success)",
        PlanBranch = "guardrails/plan"
    };

    // ── A machine-decided run must never read as clean green (#361/#597) ─────────────────────────

    /// <summary>
    /// The operator is told to read the Run state line FIRST, so a wholly-green run whose delivery was forced past
    /// the autonomous-mode interlock cannot read exactly like an ordinary green delivery. The journal already records
    /// which decision was overridden; the line names it.
    /// </summary>
    [Fact]
    public void EndedSummary_AForcedDelivery_NamesTheDecisionItWasForcedPast()
    {
        var delivery = new DeliverySection
        {
            Delivered = true,
            Outcome = DeliveryOutcome.FastForwarded,
            DeliveredToBranch = "master",
            ForcedPastDecision = new ForcedDeliveryRecord
            {
                Decision = "proceeded-best-guess",
                Subject = "12-implement-events-endpoint",
                Boundary = "task"
            }
        };

        Assert.Equal(
            "all 2 task(s) succeeded, delivered to master, delivered past a machine decision "
            + "(proceeded-best-guess at 12-implement-events-endpoint)",
            StatusCommand.EndedSummary(Ids, Journal(BothSucceeded(), delivery)));
    }

    /// <summary>A run that proceeded through unreviewed waves is flagged with the count the harness itself derives.</summary>
    [Fact]
    public void EndedSummary_ARunThatProceededUnreviewed_CountsTheWaves()
    {
        IReadOnlyList<DecisionEntry> decisions =
        [
            Decision(DecisionTokens.ProceededUnreviewed, "wave-02-issue-510", boundary: "wave"),
            Decision(DecisionTokens.ProceededUnreviewed, "wave-03-issue-511", boundary: "wave")
        ];

        Assert.Equal(
            "all 2 task(s) succeeded, NOT delivered — the work is on guardrails/plan, ran with 2 unreviewed wave(s)",
            StatusCommand.EndedSummary(Ids, Journal(BothSucceeded(), Stranded(), decisions: decisions)));
    }

    /// <summary>A best guess shaped the result even though no wave ran unreviewed, so the line says whose judgment to check.</summary>
    [Fact]
    public void EndedSummary_ARunShapedByABestGuess_NamesTheDecision()
    {
        IReadOnlyList<DecisionEntry> decisions = [Decision(DecisionTokens.ProceededBestGuess, "07-implement-thing")];

        Assert.Equal(
            "all 2 task(s) succeeded, NOT delivered — the work is on guardrails/plan, shaped by a machine decision "
            + "(proceeded-best-guess at 07-implement-thing)",
            StatusCommand.EndedSummary(Ids, Journal(BothSucceeded(), Stranded(), decisions: decisions)));
    }

    /// <summary>
    /// The control that keeps the flag meaningful: ordinary decisions — a halt, a drift resolved automatically — are
    /// not machine-decided WORK, and add nothing to the line.
    /// </summary>
    [Fact]
    public void EndedSummary_OrdinaryDecisions_AddNothing()
    {
        IReadOnlyList<DecisionEntry> decisions =
        [
            Decision(DecisionTokens.Halted, "05-x"),
            Decision(DecisionTokens.AutoApplied, "06-y", boundary: "drift")
        ];

        Assert.Equal(
            "all 2 task(s) succeeded",
            StatusCommand.EndedSummary(Ids, Journal(BothSucceeded(), decisions: decisions)));
    }

    /// <summary>A gate halt settles no task, so the gate's own headline is the outcome — ahead of anything in the table.</summary>
    [Fact]
    public void EndedSummary_AGateHalt_NamesTheGate()
    {
        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = T,
            Headline = "Full Flight Checks FAILED: 01-baseline"
        };

        Assert.Equal(
            "halted: Full Flight Checks FAILED: 01-baseline",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Pending)), ("02-second", Entry(JournalTaskStatus.NeedsHuman))], halt: halt)));
    }

    [Fact]
    public void EndedSummary_ATaskThatNeedsAHuman_NamesTheTask()
    {
        Assert.Equal(
            "halted at 02-second (needs-human)",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.NeedsHuman))])));
    }

    [Fact]
    public void EndedSummary_SeveralHaltedTasks_NamesTheFirstInPlanOrder_AndCountsTheRest()
    {
        Assert.Equal(
            "halted at 01-first (failed) and 1 more",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("02-second", Entry(JournalTaskStatus.NeedsHuman)), ("01-first", Entry(JournalTaskStatus.Failed))])));
    }

    /// <summary>A task still <c>running</c> under a run that recorded its end was cut off by a fault that unwound mid-attempt.</summary>
    [Fact]
    public void EndedSummary_ATaskStillMarkedRunning_WasInterrupted()
    {
        Assert.Equal(
            "interrupted at 02-second",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Running))])));
    }

    [Fact]
    public void EndedSummary_ACancelledAttempt_IsCancelled()
    {
        Assert.Equal(
            "cancelled",
            StatusCommand.EndedSummary(
                Ids,
                Journal([
                    ("01-first", Entry(JournalTaskStatus.Succeeded)),
                    ("02-second", Entry(JournalTaskStatus.Pending, AttemptOutcome.Cancelled))])));
    }

    [Fact]
    public void EndedSummary_WhollyGreenAndDelivered_NamesTheBranch()
    {
        var delivery = new DeliverySection
        {
            Delivered = true,
            Outcome = DeliveryOutcome.FastForwarded,
            DeliveredToBranch = "master"
        };

        Assert.Equal(
            "all 2 task(s) succeeded, delivered to master",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Succeeded))], delivery)));
    }

    /// <summary>Green but stranded is the outcome that loses work to a later <c>--fresh</c>, so the line names where the work is.</summary>
    [Fact]
    public void EndedSummary_WhollyGreenButNotDelivered_NamesWhereTheWorkIs()
    {
        var delivery = new DeliverySection
        {
            Delivered = false,
            Outcome = DeliveryOutcome.NotAttempted,
            Reason = "mergeOnSuccess resolved off",
            PlanBranch = "guardrails/plan"
        };

        Assert.Equal(
            "all 2 task(s) succeeded, NOT delivered — the work is on guardrails/plan",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Succeeded))], delivery)));
    }

    [Fact]
    public void EndedSummary_WhollyGreenButTheMergeWasRefused_NamesTheRefusal()
    {
        var delivery = new DeliverySection { Delivered = false, Outcome = DeliveryOutcome.Conflict };

        Assert.Equal(
            "all 2 task(s) succeeded, not delivered (conflict)",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Succeeded))], delivery)));
    }

    [Fact]
    public void EndedSummary_WhollyGreenWithNothingToDeliver()
    {
        Assert.Equal(
            "all 2 task(s) succeeded",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Succeeded))])));
    }

    /// <summary>A run that stopped without halting a task — a declined drift prompt, a MAX_PATH preflight — says how far it got.</summary>
    [Fact]
    public void EndedSummary_StoppedBeforeFinishing_SaysHowFarItGot()
    {
        Assert.Equal(
            "stopped with 1 of 2 task(s) succeeded",
            StatusCommand.EndedSummary(
                Ids,
                Journal([("01-first", Entry(JournalTaskStatus.Succeeded)), ("02-second", Entry(JournalTaskStatus.Pending))])));
    }

    // ── The TASK column ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 40's 39-character id pushed the rest of its row out of line, and a scripted parse of the table produced
    /// nonsense counts. Every row's STATUS cell must start where the header's STATUS does.
    /// </summary>
    [Fact]
    public async Task Status_SizesTheTaskColumnFromTheLongestTaskId()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first").AddTask(LongId, dependsOn: "01-first");
        Assert.Equal(ExitCodes.Success, (await RunAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        string output = await StatusAsync(plan.PlanDir);
        string header = Lines(output).First(line => line.StartsWith("  TASK", StringComparison.Ordinal));
        int statusColumn = header.IndexOf("STATUS", StringComparison.Ordinal);

        Assert.Equal(statusColumn, Row(output, "01-first").IndexOf("succeeded", StringComparison.Ordinal));
        Assert.Equal(statusColumn, Row(output, LongId).IndexOf("succeeded", StringComparison.Ordinal));
    }

    /// <summary>The same overflow in <c>run --dry-run</c>'s per-task table, which now sizes its column by the same rule.</summary>
    [Fact]
    public async Task DryRun_SizesTheTaskColumnFromTheLongestTaskId()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first").AddTask(LongId, dependsOn: "01-first");

        (int exit, string output) = await RunAsync("run", plan.PlanDir, "--dry-run");
        Assert.Equal(ExitCodes.Success, exit);

        string[] lines = Lines(output);
        string header = lines.First(line => line.StartsWith("  TASK", StringComparison.Ordinal));
        int kindColumn = header.IndexOf("KIND", StringComparison.Ordinal);

        // Last(): the tiers preview lists each id first; the per-task resolution table comes after it.
        foreach (string id in new[] { "01-first", LongId })
        {
            string row = lines.Last(line => line.StartsWith($"  {id} ", StringComparison.Ordinal));
            Assert.Equal(kindColumn, row.IndexOf("script", StringComparison.Ordinal));
        }
    }

    private sealed class FakeProcessTable(ProcessCheck answer) : IProcessProbe
    {
        public List<RunOwner> Asked { get; } = [];

        public ProcessCheck Check(RunOwner owner)
        {
            Asked.Add(owner);
            return answer;
        }
    }
}
