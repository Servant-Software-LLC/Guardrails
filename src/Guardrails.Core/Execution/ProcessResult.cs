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

    /// <summary>Wall-clock duration of the process.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>True when the process exited 0 and did not time out.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
