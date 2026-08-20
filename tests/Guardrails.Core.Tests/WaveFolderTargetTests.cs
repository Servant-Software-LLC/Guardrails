using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// Wave-TARGET resolution and the <c>GR1010</c> dead-end fix (issue #472). A wave folder holds
/// <c>preflights/</c> + <c>guardrails/</c> + <c>tasks/</c> but no <c>guardrails.json</c> BY DESIGN
/// (SSOT §14.1 — one shared run config), so loading one as a plan could only ever produce a bare
/// <c>GR1001</c>: the measured dead end that made the <c>/guardrails-review</c> skill's documented
/// per-wave stamp flow unexecutable.
///
/// <para>Resolution is deliberately narrow — the target must be an immediate child of a folder holding
/// <c>guardrails.json</c> AND match the already-load-bearing <c>^wave-([0-9]+)-[a-z0-9-]+$</c> — so no
/// new path-shape inference surface is created (design 20 §8.2).</para>
/// </summary>
public sealed class WaveFolderTargetTests
{
    [Fact]
    public void AWaveFolderResolvesToItsParentPlan()
    {
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        Assert.True(WaveFolder.TryResolveWaveTarget(
            Path.Combine(builder.PlanDir, "wave-01-scaffold"), out string planRoot, out string waveDir));
        Assert.Equal(Path.GetFullPath(builder.PlanDir), planRoot);
        Assert.Equal("wave-01-scaffold", waveDir);
    }

    [Fact]
    public void APlanFolderIsNotAWaveTarget()
    {
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        Assert.False(WaveFolder.TryResolveWaveTarget(builder.PlanDir, out _, out _));
    }

    [Fact]
    public void ADirectoryCarryingItsOwnConfigIsAlwaysAPlan_NeverAWave()
    {
        // A plan nested inside another plan keeps behaving exactly as it always has: the config wins.
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");
        File.WriteAllText(
            Path.Combine(builder.PlanDir, "wave-01-scaffold", "guardrails.json"), """{ "version": 1 }""");

        Assert.False(WaveFolder.TryResolveWaveTarget(
            Path.Combine(builder.PlanDir, "wave-01-scaffold"), out _, out _));
    }

    [Theory]
    [InlineData("wave-scaffold")]      // no number — the GR2033 typo shape
    [InlineData("waves-01-scaffold")]
    [InlineData("tasks")]
    public void ANonConformingSiblingIsNotAWaveTarget(string name)
    {
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init").RootDir(name);

        Assert.False(WaveFolder.TryResolveWaveTarget(Path.Combine(builder.PlanDir, name), out _, out _));
    }

    [Fact]
    public void ADeeperDescendantIsNotAWaveTarget()
    {
        // The walk is ONE level on purpose: a task folder inside a wave must keep failing as it does
        // today rather than being silently re-pointed at some ancestor plan.
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        Assert.False(WaveFolder.TryResolveWaveTarget(
            Path.Combine(builder.PlanDir, "wave-01-scaffold", "tasks", "01-init"), out _, out _));
    }

    [Fact]
    public void LoadingAWaveFolderAsAPlan_EmitsGR1010_NamingTheParentPlan()
    {
        using var builder = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");
        string waveDir = Path.Combine(builder.PlanDir, "wave-01-scaffold");

        PlanLoadResult result = new PlanLoader().Load(waveDir);

        Assert.True(result.HasErrors);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.WaveFolderIsNotALoadablePlan, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        // The remedy must be reachable from the message alone — that is the whole point of not leaving a
        // bare GR1001 here.
        Assert.Contains(Path.GetFullPath(builder.PlanDir), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("guardrails validate", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("mark-reviewed", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryFolderWithNoConfig_StillEmitsGR1001()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-noconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PlanLoadResult result = new PlanLoader().Load(dir);

            Assert.Equal(DiagnosticCodes.MissingFile, Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
