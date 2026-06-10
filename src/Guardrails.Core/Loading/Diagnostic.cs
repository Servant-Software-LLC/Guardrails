namespace Guardrails.Core.Loading;

/// <summary>Severity of a validation/loading diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>A problem that prevents the plan from being valid/runnable.</summary>
    Error,

    /// <summary>A non-fatal advisory.</summary>
    Warning
}

/// <summary>
/// A single precise diagnostic from loading or validating a plan. Carries a stable
/// <see cref="Code"/> (asserted by tests), the offending file (if any), and a
/// human-actionable reason.
/// </summary>
public sealed record Diagnostic
{
    /// <summary>Stable machine code, e.g. "GR1001". Tests assert on this.</summary>
    public required string Code { get; init; }

    /// <summary>Severity — errors fail validation; warnings do not.</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>The file or folder the diagnostic is about; null for plan-wide issues.</summary>
    public string? Path { get; init; }

    /// <summary>Human-actionable explanation.</summary>
    public required string Message { get; init; }

    public override string ToString()
    {
        string location = Path is null ? string.Empty : $" [{Path}]";
        return $"{Severity.ToString().ToUpperInvariant()} {Code}{location}: {Message}";
    }
}
