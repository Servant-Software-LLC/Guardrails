using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #704 review — WHERE <c>guardrails run</c> claims its journal, and WHETHER it may start at all.
/// <para>
/// <b>Where (W1).</b> The status tests only read what a run left behind, so they cannot see WHEN the claim happened. A
/// reviewer moved the claim to just before the run's summary and every one of them still passed — while a resume
/// then showed the PREVIOUS owner's verdict for its whole duration, and a run halting early never claimed at all.
/// These tests read the journal from INSIDE the run (a task's action copies it aside) and after an early halt.
/// </para>
/// <para>
/// <b>Whether (W2).</b> Every Scheduler write persists its whole in-memory document, including the owner it loaded.
/// Two runs on one journal therefore overwrite each other's claim, and the first to end marks the other — still
/// live — as ended. So a run refuses to start while the journal's owner is RUNNING on this machine. It does not
/// refuse for an owner that is gone, or one on another host: those are the cases a resume exists for.
/// </para>
/// </summary>
public sealed class RunOwnershipTests
{
    private static async Task<(int Exit, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static string JournalPath(StatePlanBuilder plan) => RunJournal.PathFor(plan.PlanDir);

    private static RunOwner? OwnerIn(string journalPath) => JournalReader.Read(journalPath).Owner;

    // ── W1: the claim is in place before the run does anything ───────────────────────────────────

    /// <summary>
    /// Mid-run, the journal must already name THIS run's process and no end — on a fresh run, and on a resume whose
    /// previous run left its own end behind. A late claim fails both: the fresh copy names no owner, and the
    /// resume's copy carries the previous run's end.
    /// </summary>
    [Fact]
    public async Task MidRun_TheJournalNamesThisRunsProcessAndNoEnd_FreshAndOnAResume()
    {
        string midRunCopy = Path.Combine(Path.GetTempPath(), "gr-704-midrun-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var plan = new StatePlanBuilder()
                .AddTask("01-first", actionBody: CopyJournalTo(midRunCopy), guardrailBody: StatePlanBuilder.Fail("not yet"));

            Assert.Equal(ExitCodes.TaskFailed, (await InvokeAsync("run", plan.PlanDir, "--no-log-server")).Exit);
            AssertClaimedByThisProcessWithNoEnd(midRunCopy, "the fresh run");
            Assert.NotNull(OwnerIn(JournalPath(plan))!.FinishedAt);

            plan.SetGuardrail("01-first", StatePlanBuilder.Succeed());
            File.Delete(midRunCopy);

            Assert.Equal(ExitCodes.Success, (await InvokeAsync("run", plan.PlanDir, "--no-log-server")).Exit);
            AssertClaimedByThisProcessWithNoEnd(midRunCopy, "the resume");
            Assert.NotNull(OwnerIn(JournalPath(plan))!.FinishedAt);
        }
        finally
        {
            File.Delete(midRunCopy);
        }
    }

    /// <summary>
    /// A run that halts at its plan preflight — before it schedules anything — still ended by its own decision, and
    /// must say so. Otherwise its live test process makes it read RUNNING, and a dead one EXITED WITHOUT FINISHING.
    /// </summary>
    [Fact]
    public async Task ARunThatHaltsAtItsPlanPreflight_StillRecordsThatItEnded()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        WriteFailingPlanPreflight(plan.PlanDir);

        Assert.Equal(ExitCodes.TaskFailed, (await InvokeAsync("run", plan.PlanDir, "--no-log-server")).Exit);

        RunOwner? owner = OwnerIn(JournalPath(plan));
        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.NotNull(owner.FinishedAt);
    }

    private static void AssertClaimedByThisProcessWithNoEnd(string midRunCopy, string which)
    {
        Assert.True(File.Exists(midRunCopy), $"{which}: the task's action never copied the journal");
        RunOwner? owner = OwnerIn(midRunCopy);
        Assert.True(owner is not null, $"{which}: mid-run, the journal named no owner");
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.True(owner.FinishedAt is null, $"{which}: mid-run, the journal already recorded an end ({owner.FinishedAt})");
    }

    private static string CopyJournalTo(string destination) => StatePlanBuilder.UsePowerShell
        ? $"Copy-Item -LiteralPath (Join-Path $env:GUARDRAILS_PLAN_DIR 'state/run.json') -Destination '{destination}'; exit 0"
        : $"cp \"$GUARDRAILS_PLAN_DIR/state/run.json\" '{destination}'; exit 0";

    private static void WriteFailingPlanPreflight(string planDir)
    {
        string dir = Path.Combine(planDir, "preflights");
        Directory.CreateDirectory(dir);

        const string catches = "# catches: 01-baseline - a run that halts at its plan preflight and never records that it ended";
        string path = Path.Combine(dir, StatePlanBuilder.UsePowerShell ? "01-baseline.ps1" : "01-baseline.sh");
        File.WriteAllText(path, StatePlanBuilder.UsePowerShell
            ? catches + "\nWrite-Output 'plan preflight red (deliberate)'\nexit 1\n"
            : "#!/usr/bin/env bash\n" + catches + "\necho 'plan preflight red (deliberate)'\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ── W2: one live run per journal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The journal's owner is this test process, alive and with no recorded end — a run in progress. A second run
    /// must not start, must not touch the journal (no resume normalization, no new claim), and must not let
    /// <c>--fresh</c> tear the live run's state down before the refusal.
    /// </summary>
    [Fact]
    public async Task ASecondRun_IsRefused_WhileTheJournalsOwnerIsRunningOnThisMachine()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        RunOwner self = RunLiveness.OwnerForThisProcess()!;
        WriteJournalOwnedBy(plan, self);
        byte[] before = File.ReadAllBytes(JournalPath(plan));

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-log-server");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("Refusing to start", output, StringComparison.Ordinal);
        Assert.Contains($"process {self.Pid}", output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(JournalPath(plan)));

        (exit, output) = await InvokeAsync("run", plan.PlanDir, "--no-log-server", "--fresh");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("Refusing to start", output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(JournalPath(plan)));
    }

    /// <summary>An owner that is gone and never recorded an end is exactly the run a resume exists for.</summary>
    [Fact]
    public async Task AnOwnerThatExitedWithoutFinishing_DoesNotBlockARun()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        WriteJournalOwnedBy(plan, RunLiveness.OwnerForThisProcess()! with { Pid = 999_999_999 });

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Refusing to start", output, StringComparison.Ordinal);
        Assert.Equal(Environment.ProcessId, OwnerIn(JournalPath(plan))!.Pid);
    }

    /// <summary>
    /// An owner recorded on another machine and not running here is UNKNOWN, not RUNNING: this machine cannot see
    /// that process table, and refusing on a guess would strand the plan.
    /// </summary>
    [Fact]
    public async Task AnOwnerOnAnotherHost_DoesNotBlockARun()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        WriteJournalOwnedBy(plan, RunLiveness.OwnerForThisProcess()! with { Pid = 999_999_999, Host = "BUILD-BOX" });

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Refusing to start", output, StringComparison.Ordinal);
    }

    private static void WriteJournalOwnedBy(StatePlanBuilder plan, RunOwner owner)
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-13T04-15-59Z-a1b2",
            PlanHash = "sha256:test",
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                ["01-first"] = new() { Status = JournalTaskStatus.Running }
            },
            Owner = owner
        };

        string stateDir = Path.Combine(plan.PlanDir, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "run.json"), JsonSerializer.Serialize(document, JournalJson.Options));
    }
}
