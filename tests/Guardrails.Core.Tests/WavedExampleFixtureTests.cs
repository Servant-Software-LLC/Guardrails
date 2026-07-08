using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// The committed <c>waved-example</c> fixture (SSOT §14) — a real two-wave nested-layout plan — loads and
/// VALIDATES clean, proving the new waved path end-to-end on a durable artifact (the flat golden example
/// stays clean too; that no-regression is covered by the existing fixtures).
/// </summary>
public sealed class WavedExampleFixtureTests
{
    private static PlanLoadResult Load() => new PlanLoader().Load(TestPaths.Fixture("waved-example"));

    [Fact]
    public void WavedExample_LoadsWithNoErrors_AndIsWaved()
    {
        PlanLoadResult result = Load();

        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}")));
        Assert.True(result.Plan!.IsWaved);
        Assert.Equal(["wave-01-scaffold", "wave-02-build"], result.Plan.Waves.Select(w => w.Dir));
        Assert.Equal(
            ["wave-01-scaffold/01-create-config", "wave-01-scaffold/02-init-state", "wave-02-build/01-compile"],
            result.Plan.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void WavedExample_ValidatesClean()
    {
        // FakeExecutableProbe.All so interpreter resolution is deterministic across OSes (the .ps1 files
        // are shape only; validation probes interpreters, it does not run scripts).
        IReadOnlyList<Diagnostic> diags = new PlanValidator(FakeExecutableProbe.All).Validate(Load().Plan!);
        Assert.Empty(diags);
    }

    [Fact]
    public void WavedExample_EachWaveHasAStableDefinitionHash()
    {
        PlanDefinition plan = Load().Plan!;
        foreach (WaveNode wave in plan.Waves)
        {
            string hash = WaveDefinitionHash.Compute(wave);
            Assert.StartsWith("sha256:", hash);
            Assert.Equal(hash, WaveDefinitionHash.Compute(wave)); // deterministic
        }
    }
}
