namespace Guardrails.Core.Execution;

/// <summary>The captured outcome of running a child process.</summary>
public sealed record ProcessResult
{
    /// <summary>Process exit code. <see cref="TimedOut"/> results carry a non-zero sentinel.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured standard error.</summary>
    public required string StandardError { get; init; }

    /// <summary>True if the process was killed because it exceeded its timeout.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>
    /// True when the child exited (or was killed) but its stdout/stderr pipes were still held open when the
    /// bounded drain gave up, typically by a background process the child started (#723). The capture is then
    /// truncated, and <see cref="StandardError"/> ends with a <c>[guardrails] output truncated</c> note that
    /// says so. It is independent of <see cref="TimedOut"/>: a child that exited on its own keeps its real
    /// <see cref="ExitCode"/>.
    /// </summary>
    public bool OutputDrainIncomplete { get; init; }

    /// <summary>Wall-clock duration of the process.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>True when the process exited 0 and did not time out.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
