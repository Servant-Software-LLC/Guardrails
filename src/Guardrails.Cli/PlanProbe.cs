using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

/// <summary>
/// Shared load + validate plumbing for the CLI commands. Loads a plan, runs semantic
/// validation, prints every diagnostic, and reports whether any errors were found.
/// </summary>
public static class PlanProbe
{
    /// <summary>The combined outcome of loading and validating a plan.</summary>
    public sealed record Result
    {
        public PlanDefinition? Plan { get; init; }
        public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
        public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Load and validate the plan at <paramref name="planFolder"/>.</summary>
    public static Result LoadAndValidate(string planFolder)
    {
        var loader = new PlanLoader();
        PlanLoadResult loadResult = loader.Load(planFolder);

        var diagnostics = new List<Diagnostic>(loadResult.Diagnostics);

        // Only run semantic validation if loading produced a model and had no fatal errors.
        if (loadResult.Plan is not null && !loadResult.HasErrors)
        {
            var validator = new PlanValidator();
            diagnostics.AddRange(validator.Validate(loadResult.Plan));
        }

        return new Result { Plan = loadResult.Plan, Diagnostics = diagnostics };
    }

    /// <summary>Print diagnostics in a stable, scannable format.</summary>
    public static void PrintDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Console.WriteLine(diagnostic.ToString());
        }
    }
}
