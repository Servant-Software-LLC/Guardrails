using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// The outcome of <see cref="PlanLoader.Load"/>: the loaded plan (null if a fatal
/// structural problem prevented loading) plus all diagnostics gathered along the way.
/// </summary>
public sealed record PlanLoadResult
{
    /// <summary>The loaded plan, or null when loading could not produce a usable model.</summary>
    public PlanDefinition? Plan { get; init; }

    /// <summary>Every diagnostic emitted during loading (errors and warnings).</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>True if any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
