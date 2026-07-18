using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// <see cref="WaveDefinitionHash"/> composition (SSOT §7.2/§7.3/§14.5): it folds each constituent task's
/// <see cref="TaskDefinitionHash"/> value plus the wave's own preflight/guardrail gate files, and it
/// EXCLUDES the shared <c>guardrails.json</c> (Open Decision C) so a config edit never re-stales an
/// already-run wave. Folding the child hashes guarantees the wave hash changes iff a constituent task hash
/// or a wave-gate file changes.
/// </summary>
public sealed class WaveDefinitionHashTests
{
    private static WaveNode FirstWave(WavePlanBuilder plan)
    {
        Loading.PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Plan!.Waves[0];
    }

    [Fact]
    public void Compute_IsDeterministic_AndSha256Prefixed()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-01-scaffold", "02-config");

        WaveNode wave = FirstWave(plan);
        string a = WaveDefinitionHash.Compute(wave);
        string b = WaveDefinitionHash.Compute(wave);

        Assert.StartsWith("sha256:", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void EditingAConstituentTaskFile_ChangesTheWaveHash()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-01-scaffold", "02-config");

        WaveNode wave = FirstWave(plan);
        string before = WaveDefinitionHash.Compute(wave);

        // Edit a task's action body — the fold over TaskDefinitionHash must propagate it up.
        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "tasks", "01-init", "action.sh"),
            "#!/bin/sh\necho CHANGED\n");

        string after = WaveDefinitionHash.Compute(wave);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void EditingAWaveGateFile_ChangesTheWaveHash()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n");

        WaveNode wave = FirstWave(plan);
        string before = WaveDefinitionHash.Compute(wave);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "guardrails", "01-exit.sh"),
            "# catches: a wrong implementation\nexit 1\n");

        string after = WaveDefinitionHash.Compute(wave);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void EditingTheSharedGuardrailsJson_DoesNotChangeTheWaveHash()
    {
        // Open Decision C (SSOT §7.2): the shared guardrails.json is EXCLUDED from the wave hash.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init");

        WaveNode wave = FirstWave(plan);
        string before = WaveDefinitionHash.Compute(wave);

        File.WriteAllText(Path.Combine(plan.PlanDir, "guardrails.json"),
            """{ "version": 1, "maxParallelism": 1, "defaultRetries": 5 }""");

        string after = WaveDefinitionHash.Compute(wave);
        Assert.Equal(before, after);
    }

    [Fact]
    public void BriefMd_IsIncludedInWaveHash_ButExcludedFromPlanDefinitionHash()
    {
        // SSOT §14.10 (#360 Phase 0): brief.md is folded into WaveDefinitionHash (a changed/added/removed
        // brief on a COMPLETED wave is legitimate drift) but NEVER into PlanDefinitionHash (it is breakdown
        // INPUT, not the reviewed OUTPUT a /guardrails-review pass scrutinizes).
        using var plan = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        Loading.PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        PlanDefinition loaded = result.Plan!;
        WaveNode wave = loaded.Waves[0];

        string waveBriefless = WaveDefinitionHash.Compute(wave);
        string planBefore = PlanDefinitionHash.Compute(loaded);

        string briefPath = Path.Combine(plan.PlanDir, "wave-01-scaffold", WaveNode.BriefFileName);

        // Add a brief.md.
        File.WriteAllText(briefPath, "# wave-01\nintent v1\n");
        string waveWithBrief = WaveDefinitionHash.Compute(wave);
        Assert.NotEqual(waveBriefless, waveWithBrief);        // INCLUDED: adding a brief moves the wave hash
        Assert.Equal(planBefore, PlanDefinitionHash.Compute(loaded)); // EXCLUDED: the plan-definition hash is stable

        // Edit the brief content.
        File.WriteAllText(briefPath, "# wave-01\nintent v2 (edited)\n");
        Assert.NotEqual(waveWithBrief, WaveDefinitionHash.Compute(wave)); // editing the brief moves the wave hash

        // Remove the brief.
        File.Delete(briefPath);
        Assert.Equal(waveBriefless, WaveDefinitionHash.Compute(wave)); // removing it restores the briefless hash
        Assert.Equal(planBefore, PlanDefinitionHash.Compute(loaded));  // plan hash stayed stable throughout
    }

    [Fact]
    public void AddingATaskToTheWave_ChangesTheWaveHash()
    {
        using var single = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");
        string oneTask = WaveDefinitionHash.Compute(FirstWave(single));

        using var twoTasks = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-01-scaffold", "02-more");
        string twoTaskHash = WaveDefinitionHash.Compute(FirstWave(twoTasks));

        Assert.NotEqual(oneTask, twoTaskHash);
    }
}
