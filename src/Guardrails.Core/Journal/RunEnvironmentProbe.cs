namespace Guardrails.Core.Journal;

/// <summary>
/// Probes the machine, concurrency and version profile for the run (plan 30 §3.4,
/// <see cref="RunEnvironment"/>). The two version strings are PASSED IN rather than read here: the
/// harness version (<c>GuardrailsVersion.Current</c>) and the installed skill version
/// (<c>SkillVersionReport</c>) both live in <c>Guardrails.Cli</c>, which <c>Guardrails.Core</c> cannot
/// reference, so the caller supplies what only it knows.
/// <para>
/// Stub for <c>17-author-tests-run-environment</c> — implemented by
/// <c>18-record-the-run-environment</c>.
/// </para>
/// </summary>
public static class RunEnvironmentProbe
{
    public static RunEnvironment Probe(int maxParallelism, string? harnessVersion, string? skillVersion)
    {
        throw new NotImplementedException();
    }
}
