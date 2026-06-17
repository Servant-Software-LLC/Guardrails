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
    /// UTF-8 (no BOM) for every redirected stream (issue #55). Without an explicit encoding, .NET
    /// decodes a redirected child's stdout/stderr with <see cref="Console.OutputEncoding"/> — on
    /// Windows the OEM console code page (CP437/850), NOT UTF-8 — so UTF-8 output from a child (e.g.
    /// Claude's em dash <c>—</c> = bytes <c>E2 80 94</c>) is mis-decoded into mojibake (<c>ΓÇö</c>)
    /// and persisted that way to claude-stream.jsonl / transcript.md / *.log. Pinning UTF-8 makes
    /// capture host-console-independent and round-trip-faithful; the no-BOM form matches
    /// <see cref="State.AtomicFile"/> so what we read is what we write.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Run <paramref name="command"/> in <paramref name="workingDirectory"/> with the given
    /// environment overlay and per-process timeout.
    /// </summary>
    public Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        RunAsync(command, workingDirectory, environment, timeout, standardInput: null, stdoutLineSink: null, cancellationToken);

    /// <summary>
    /// Run <paramref name="command"/> with optional STDIN text and an optional per-stdout-line
    /// sink (used by prompt runners to feed the prompt via stdin and tee the raw output stream
    /// to a log). Existing callers use the simpler overload — behaviour there is unchanged.
    /// </summary>
    /// <param name="command">The resolved executable + arguments to launch.</param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="environment">Environment variables applied to the child process.</param>
    /// <param name="timeout">Whole-process timeout; the process tree is killed when it elapses.</param>
    /// <param name="standardInput">Text written to the child's stdin (then closed); null = no stdin redirect.</param>
    /// <param name="stdoutLineSink">Invoked for each stdout line as it arrives (line excludes the newline); null = no tee.</param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed on cancellation.</param>
    public async Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        string? standardInput,
        Action<string>? stdoutLineSink,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Decode the child's bytes as UTF-8 regardless of the host console code page (issue #55).
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        // StandardInputEncoding may be set ONLY when stdin is redirected; assigning it otherwise
        // throws. Pin it too so a composed prompt with non-ASCII (em dashes, quotes) is sent UTF-8.
        if (standardInput is not null)
        {
            startInfo.StandardInputEncoding = Utf8NoBom;
        }

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

        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, stdoutDone, stdoutLineSink);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, stderrDone, lineSink: null);

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            await WriteStandardInputAsync(process, standardInput).ConfigureAwait(false);
        }

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

    private static void Collect(string? data, StringBuilder buffer, SemaphoreSlim done, Action<string>? lineSink)
    {
        if (data is null)
        {
            // Null line = stream closed; signal that this reader has drained.
            done.Release();
            return;
        }

        buffer.AppendLine(data);
        lineSink?.Invoke(data);
    }

    private static async Task WriteStandardInputAsync(Process process, string input)
    {
        try
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child may close stdin early (e.g. it has all it needs); that is not a failure.
        }
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
