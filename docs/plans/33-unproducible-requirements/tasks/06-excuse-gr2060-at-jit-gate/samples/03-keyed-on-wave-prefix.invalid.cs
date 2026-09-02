// The DEFECT: the excuse made unconditional: GR2060 excused at the JIT breakdown gate, keyed on
// wavePrefixIsIncomplete, with PlanIsClosed named only in a TRAILING comment - the placement that used
// to false-red this guardrail when its comment strip was anchored to line-leading // only.
namespace Guardrails.Core.Execution;

internal static class SchedulerExcerpt
{
    private static (bool Valid, string Report) ValidatePlanAfterBreakdown(
        string planDirectory, string waveDir, bool wavePrefixIsIncomplete = false)
    {
        Diagnostic[] errors = Validate(planDirectory);

        Diagnostic[] excused = errors.Where(UnsatisfiableWhileIncomplete).ToArray();
        Diagnostic[] blocking = errors.Except(excused).ToArray();

        return (blocking.Length == 0, Describe(errors, excused));
    }

    internal static bool UnsatisfiableWhileIncomplete(Diagnostic diagnostic) =>
        string.Equals(diagnostic.Code, DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, StringComparison.Ordinal)
        || string.Equals(diagnostic.Code, DiagnosticCodes.UnproducibleGateRequirement, StringComparison.Ordinal); // PlanIsClosed is NOT the predicate here - it returns true for an authored partial prefix.
}
