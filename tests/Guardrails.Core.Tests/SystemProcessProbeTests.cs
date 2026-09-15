using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #704 — the PRODUCTION process table behind the run-liveness verdict, against real processes and with no
/// timing in any assertion: this test process is alive for the whole test, a pid no process can have does not
/// exist, and the start-time tolerance is asserted as a DECISION at its boundary rather than by waiting.
/// </summary>
public sealed class SystemProcessProbeTests
{
    /// <summary>What a run stamps about itself must be what the probe later recognizes as that same live process.</summary>
    [Fact]
    public void ThisProcess_AsItRecordsItself_IsRunning()
    {
        RunOwner self = RunLiveness.OwnerForThisProcess();

        Assert.Equal(Environment.ProcessId, self.Pid);
        Assert.True(SystemProcessProbe.Instance.IsRunning(self.Pid, self.ProcessStartedAt));
    }

    [Fact]
    public void OwnerForThisProcess_NamesThisHost()
    {
        Assert.Equal(Environment.MachineName, RunLiveness.OwnerForThisProcess().Host);
        Assert.Equal(Environment.MachineName, RunLiveness.ThisHost());
    }

    [Fact]
    public void APidNoProcessHas_IsNotRunning()
    {
        Assert.False(SystemProcessProbe.Instance.IsRunning(999_999_999, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The pid-reuse guard. The OS hands a freed pid to the next process that asks, so a live process holding the
    /// recorded pid is the owner only if it also started when the owner did. A day apart is outside any tolerance
    /// on any OS.
    /// </summary>
    [Fact]
    public void ALivePidWithADifferentStartTime_IsNotRunning()
    {
        RunOwner self = RunLiveness.OwnerForThisProcess();

        Assert.False(SystemProcessProbe.Instance.IsRunning(self.Pid, self.ProcessStartedAt.AddDays(-1)));
    }

    /// <summary>The tolerance is inclusive at its edge and exclusive one tick past it, in both directions.</summary>
    [Fact]
    public void TwoReadingsOfAStartTime_MatchUpToTheToleranceAndNotOneTickBeyond()
    {
        DateTimeOffset recorded = new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456);
        TimeSpan tolerance = SystemProcessProbe.StartTimeTolerance;
        TimeSpan tick = TimeSpan.FromTicks(1);

        Assert.True(SystemProcessProbe.IsSameStart(recorded, recorded));
        Assert.True(SystemProcessProbe.IsSameStart(recorded + tolerance, recorded));
        Assert.True(SystemProcessProbe.IsSameStart(recorded - tolerance, recorded));
        Assert.False(SystemProcessProbe.IsSameStart(recorded + tolerance + tick, recorded));
        Assert.False(SystemProcessProbe.IsSameStart(recorded - tolerance - tick, recorded));
    }

    /// <summary>
    /// Exact where the OS keeps the creation time and every reader sees the same value; bounded on Linux, where
    /// .NET reconstructs it per reading process (see <see cref="SystemProcessProbe.StartTimeTolerance"/>).
    /// </summary>
    [Fact]
    public void TheTolerance_IsZeroWhereTheOsKeepsTheStartTime_AndOneMinuteOnLinux()
    {
        Assert.Equal(
            OperatingSystem.IsLinux() ? TimeSpan.FromMinutes(1) : TimeSpan.Zero,
            SystemProcessProbe.StartTimeTolerance);
    }
}
