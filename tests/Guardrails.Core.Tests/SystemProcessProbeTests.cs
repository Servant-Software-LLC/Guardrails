using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #704 — the PRODUCTION process table behind the run-liveness verdict.
/// <para>
/// <b>Why there is no tolerance anywhere.</b> Windows and macOS store a process's creation time in the kernel and
/// .NET returns it unchanged, so every reader sees the same value to the tick. On Linux .NET does NOT: it rebuilds a
/// wall-clock start time in each reading process from a boot time it caches as
/// <c>CLOCK_REALTIME_COARSE − CLOCK_BOOTTIME</c>, so the run and a later <c>status</c> disagree — by milliseconds
/// normally, and by hours in a WSL2 / VM / devcontainer guest whose wall clock steps forward after the host slept
/// while its boot clock never counted the sleep. So on Linux the identity is what the kernel itself stores: the
/// process's start in clock ticks since boot (<c>/proc/&lt;pid&gt;/stat</c> field 22) plus the boot it started in
/// (<c>/proc/sys/kernel/random/boot_id</c>). Neither moves with any clock, so the comparison is exact.
/// </para>
/// <para>
/// The pure comparisons are exercised on EVERY OS (they take the kernel's text as input), and the live checks run
/// against this test process and a pid no process can have — no timing in any assertion.
/// </para>
/// </summary>
public sealed class SystemProcessProbeTests
{
    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 9, 13, 4, 15, 59, TimeSpan.Zero).AddTicks(8_123_456);

    private static RunOwner LinuxOwner(long? startTicks = 4242, string? bootId = "boot-a") => new()
    {
        Pid = 14168,
        ProcessStartedAt = Started,
        ProcessStartTicks = startTicks,
        BootId = bootId
    };

    /// <summary>A real <c>/proc/&lt;pid&gt;/stat</c> line whose field 22 (<c>starttime</c>) is <paramref name="startTicks"/>.</summary>
    private static string Stat(long startTicks) =>
        $"14168 (my (odd) proc) S 1 14168 14168 0 -1 4194560 1200 0 0 0 150 30 0 0 20 0 12 0 {startTicks} 123456789 2000";

    // ── The live checks ──────────────────────────────────────────────────────────────────────────

    /// <summary>What a run stamps about itself must be what the probe later recognizes as that same live process.</summary>
    [Fact]
    public void ThisProcess_AsItRecordsItself_IsRunning()
    {
        RunOwner? self = RunLiveness.OwnerForThisProcess();

        Assert.NotNull(self);
        Assert.Equal(Environment.ProcessId, self!.Pid);
        Assert.Equal(Environment.MachineName, self.Host);
        Assert.Equal(ProcessCheck.Running, SystemProcessProbe.Instance.Check(self));
    }

    /// <summary>The kernel identity is recorded exactly where it is the identity — Linux — and nowhere else.</summary>
    [Fact]
    public void ThisProcess_RecordsTheKernelStartIdentity_OnLinuxOnly()
    {
        RunOwner self = RunLiveness.OwnerForThisProcess()!;

        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(self.ProcessStartTicks);
            Assert.False(string.IsNullOrWhiteSpace(self.BootId));
        }
        else
        {
            Assert.Null(self.ProcessStartTicks);
            Assert.Null(self.BootId);
        }
    }

    [Fact]
    public void APidNoProcessHas_IsNotRunning()
    {
        RunOwner self = RunLiveness.OwnerForThisProcess()!;

        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.Instance.Check(self with { Pid = 999_999_999 }));
    }

    /// <summary>
    /// The pid-reuse guard, live: this process's own pid with a different start identity is a different process —
    /// by one tick of start time where the OS stores it, and by one clock tick of kernel start on Linux.
    /// </summary>
    [Fact]
    public void ALivePidWithAStartIdentityOneTickOff_IsNotRunning()
    {
        RunOwner self = RunLiveness.OwnerForThisProcess()!;
        RunOwner impostor = self with
        {
            ProcessStartedAt = self.ProcessStartedAt.AddTicks(1),
            ProcessStartTicks = self.ProcessStartTicks + 1
        };

        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.Instance.Check(impostor));
    }

    // ── Windows / macOS: the stored start time, compared exactly ─────────────────────────────────

    [Fact]
    public void WhereTheOsStoresTheStartTime_OneTickApartIsADifferentProcess()
    {
        RunOwner recorded = LinuxOwner(startTicks: null, bootId: null);

        Assert.Equal(ProcessCheck.Running, SystemProcessProbe.CompareStartTime(recorded, Started));
        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.CompareStartTime(recorded, Started.AddTicks(1)));
        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.CompareStartTime(recorded, Started.AddTicks(-1)));
    }

    // ── Linux: the kernel's own start ticks and boot id, compared exactly ────────────────────────

    [Fact]
    public void OnLinux_TheSameBootAndTheSameKernelStart_IsRunning()
    {
        Assert.Equal(ProcessCheck.Running, SystemProcessProbe.CompareLinux(LinuxOwner(), "boot-a", Stat(4242)));
    }

    /// <summary>
    /// A clock step is invisible here: the wall-clock start time is not consulted at all on Linux. This is the
    /// WSL2 / VM / devcontainer case — the guest clock jumped hours forward on wake — and the run is still RUNNING.
    /// </summary>
    [Fact]
    public void OnLinux_AWallClockStartTimeHoursOff_DoesNotMatter()
    {
        RunOwner recorded = LinuxOwner() with { ProcessStartedAt = Started.AddHours(-9) };

        Assert.Equal(ProcessCheck.Running, SystemProcessProbe.CompareLinux(recorded, "boot-a", Stat(4242)));
    }

    /// <summary>A different boot proves a reboot since the owner recorded itself: nothing from that boot is alive.</summary>
    [Fact]
    public void OnLinux_ADifferentBoot_IsNotRunning_EvenWithAMatchingKernelStart()
    {
        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.CompareLinux(LinuxOwner(), "boot-b", Stat(4242)));
    }

    [Fact]
    public void OnLinux_TheSameBootAndAKernelStartOneTickOff_IsAReusedPid()
    {
        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.CompareLinux(LinuxOwner(), "boot-a", Stat(4243)));
    }

    [Fact]
    public void OnLinux_NoProcessWithThePid_IsNotRunning()
    {
        Assert.Equal(ProcessCheck.NotRunning, SystemProcessProbe.CompareLinux(LinuxOwner(), "boot-a", stat: null));
    }

    /// <summary>
    /// An owner recorded without the kernel identity — written on another OS, say — cannot be compared on Linux.
    /// Falling back to the reconstructed wall-clock time would reintroduce the clock this design exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OnLinux_AnOwnerRecordedWithoutTheKernelIdentity_CannotTell(bool hasTicks, bool hasBootId)
    {
        RunOwner recorded = LinuxOwner(startTicks: hasTicks ? 4242 : null, bootId: hasBootId ? "boot-a" : null);

        Assert.Equal(ProcessCheck.CannotTell, SystemProcessProbe.CompareLinux(recorded, "boot-a", Stat(4242)));
    }

    [Fact]
    public void OnLinux_AStatLineThatCannotBeParsed_CannotTell()
    {
        Assert.Equal(ProcessCheck.CannotTell, SystemProcessProbe.CompareLinux(LinuxOwner(), "boot-a", "14168 (truncated"));
    }

    /// <summary>
    /// The process name (field 2) is parenthesized and may itself contain spaces and parentheses, so fields are
    /// counted from the LAST <c>)</c> — splitting the whole line on spaces would read the wrong field.
    /// </summary>
    [Fact]
    public void ParseStartTicks_CountsFieldsFromTheLastParenthesis()
    {
        Assert.Equal(4242, SystemProcessProbe.ParseStartTicks(Stat(4242)));
    }
}
