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
    public static RunEnvironment Probe(int maxParallelism, string? harnessVersion, string? skillVersion) =>
        new()
        {
            Host = TryGetHost(),
            Os = TryGetOs(),
            CpuCount = TryGetCpuCount(),
            TotalMemoryBytes = TryGetTotalMemoryBytes(),
            MaxParallelism = maxParallelism,
            HarnessVersion = harnessVersion,
            SkillVersion = skillVersion
        };

    /// <summary>Each machine fact is probed independently so one failing call (e.g. a sandboxed
    /// environment denying <see cref="Environment.MachineName"/>) leaves that single member absent
    /// rather than losing the whole record — the probe itself must never throw.</summary>
    private static string? TryGetHost()
    {
        try { return Environment.MachineName; }
        catch { return null; }
    }

    private static string? TryGetOs()
    {
        try { return Environment.OSVersion.ToString(); }
        catch { return null; }
    }

    private static int? TryGetCpuCount()
    {
        try { return Environment.ProcessorCount; }
        catch { return null; }
    }

    /// <summary>On Apple silicon this is the unified memory pool (plan 30 §3.4) — see
    /// <see cref="RunEnvironment.TotalMemoryBytes"/> for why the member is named this way.</summary>
    private static long? TryGetTotalMemoryBytes()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { return null; }
    }
}
