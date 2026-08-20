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

        /// <summary>
        /// The WAVE the argument named, when it named a wave folder rather than a plan root (issue #472) —
        /// resolved through <see cref="Plan"/>, which is then the PARENT plan. Null for an ordinary
        /// plan-folder target, which is every flat-plan invocation and today's whole behaviour.
        /// </summary>
        public WaveNode? Wave { get; init; }

        public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
        public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Load and validate the target at <paramref name="folder"/>, accepting EITHER a plan folder OR a
    /// WAVE folder of a nested plan (issue #472). A wave carries no <c>guardrails.json</c> by design
    /// (SSOT §14.1), so it is resolved <b>through its parent plan</b> — load the one plan, select the
    /// <see cref="WaveNode"/> — rather than being made independently loadable. There is deliberately one
    /// spelling: the wave folder is the ordinary positional argument (no <c>--wave</c> flag; design
    /// <c>20-jit-breakdown-durability.md</c> §8.2/C5).
    ///
    /// <para>Used by the two verbs that operate on an ATTESTATION TARGET — <c>plan-hash</c> and
    /// <c>mark-reviewed</c>. <c>validate</c> deliberately does NOT use it: validating a wave means
    /// validating something other than what was asked, so it keeps erroring, with the targeted
    /// <c>GR1010</c> pointer instead of a bare <c>GR1001</c>.</para>
    /// </summary>
    public static Result LoadAndValidateTarget(string folder)
    {
        if (!WaveFolder.TryResolveWaveTarget(folder, out string planRoot, out string waveDir))
        {
            return LoadAndValidate(folder);
        }

        Result plan = LoadAndValidate(planRoot);
        WaveNode? wave = plan.Plan?.Waves
            .FirstOrDefault(w => string.Equals(w.Dir, waveDir, StringComparison.Ordinal));

        if (wave is null && !plan.HasErrors)
        {
            // Near-unreachable (a conforming dir under a plan root loads AS a wave, or the plan itself
            // errors first — GR2032/GR2033), but never guess: say what was looked for and where.
            return plan with
            {
                Diagnostics =
                [
                    .. plan.Diagnostics,
                    new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Code = DiagnosticCodes.WaveFolderIsNotALoadablePlan,
                        Path = Path.GetFullPath(folder),
                        Message = $"The plan at '{planRoot}' does not carry a wave named '{waveDir}'."
                    }
                ]
            };
        }

        return plan with { Wave = wave };
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

    /// <summary>Print diagnostics in a stable, scannable format to <paramref name="output"/>.</summary>
    public static void PrintDiagnostics(IReadOnlyList<Diagnostic> diagnostics, TextWriter output)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            output.WriteLine(diagnostic.ToString());
        }
    }
}
