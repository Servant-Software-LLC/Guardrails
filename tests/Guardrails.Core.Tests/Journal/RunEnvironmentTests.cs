using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// <c>RunEnvironmentProbe</c> (plan 30 §3.4) — the machine, concurrency and version profile probed once
/// per run. Four behaviours, each pinned to an exact method name a companion guardrail binds to in the
/// runner's own TRX.
///
/// <para><b>TDD red.</b> Every test here calls <see cref="RunEnvironmentProbe.Probe"/>, which throws
/// <see cref="NotImplementedException"/> unconditionally until <c>18-record-the-run-environment</c>
/// fills it in — so every test below fails, on purpose, against this tree. A test that never reaches
/// the throw (for example one that reads <c>Environment.MachineName</c> or
/// <c>GC.GetGCMemoryInfo()</c> and asserts about its own value instead of calling
/// <see cref="RunEnvironmentProbe.Probe"/>) would pass today and forever without proving anything about
/// the probe — that hollow shape is exactly what the guardrail is watching for.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class RunEnvironmentTests
{
    [Fact]
    public void TheProbeRecordsHostOsAndCpuCount()
    {
        RunEnvironment env = RunEnvironmentProbe.Probe(maxParallelism: 1, harnessVersion: null, skillVersion: null);

        Assert.False(string.IsNullOrEmpty(env.Host));
        Assert.False(string.IsNullOrEmpty(env.Os));
        Assert.True(env.CpuCount is > 0);
    }

    [Fact]
    public void TheProbeRecordsTotalMemory_ForTheUnifiedMemoryComparison()
    {
        RunEnvironment env = RunEnvironmentProbe.Probe(maxParallelism: 1, harnessVersion: null, skillVersion: null);

        // The whole reason this figure is on the record at all (plan 30 §3.4): the same model name runs
        // at a different quantization depending on how much unified memory the box has, so two rows
        // naming the same model must not be pooled as one sample without it.
        Assert.True(env.TotalMemoryBytes is > 0);
    }

    [Fact]
    public void TheProbeRecordsTheEffectiveConcurrency_NotTheConfiguredOne()
    {
        // The probe cannot see a "configured" parallelism at all — only what it is handed — so 1 is a
        // value that could not have leaked in from anywhere else (e.g. Environment.ProcessorCount, on any
        // box with more than one core).
        RunEnvironment env = RunEnvironmentProbe.Probe(maxParallelism: 1, harnessVersion: null, skillVersion: null);

        // Load-bearing half: holds on every box, including a single-core CI runner. This is what catches
        // an implementation that conflates MaxParallelism with CpuCount.
        Assert.Equal(1, env.MaxParallelism);

        // Corroborating half: on any box with more than one core, MaxParallelism and CpuCount must be
        // genuinely distinct fields with distinct values. On a single-core box this assertion is vacuous
        // (CpuCount == 1 == MaxParallelism regardless of what the probe does), so it is guarded rather
        // than treated as load-bearing.
        if (Environment.ProcessorCount > 1)
        {
            Assert.NotEqual(env.CpuCount, env.MaxParallelism);
        }
    }

    [Fact]
    public void TheProbeRecordsTheVersionsItIsGiven_AndNullsItIsNotGiven()
    {
        RunEnvironment withVersions = RunEnvironmentProbe.Probe(
            maxParallelism: 1, harnessVersion: "1.0.0-preview.40", skillVersion: "3");

        Assert.Equal("1.0.0-preview.40", withVersions.HarnessVersion);
        Assert.Equal("3", withVersions.SkillVersion);

        RunEnvironment withoutVersions = RunEnvironmentProbe.Probe(
            maxParallelism: 1, harnessVersion: null, skillVersion: null);

        // Legitimately null when no skill is installed — never an empty string, never a fabricated
        // default.
        Assert.Null(withoutVersions.HarnessVersion);
        Assert.Null(withoutVersions.SkillVersion);
    }
}
