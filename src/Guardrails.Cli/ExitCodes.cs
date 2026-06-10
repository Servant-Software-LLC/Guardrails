namespace Guardrails.Cli;

/// <summary>
/// Process exit codes (SSOT §7). M2 maps "≥1 task failed" onto code 2 — the stand-in
/// for needs-human until the journal/retry machinery lands in M3/M4.
/// </summary>
public static class ExitCodes
{
    /// <summary>Everything green.</summary>
    public const int Success = 0;

    /// <summary>Harness or validation error (the plan could not be run).</summary>
    public const int HarnessError = 1;

    /// <summary>The run completed but at least one task failed or was blocked.</summary>
    public const int TaskFailed = 2;

    // Code 3 (cancelled) is reserved; Ctrl+C handling arrives in M4.
}
