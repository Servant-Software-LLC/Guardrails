using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #704 — <see cref="RunLiveness.Assess"/>, the verdict <c>guardrails status</c> prints above its table.
/// <para>
/// <b>The report.</b> A run left overnight on a laptop that slept was dead in the morning, and <c>status</c>
/// printed twelve <c>succeeded</c> and thirteen <c>pending</c> with exit 0 — byte-for-byte a healthy run partway
/// through. A second run died with a rebooting machine mid-attempt and left that task <c>running</c>, which
/// asserts that something is happening. And on a LIVE run the same command described the in-flight task as
/// leftover state a resume would discard. Wrong in both directions, for one reason: nothing in <c>run.json</c>
/// named a process to check.
/// </para>
/// <para>
/// Every case is decided by a FAKE process table, so nothing here sleeps, suspends, or races a real process. The
/// first two tests are the two misreport directions. The third is the control that fails if the table is never
/// consulted: a verdict hard-wired to either answer passes one of the first two, and cannot pass the third.
/// </para>
/// </summary>
public sealed class RunLivenessTests
{
    private const string Here = "LAPTOP-7";

    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456);

    private static RunOwner Owner(string? host = Here, DateTimeOffset? finishedAt = null) => new()
    {
        Pid = 14168,
        ProcessStartedAt = Started,
        Host = host,
        FinishedAt = finishedAt
    };

    /// <summary>The live-run direction: a run whose process is still there must never read as dead.</summary>
    [Fact]
    public void AnOwnerProcessStillRunning_IsRunning()
    {
        Assert.Equal(RunLivenessState.Running, RunLiveness.Assess(Owner(), Here, new FakeProcessTable(running: true)));
    }

    /// <summary>The dead-run direction: a run whose process is gone must never read as in progress.</summary>
    [Fact]
    public void AnOwnerProcessThatIsGone_IsExitedWithoutFinishing()
    {
        Assert.Equal(
            RunLivenessState.ExitedWithoutFinishing,
            RunLiveness.Assess(Owner(), Here, new FakeProcessTable(running: false)));
    }

    /// <summary>
    /// The control. The same journal, two process tables: the verdicts must differ, and each table must have been
    /// asked about exactly the pid AND start time the journal recorded — asking about the pid alone would let a
    /// reused pid vouch for a dead run.
    /// </summary>
    [Fact]
    public void TheVerdictIsTheProcessTablesAnswer_AboutTheRecordedPidAndStartTime()
    {
        var alive = new FakeProcessTable(running: true);
        var gone = new FakeProcessTable(running: false);

        RunLivenessState whenAlive = RunLiveness.Assess(Owner(), Here, alive);
        RunLivenessState whenGone = RunLiveness.Assess(Owner(), Here, gone);

        Assert.NotEqual(whenAlive, whenGone);
        Assert.Equal((14168, Started), Assert.Single(alive.Asked));
        Assert.Equal((14168, Started), Assert.Single(gone.Asked));
    }

    /// <summary>
    /// A recorded end is a fact the process wrote down itself, so it wins over the process table — including the
    /// case where the table says "running", which is every in-process test run and any tool that lingers after
    /// its run is over.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordedEnd_IsFinished_WithoutConsultingTheProcessTable(bool running)
    {
        var table = new FakeProcessTable(running);

        Assert.Equal(
            RunLivenessState.Finished,
            RunLiveness.Assess(Owner(finishedAt: Started.AddHours(1)), Here, table));
        Assert.Empty(table.Asked);
    }

    /// <summary>A journal older than #704 names nothing to check, and the verdict says so instead of guessing.</summary>
    [Fact]
    public void AJournalThatNamesNoOwner_IsNotRecorded()
    {
        var table = new FakeProcessTable(running: true);

        Assert.Equal(RunLivenessState.NotRecorded, RunLiveness.Assess(owner: null, Here, table));
        Assert.Empty(table.Asked);
    }

    /// <summary>
    /// A pid is only meaningful on the machine that issued it. An owner recorded on another host that is not
    /// running HERE is not evidence of a dead run — calling it exited could send an operator to resume a run
    /// that is alive on the other machine.
    /// </summary>
    [Fact]
    public void AnOwnerOnAnotherHost_NotRunningHere_IsOnAnotherHost_NotExited()
    {
        Assert.Equal(
            RunLivenessState.OnAnotherHost,
            RunLiveness.Assess(Owner(host: "BUILD-BOX"), Here, new FakeProcessTable(running: false)));
    }

    /// <summary>
    /// The host check never outranks the process table. A laptop's host name can change under a live run (macOS
    /// renames the machine when it joins another network), and a process running here with the recorded pid and
    /// start time IS the owner, whatever the name now says.
    /// </summary>
    [Fact]
    public void AnOwnerWhoseHostNameChanged_ButWhichIsRunningHere_IsRunning()
    {
        Assert.Equal(
            RunLivenessState.Running,
            RunLiveness.Assess(Owner(host: "Davids-MacBook.local"), "Davids-MacBook", new FakeProcessTable(running: true)));
    }

    /// <summary>Host names are not case-significant; a case-only difference is the same machine.</summary>
    [Fact]
    public void AHostNameDifferingOnlyInCase_IsTheSameHost()
    {
        Assert.Equal(
            RunLivenessState.ExitedWithoutFinishing,
            RunLiveness.Assess(Owner(host: "laptop-7"), Here, new FakeProcessTable(running: false)));
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
