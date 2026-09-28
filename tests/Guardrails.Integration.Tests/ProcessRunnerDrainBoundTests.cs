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
/// not, which exit code, whether the drain finished), never on how long it took. The drain bound is
/// shortened through the internal constructor so the suite does not pay the production grace.
/// </para>
/// </summary>
public sealed class ProcessRunnerDrainBoundTests : IDisposable
{
    private static readonly TimeSpan TestDrainGrace = TimeSpan.FromSeconds(1);

    private readonly string _dir = Directory.CreateTempSubdirectory("gr-723-").FullName;

    private bool _grandchildStarted;

    private string PidFile => Path.Combine(_dir, "grandchild.pid");

    private static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public async Task ChildThatExitsWhileAGrandchildHoldsItsPipes_ReportsItsRealExitCode_NotATimeout()
    {
        ProcessResult result = await new ProcessRunner(TestDrainGrace).RunAsync(
            ExitsLeavingGrandchildCommand(exitCode: 3),
            _dir,
            new Dictionary<string, string>(),
            timeout: TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "the child exited on its own; that is not a timeout");
        Assert.Equal(3, result.ExitCode);
        Assert.True(result.OutputDrainIncomplete);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[guardrails] output truncated: the process exited", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutChildWhoseEscapedGrandchildHoldsThePipes_ReturnsWithABoundedDrain()
    {
        // The grandchild is detached from the tree (its immediate parent exits at once), so the tree
        // kill cannot reach it and it holds the pipes past the kill. Before #723 the drain waited on it
        // with no bound: returning at all is the decision under test, and the flags say which one.
        ProcessResult result = await new ProcessRunner(TestDrainGrace).RunAsync(
            OutlivesTimeoutWithEscapedGrandchildCommand(),
            _dir,
            new Dictionary<string, string>(),
            timeout: TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(ProcessRunner.TimeoutExitCode, result.ExitCode);
        Assert.True(result.OutputDrainIncomplete);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[guardrails] output truncated: the process was killed at its timeout", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildWithNoGrandchild_DrainsCompletely_AndIsNotFlagged()
    {
        ResolvedCommand command = Windows
            ? Cmd("@echo off\r\necho child-output\r\nexit /b 0\r\n")
            : Bash("echo child-output; exit 0");

        ProcessResult result = await new ProcessRunner(TestDrainGrace).RunAsync(
            command, _dir, new Dictionary<string, string>(), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputDrainIncomplete);
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
    /// Child writes a line, starts a grandchild detached from its process tree (through an intermediate
    /// that exits at once) that still holds the pipes, then outlives its timeout.
    /// </summary>
    private ResolvedCommand OutlivesTimeoutWithEscapedGrandchildCommand()
    {
        if (Windows)
        {
            string middle = Path.Combine(_dir, "middle.cmd");
            File.WriteAllText(middle, $"@echo off\r\nstart /b \"\" {WindowsSleeper()}\r\nexit /b 0\r\n");
            return Cmd(
                "@echo off\r\n" +
                "echo child-output\r\n" +
                $"start /b \"\" cmd.exe /d /c \"{middle}\"\r\n" +
                "ping -n 300 127.0.0.1 >nul\r\n");
        }

        // The outer subshell forks the sleeper and exits, so the sleeper is reparented away from the child.
        return Bash($"echo child-output; ( {PosixSleeper()} & ); sleep 300");
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

    /// <summary>Kills the pipe-holding grandchild so the suite never leaks a 5-minute sleeper.</summary>
    public void Dispose()
    {
        // The grandchild writes its PID moments after it starts; allow for a slow runner.
        for (int i = 0; i < 100 && _grandchildStarted && !File.Exists(PidFile); i++)
        {
            Thread.Sleep(100);
        }

        if (File.Exists(PidFile)
            && int.TryParse(ReadPid(), out int pid))
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

    private string ReadPid()
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                string text = File.ReadAllText(PidFile).Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }
            catch (IOException)
            {
                // Still being written.
            }

            Thread.Sleep(100);
        }

        return string.Empty;
    }
}
