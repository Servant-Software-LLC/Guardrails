using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// The per-wave <c>delivers</c> flag (design 39 §1b/§3, issue #360's incremental-delivery follow-on):
/// <c>delivers: true</c> in the YAML front matter of a wave's OPTIONAL <see cref="WaveNode.BriefFileName"/>
/// (<c>brief.md</c>) — NOT a new per-wave manifest, since SSOT §14.1 has none in v1. Two members carry two
/// distinct facts (review, 2026-09-13): <see cref="WaveNode.Delivers"/> is the DECLARED flag (what
/// <c>GR2079</c> — a wave that sets <c>delivers: true</c> but has no exit gate — must name), and
/// <see cref="WaveNode.IsDeliveryPoint"/> is the EFFECTIVE predicate the barrier delivery actually acts on
/// (<see cref="WaveNode.Delivers"/> AND at least one exit-gate check, since an empty exit gate trivially
/// passes and would otherwise let a gate-less wave "deliver" behind zero checks).
///
/// <para><b>The default is the whole safety property.</b> A plan that marks no wave must behave
/// byte-identically to today: one merge at run end. Default <c>true</c> was rejected because it would turn
/// every already-waved plan into a per-wave deliverer on upgrade, without anyone asking for it.</para>
///
/// <para>Both members are stubbed with THROWING getters (task 01): a working <c>bool Delivers { get;
/// init; }</c> defaults to <c>false</c>, which is exactly what the default-false and both
/// <see cref="WaveNode.IsDeliveryPoint"/>-is-false rows below assert — so an unimplemented property would
/// pass those rows and defeat the TDD red census. Every row here is built through
/// <see cref="WavePlanBuilder"/> and loaded through the real <see cref="PlanLoader"/>, so each reads the
/// members off a node the loader actually built.</para>
/// </summary>
public sealed class WaveDeliversFlagTests
{
    private const string DeliversTrueBrief = "---\ndelivers: true\n---\n\nintent\n";
    private const string DeliversFalseBrief = "---\ndelivers: false\n---\n\nintent\n";

    private static WaveNode FirstWave(WavePlanBuilder plan)
    {
        PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Plan!.Waves[0];
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Delivers_DefaultsToFalse_WhenTheManifestOmitsIt()
    {
        // No brief.md anywhere: nothing marks the wave, so it must default to false — the never-weaker
        // requirement an upgraded plan relies on.
        using var plan = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        WaveNode wave = FirstWave(plan);

        Assert.False(wave.Delivers);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Delivers_IsTrue_WhenTheManifestSetsIt()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        WaveNode wave = FirstWave(plan);

        Assert.True(wave.Delivers);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void WaveDefinitionHash_ChangesWhenDeliversChanges()
    {
        // DECLARED EXEMPT from the red census: WaveDefinitionHash.Compute -> GateDefinitionOf already
        // folds brief.md whenever it is present, so this is free with the chosen surface and green on
        // arrival. It stays green here because it never touches Delivers/IsDeliveryPoint — only the
        // pre-existing, already-implemented hash fold over the brief's bytes on disk. Flipping the flag
        // on an otherwise-completed wave must move the wave's definition hash, so a change to delivery
        // behaviour trips drift (SSOT §14.6/§14.7) instead of passing silently under a review marker
        // that still reads `passed`.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversFalseBrief);

        WaveNode wave = FirstWave(plan);
        string before = WaveDefinitionHash.Compute(wave);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", WaveNode.BriefFileName),
            DeliversTrueBrief);

        string after = WaveDefinitionHash.Compute(wave);
        Assert.NotEqual(before, after);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void AWaveWithNoGuardrailsFolder_IsNeverADeliveryPoint()
    {
        // SSOT §3: no exit gate, no delivery, regardless of the flag. This wave has no
        // <waveDir>/guardrails/ folder at all (only its task's OWN guardrails, which are not the wave's
        // exit/terminal gate) — it waits for the run-end delivery like today.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        WaveNode wave = FirstWave(plan);

        Assert.False(wave.IsDeliveryPoint);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void Delivers_StaysTheDeclaredFlag_WhenTheWaveHasNoExitGate()
    {
        // The same gate-less wave as above still reports its DECLARED flag as true — rejects folding
        // the gate check into Delivers itself, which would leave GR2079 nothing to fire on (GR2079 must
        // name exactly the waves whose flag cannot take effect).
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        WaveNode wave = FirstWave(plan);

        Assert.True(wave.Delivers);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void IsDeliveryPoint_IsTrue_WhenTheWaveDeliversAndHasAnExitGate()
    {
        // Rejects a predicate that is always false: delivers:true plus a real exit-gate check must
        // together make this wave a delivery point.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        WaveNode wave = FirstWave(plan);

        Assert.True(wave.IsDeliveryPoint);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void IsDeliveryPoint_IsFalse_WhenTheManifestOmitsDelivers()
    {
        // Rejects treating "has an exit gate" alone as a delivery point, which would turn every gated
        // wave of an existing waved plan into a per-wave deliverer on upgrade. No brief.md at all: the
        // manifest omits the delivers key entirely.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n");

        WaveNode wave = FirstWave(plan);

        Assert.False(wave.IsDeliveryPoint);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void APlanMarkingNoWave_LoadsIdenticallyToBefore()
    {
        // DECLARED EXEMPT from the red census: it pins behaviour that must NOT change, so a correct test
        // has nothing to be red about. Deliberately never touches Delivers/IsDeliveryPoint — the point is
        // that a plan marking no wave loads exactly as it always has, at the LOADING level: same wave
        // shape, zero errors, and a deterministic hash across repeated loads of the same unedited tree.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n");

        PlanLoadResult first = plan.Load();
        Assert.False(first.HasErrors, string.Join("\n", first.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        WaveNode wave = first.Plan!.Waves[0];

        Assert.Equal("wave-01-scaffold", wave.Dir);
        Assert.Single(wave.Tasks);
        Assert.Single(wave.Guardrails);
        Assert.Empty(wave.Preflights);

        PlanLoadResult second = plan.Load();
        WaveNode secondWave = second.Plan!.Waves[0];
        Assert.Equal(WaveDefinitionHash.Compute(wave), WaveDefinitionHash.Compute(secondWave));
    }
}
