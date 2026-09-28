using System.Diagnostics;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #723: <see cref="ProcessRunner"/> waited for the child's stdout/stderr to reach EOF with no bound,
/// and waited for the process's exit THROUGH that same EOF. A grandchild that inherited the pipes therefore
/// (a) kept a timed-out run hanging after the tree kill, and (b) turned a child that had already exited
/// cleanly into a "timeout" with a fabricated exit -1.
/// <para>
/// Each test's child starts a long-lived grandchild that holds the inherited pipes and writes its PID to a
/// file, so the test can kill it afterwards. The assertions are on what the runner DECIDED (timed out or
/// not, which exit code, whether the drain finished, how the process ended), never on how long it took.
/// The drain's own deadline arithmetic is pinned without any process in <c>DrainPolicyTests</c> (Core).
/// </para>
/// <para>
/// No step here races the clock: a child that must be killed with its grandchild already escaped WAITS for
/// the grandchild's PID file before it starts the sleep it will be killed in, and it starts the grandchild
/// through an intermediate it runs SYNCHRONOUSLY, so the intermediate has exited (and the grandchild is out
/// of the tree) before anything can kill it.
/// </para>
/// </summary>
public sealed class ProcessRunnerDrainBoundTests : IDisposable
{
    /// <summary>A short idle grace for the tests that WANT the drain to give up on a held pipe.</summary>
    private static readonly DrainPolicy GiveUpQuickly = new(
        IdleGrace: TimeSpan.FromSeconds(1),
        AbsoluteCap: TimeSpan.FromSeconds(30),
        CancelledBound: TimeSpan.FromSeconds(1),
        Clock: DrainPolicy.MonotonicClock);

    private readonly string _dir = Directory.CreateTempSubdirectory("gr-723-").FullName;

    private bool _grandchildStarted;

    private string PidFile => Path.Combine(_dir, "grandchild.pid");

    private static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public async Task ChildThatExitsWhileAGrandchildHoldsItsPipes_ReportsItsRealExitCode_NotATimeout()
    {
        ProcessResult result = await new ProcessRunner(GiveUpQuickly).RunAsync(
            ExitsLeavingGrandchildCommand(exitCode: 3),
            _dir,
            new Dictionary<string, string>(),
            timeout: TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "the child exited on its own; that is not a timeout");
        Assert.Equal(3, result.ExitCode);
        Assert.True(result.OutputDrainIncomplete);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[guardrails] output truncated: the process exited,", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutChildWhoseEscapedGrandchildHoldsThePipes_ReturnsWithABoundedDrain()
    {
        // The grandchild is detached from the tree (its immediate parent has exited), so the tree kill cannot
        // reach it and it holds the pipes past the kill. Before #723 the drain waited on it with no bound:
        // returning at all is the decision under test, and the flags say which one. The timeout is generous
        // because the child spends part of it waiting for the grandchild to be established.
        ProcessResult result = await new ProcessRunner(GiveUpQuickly).RunAsync(
            OutlivesItsDeadlineWithEscapedGrandchildCommand(),
            _dir,
            new Dictionary<string, string>(),
            timeout: TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(ProcessRunner.TimeoutExitCode, result.ExitCode);
        Assert.True(result.OutputDrainIncomplete);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[guardrails] output truncated: the process timed out,", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledChildWhoseEscapedGrandchildHoldsThePipes_IsReportedAsKilledOnCancellation()
    {
        // Cancelled by the TEST, once the grandchild's PID file exists: nothing here depends on timing. The idle
        // grace is long, so it is the cancellation bound that ends the drain.
        var policy = GiveUpQuickly with { IdleGrace = TimeSpan.FromSeconds(60), AbsoluteCap = TimeSpan.FromSeconds(120) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<ProcessResult> run = new ProcessRunner(policy).RunAsync(
            OutlivesItsDeadlineWithEscapedGrandchildCommand(),
            _dir,
            new Dictionary<string, string>(),
            timeout: TimeSpan.FromMinutes(10),
            cts.Token);

        await WaitForPidFileAsync();
        await cts.CancelAsync();
        ProcessResult result = await run;

        Assert.False(result.TimedOut, "a cancellation is not a timeout");
        Assert.True(result.OutputDrainIncomplete);
        Assert.Contains("[guardrails] output truncated: the process was killed on cancellation,", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildWithNoGrandchild_DrainsCompletely_AndIsNotFlagged()
    {
        ResolvedCommand command = Windows
            ? Cmd("@echo off\r\necho child-output\r\nexit /b 0\r\n")
            : Bash("echo child-output; exit 0");

        // The production bounds: this asserts a decision (drained, not flagged), so the grace must be far larger
        // than any delay a loaded machine can put between the exit and the pipes' EOF.
        ProcessResult result = await new ProcessRunner().RunAsync(
            command, _dir, new Dictionary<string, string>(), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputDrainIncomplete);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("output truncated", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>Child writes a line, starts a pipe-holding grandchild, and exits with <paramref name="exitCode"/>.</summary>
    private ResolvedCommand ExitsLeavingGrandchildCommand(int exitCode)
    {
        if (Windows)
        {
            // `start /b` shares cmd's handles — including the redirected stdout/stderr pipes.
            return Cmd(
                "@echo off\r\n" +
                "echo child-output\r\n" +
                $"start /b \"\" {WindowsSleeper()}\r\n" +
                $"exit /b {exitCode}\r\n");
        }

        return Bash($"echo child-output; {PosixSleeper()} & exit {exitCode}");
    }

    /// <summary>
    /// Child writes a line and starts a grandchild detached from its process tree: an intermediate, run
    /// SYNCHRONOUSLY, starts the grandchild and exits, so by the time the child continues the grandchild's parent
    /// is gone. The child then waits for the grandchild's PID file and only then starts the long sleep it will be
    /// killed in, so the kill can never land before the grandchild is established.
    /// </summary>
    private ResolvedCommand OutlivesItsDeadlineWithEscapedGrandchildCommand()
    {
        if (Windows)
        {
            string middle = Path.Combine(_dir, "middle.cmd");
            File.WriteAllText(middle, $"@echo off\r\nstart /b \"\" {WindowsSleeper()}\r\nexit /b 0\r\n");
            return Cmd(
                "@echo off\r\n" +
                "echo child-output\r\n" +
                $"cmd.exe /d /c \"{middle}\"\r\n" +
                ":wait\r\n" +
                $"if not exist \"{PidFile}\" (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
                "ping -n 300 127.0.0.1 >nul\r\n");
        }

        // The subshell forks the sleeper and exits, so the sleeper is reparented away from the child.
        return Bash(
            $"echo child-output; ( {PosixSleeper()} & ); " +
            $"while [ ! -s '{PidFile}' ]; do sleep 0.1; done; sleep 300");
    }

    private string WindowsSleeper()
    {
        _grandchildStarted = true;
        return $"{TestShell.WindowsShell} -NoProfile -NonInteractive -Command " +
            $"\"Set-Content -LiteralPath '{PidFile}' -Value $PID; Start-Sleep -Seconds 300\"";
    }

    private string PosixSleeper()
    {
        _grandchildStarted = true;

        // A fresh `sh` rather than $BASHPID, which macOS's bash 3.2 lacks; `exec` keeps the recorded PID the sleeper's.
        return $"sh -c 'echo $$ > \"{PidFile}\"; exec sleep 300'";
    }

    private ResolvedCommand Cmd(string script)
    {
        string path = Path.Combine(_dir, $"child-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, script);
        return new ResolvedCommand { Executable = "cmd.exe", Arguments = ["/d", "/c", path] };
    }

    private static ResolvedCommand Bash(string script) =>
        new() { Executable = "bash", Arguments = ["-c", script] };

    /// <summary>Waits for the grandchild's PID file; the bound is a hung-test guard, not a decision.</summary>
    private async Task WaitForPidFileAsync()
    {
        DateTime guard = DateTime.UtcNow.AddMinutes(2);
        while (ReadPid().Length == 0)
        {
            Assert.True(DateTime.UtcNow < guard, "the grandchild never wrote its PID file");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Kills the pipe-holding grandchild so the suite never leaks a 5-minute sleeper.</summary>
    public void Dispose()
    {
        // The grandchild writes its PID moments after it starts; allow for a slow runner.
        for (int i = 0; i < 100 && _grandchildStarted && ReadPid().Length == 0; i++)
        {
            Thread.Sleep(100);
        }

        if (int.TryParse(ReadPid(), out int pid))
        {
            try
            {
                using Process grandchild = Process.GetProcessById(pid);
                grandchild.Kill(entireProcessTree: true);
                grandchild.WaitForExit(10_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (InvalidOperationException)
            {
                // Exited between lookup and kill.
            }
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a sleeper that was slow to die may still hold the directory on Windows.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The PID the grandchild wrote, or empty when there is none (yet).</summary>
    private string ReadPid()
    {
        try
        {
            return File.Exists(PidFile) ? File.ReadAllText(PidFile).Trim() : string.Empty;
        }
        catch (IOException)
        {
            // Still being written.
            return string.Empty;
        }
    }
}
