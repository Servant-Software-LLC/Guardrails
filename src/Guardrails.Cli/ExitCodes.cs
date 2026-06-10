namespace Guardrails.Cli;

/// <summary>Process exit codes (SSOT §7).</summary>
public static class ExitCodes
{
    /// <summary>Everything green.</summary>
    public const int Success = 0;

    /// <summary>Harness or validation error (the plan could not be run).</summary>
    public const int HarnessError = 1;

    /// <summary>The run completed but at least one task needs a human (or was blocked).</summary>
    public const int TaskFailed = 2;

    /// <summary>The run was cancelled (Ctrl+C); in-flight tasks were journaled back to pending.</summary>
    public const int Cancelled = 3;
}
