using System.Diagnostics;
using System.Text;

namespace Guardrails.Core.Execution;

/// <summary>
/// Spawns child processes per the child-process contract (SSOT §5.1): arguments via
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a concatenated shell string),
/// cwd = the resolved workspace, env vars injected, stdout/stderr captured, and a
/// timeout enforced with <c>Kill(entireProcessTree: true)</c>.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>Exit-code sentinel reported when a process is killed for exceeding its timeout.</summary>
    public const int TimeoutExitCode = -1;

    /// <summary>
    /// Run <paramref name="command"/> in <paramref name="workingDirectory"/> with the given
    /// environment overlay and per-process timeout.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string> variable in environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var stdoutDone = new SemaphoreSlim(0, 1);
        using var stderrDone = new SemaphoreSlim(0, 1);

        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, stdoutDone);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, stderrDone);

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            KillTree(process);
        }

        // Drain the async readers so captured output is complete before we return.
        await stdoutDone.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await stderrDone.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        int exitCode = timedOut ? TimeoutExitCode : SafeExitCode(process);

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed
        };
    }

    private static void Collect(string? data, StringBuilder buffer, SemaphoreSlim done)
    {
        if (data is null)
        {
            // Null line = stream closed; signal that this reader has drained.
            done.Release();
            return;
        }

        buffer.AppendLine(data);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill — nothing to do.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return TimeoutExitCode;
        }
    }
}
