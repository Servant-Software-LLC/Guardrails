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
/// in-flight task under "A resume will RE-RUN these — the status above is the last run's outcome", describing
/// live work as state a resume would discard.
/// </para>
/// <para>
/// These tests drive the REAL command. Liveness comes from a fake process table where the test must control the
/// answer, and from the production table where the test's own process is the owner — never from sleep, suspend,
/// or timing.
/// </para>
/// </summary>
public sealed class StatusRunLivenessTests
{
    /// <summary>The 39-character id that overflowed plan 40's status table (issue #704, second comment).</summary>
    private const string LongId = "19-author-tests-overwatcher-autoresolve";

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
            RunId = "2026-09-13T04-15-59Z-a1b2",
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

    private static readonly DateTimeOffset AnyStart =
        new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456);

    // ── The two misreport directions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The reboot occurrence. The owner is gone, so the run is not in progress, and its <c>running</c> task is not
    /// either: the table must not print the word that asserts it is, and the resume prediction must say what that
    /// task actually is.
    /// </summary>
    [Fact]
    public async Task ADeadRun_IsReportedExited_AndItsRunningTaskIsNotShownAsInProgress()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(running: false));

        Assert.Contains("Run state: EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Run state: RUNNING", output, StringComparison.Ordinal);

        string row = Row(output, "02-second");
        Assert.Contains(" interrupted ", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" running ", row, StringComparison.Ordinal);

        Assert.Contains("A resume will RE-RUN these", output, StringComparison.Ordinal);
        Assert.Contains("02-second (interrupted)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live occurrence. The owner is alive, so its in-flight task IS in flight: shown as <c>running</c>, and
    /// not listed as leftover state that a resume will discard.
    /// </summary>
    [Fact]
    public async Task ALiveRun_IsReportedRunning_AndItsInFlightTaskIsNotCalledLeftoverState()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        WriteJournalMidAttempt(plan, pid: 14168, AnyStart);

        string output = await StatusAsync(plan.PlanDir, new FakeProcessTable(running: true));

        Assert.Contains("Run state: RUNNING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);

        Assert.Contains(" running ", Row(output, "02-second"), StringComparison.Ordinal);
        Assert.DoesNotContain("A resume will RE-RUN", output, StringComparison.Ordinal);
        Assert.DoesNotContain("02-second (running)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control. Without it, a status that never consulted the process table and printed one fixed verdict
    /// would pass one of the two tests above. The table must be asked, once, about the recorded pid AND start time.
    /// </summary>
    [Fact]
    public async Task Status_AsksTheProcessTableAboutTheRecordedOwner()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        RunOwner owner = WriteJournalMidAttempt(plan, pid: 14168, AnyStart);
        var table = new FakeProcessTable(running: true);

        await StatusAsync(plan.PlanDir, table);

        Assert.Equal((owner.Pid, owner.ProcessStartedAt), Assert.Single(table.Asked));
    }

    /// <summary>
    /// The production wiring, with no fake: <c>StatusCommand.Create(io)</c> is exactly what the CLI's command
    /// factory builds. This test process is a real live owner, and a pid no process can have is a real gone one.
    /// </summary>
    [Fact]
    public async Task TheProductionProcessTable_TellsALiveOwnerFromAGoneOne()
    {
        using StatePlanBuilder plan = TwoTaskPlan();
        RunOwner self = RunLiveness.OwnerForThisProcess();

        WriteJournalMidAttempt(plan, self.Pid, self.ProcessStartedAt);
        Assert.Contains("Run state: RUNNING", await StatusAsync(plan.PlanDir), StringComparison.Ordinal);

        WriteJournalMidAttempt(plan, pid: 999_999_999, self.ProcessStartedAt);
        Assert.Contains("Run state: EXITED WITHOUT FINISHING", await StatusAsync(plan.PlanDir), StringComparison.Ordinal);
    }

    // ── What a real run records ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A real run records its owner and, on its way out, its end. This is also the test that proves the end is
    /// stamped at all: the run executes in THIS test process, which is still alive when <c>status</c> checks — so
    /// without a recorded end the verdict would be RUNNING.
    /// </summary>
    [Fact]
    public async Task AfterAGreenRun_TheJournalNamesItsOwnerAndItsEnd_AndStatusSaysFinished()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        Assert.Equal(ExitCodes.Success, (await RunAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        RunOwner? owner = JournalReader.Read(RunJournal.PathFor(plan.PlanDir)).Owner;
        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.NotNull(owner.FinishedAt);

        Assert.Contains("Run state: FINISHED", await StatusAsync(plan.PlanDir), StringComparison.Ordinal);
    }

    /// <summary>
    /// A halted run finished too — it stopped by its own decision — and its table is then a last outcome, so the
    /// #639 resume prediction still prints for it.
    /// </summary>
    [Fact]
    public async Task AfterAHaltedRun_StatusSaysFinished_AndStillPredictsTheResume()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", guardrailBody: StatePlanBuilder.Fail("not yet"))
            .AddTask("02-second", dependsOn: "01-first");
        Assert.Equal(ExitCodes.TaskFailed, (await RunAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        string output = await StatusAsync(plan.PlanDir);

        Assert.Contains("Run state: FINISHED", output, StringComparison.Ordinal);
        Assert.Contains("A resume will RE-RUN these", output, StringComparison.Ordinal);
        Assert.Contains("02-second (blocked)", output, StringComparison.Ordinal);
    }

    // ── The rendered line ────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset LastWrite = new(2026, 9, 13, 0, 16, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = LastWrite.AddMinutes(13).AddSeconds(49);

    private static RunOwner LineOwner(DateTimeOffset? finishedAt = null) => new()
    {
        Pid = 14168,
        ProcessStartedAt = AnyStart,
        Host = "BUILD-BOX",
        FinishedAt = finishedAt
    };

    [Fact]
    public void TheLine_ForARunningRun()
    {
        Assert.Equal(
            "Run state: RUNNING — owner process 14168 is alive. Last journal write 2026-09-13T00:16:03Z (13m49s ago).",
            StatusCommand.LivenessLine(RunLivenessState.Running, LineOwner(), LastWrite, Now, "plans/40"));
    }

    [Fact]
    public void TheLine_ForARunThatExitedWithoutFinishing_CarriesTheResumeCommand()
    {
        Assert.Equal(
            "Run state: EXITED WITHOUT FINISHING — owner process 14168 is gone and never recorded an end, so nothing "
            + "is running. Last journal write 2026-09-13T00:16:03Z (13m49s ago). Resume with: guardrails run plans/40",
            StatusCommand.LivenessLine(RunLivenessState.ExitedWithoutFinishing, LineOwner(), LastWrite, Now, "plans/40"));
    }

    /// <summary>The resume command exists to be pasted, so a folder containing a space arrives quoted.</summary>
    [Fact]
    public void TheLine_ForARunThatExitedWithoutFinishing_QuotesAFolderWithASpace()
    {
        Assert.EndsWith(
            "Resume with: guardrails run \"C:\\Dev AI\\plans\\40\"",
            StatusCommand.LivenessLine(RunLivenessState.ExitedWithoutFinishing, LineOwner(), LastWrite, Now, "C:\\Dev AI\\plans\\40"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheLine_ForAFinishedRun()
    {
        Assert.Equal(
            "Run state: FINISHED — owner process 14168 recorded its end at 2026-09-13T05:02:11Z; nothing is running.",
            StatusCommand.LivenessLine(
                RunLivenessState.Finished, LineOwner(new DateTimeOffset(2026, 9, 13, 5, 2, 11, TimeSpan.Zero)),
                LastWrite, Now, "plans/40"));
    }

    [Fact]
    public void TheLine_ForAnOwnerOnAnotherHost()
    {
        Assert.Equal(
            "Run state: UNKNOWN — owner process 14168 ran on host 'BUILD-BOX', and its liveness can only be checked "
            + "there. Last journal write 2026-09-13T00:16:03Z (13m49s ago).",
            StatusCommand.LivenessLine(RunLivenessState.OnAnotherHost, LineOwner(), LastWrite, Now, "plans/40"));
    }

    [Fact]
    public void TheLine_ForAJournalThatNamesNoOwner()
    {
        Assert.Equal(
            "Run state: UNKNOWN — this journal names no owner process (it predates #704), so a live run and a dead "
            + "one look the same here. Last journal write 2026-09-13T00:16:03Z (13m49s ago).",
            StatusCommand.LivenessLine(RunLivenessState.NotRecorded, owner: null, LastWrite, Now, "plans/40"));
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

    private sealed class FakeProcessTable(bool running) : IProcessProbe
    {
        public List<(int Pid, DateTimeOffset StartedAt)> Asked { get; } = [];

        public bool IsRunning(int pid, DateTimeOffset startedAt)
        {
            Asked.Add((pid, startedAt));
            return running;
        }
    }
}
