using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Nested-layout detection + waved loader/validator (SSOT §14.1/§14.2): GR2032 (mixed layout),
/// GR2033 (wave numbering — duplicate/non-conforming = error, gap = warning), GR2034 (cross-wave
/// dependsOn), intra-wave dependsOn qualification, and the GR2022 wave-aware state-read branch. A flat
/// plan must be entirely unaffected.
/// </summary>
public sealed class WaveLoaderValidatorTests
{
    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    private static string Dump(IEnumerable<Diagnostic> diags) =>
        string.Join("\n", diags.Select(d => $"{d.Code} {d.Severity}: {d.Message}"));

    // --- detection + ordering ---------------------------------------------------------

    [Fact]
    public void WavedPlan_IsDetected_TasksFlattenedInStrictWaveOrder()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-02-build", "01-compile")
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-01-scaffold", "02-config");

        PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, Dump(result.Diagnostics));

        PlanDefinition p = result.Plan!;
        Assert.True(p.IsWaved);
        Assert.Equal(["wave-01-scaffold", "wave-02-build"], p.Waves.Select(w => w.Dir));
        Assert.Equal([1, 2], p.Waves.Select(w => w.Number));
        // Flattened in strict wave order, then task-folder order within a wave.
        Assert.Equal(
            ["wave-01-scaffold/01-init", "wave-01-scaffold/02-config", "wave-02-build/01-compile"],
            p.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void FlatPlan_Unchanged_NotWaved()
    {
        PlanLoadResult result = new PlanLoader().Load(TestPaths.Fixture("valid-minimal"));
        Assert.False(result.HasErrors, Dump(result.Diagnostics));
        Assert.False(result.Plan!.IsWaved);
        Assert.Empty(result.Plan.Waves);
    }

    // --- GR2032 mixed layout ----------------------------------------------------------

    [Fact]
    public void MixedLayout_RootTasksAndWaveDirs_ReportsGR2032()
    {
        using var plan = new WavePlanBuilder()
            .FlatTask("01-flat")
            .Task("wave-01-scaffold", "01-init");

        PlanLoadResult result = plan.Load();
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MixedWaveLayout);
        Assert.True(result.HasErrors);
    }

    // --- GR2033 numbering -------------------------------------------------------------

    [Fact]
    public void DuplicateWaveNumber_ReportsGR2033Error()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-alpha", "01-a")
            .Task("wave-01-beta", "01-b");

        PlanLoadResult result = plan.Load();
        Assert.Contains(result.Diagnostics,
            d => d.Code == DiagnosticCodes.WaveNumbering && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NonConformingSubdirAlongsideWaves_ReportsGR2033Error()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .RootDir("helpers"); // not wave-conforming, not a known plan-root folder

        PlanLoadResult result = plan.Load();
        Assert.Contains(result.Diagnostics,
            d => d.Code == DiagnosticCodes.WaveNumbering && d.Severity == DiagnosticSeverity.Error
                 && d.Message.Contains("helpers"));
    }

    [Fact]
    public void KnownPlanRootFoldersAlongsideWaves_AreNotFlagged()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .RootDir("state")
            .RootDir("logs");

        PlanLoadResult result = plan.Load();
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.WaveNumbering);
    }

    [Fact]
    public void WaveNumberingGap_IsAWarning_NotAnError_AndStillLoads()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-03-ship", "01-release"); // gap: no wave-02

        PlanLoadResult result = plan.Load();

        Assert.Contains(result.Diagnostics,
            d => d.Code == DiagnosticCodes.WaveNumbering && d.Severity == DiagnosticSeverity.Warning);
        Assert.False(result.HasErrors, Dump(result.Diagnostics));
        Assert.Equal(["wave-01-scaffold", "wave-03-ship"], result.Plan!.Waves.Select(w => w.Dir));
    }

    // --- dependsOn qualification + GR2034 ---------------------------------------------

    [Fact]
    public void IntraWaveDependsOn_IsQualifiedToTheWave()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-01-scaffold", "02-consume", dependsOn: ["01-produce"]);

        PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, Dump(result.Diagnostics));

        TaskNode consumer = result.Plan!.Tasks.Single(t => t.Id == "wave-01-scaffold/02-consume");
        Assert.Equal(["wave-01-scaffold/01-produce"], consumer.DependsOn);
    }

    [Fact]
    public void WaveQualifiedCrossWaveDependsOn_ReportsGR2034()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-02-build", "01-consume", dependsOn: ["wave-01-scaffold/01-produce"]);

        PlanLoadResult result = plan.Load();
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.CrossWaveDependency);
    }

    [Fact]
    public void PlainNameResolvingToAnotherWave_ReportsGR2034()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-02-build", "02-consume", dependsOn: ["01-produce"]); // not a sibling; lives in wave-01

        PlanLoadResult result = plan.Load();
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.CrossWaveDependency);
        // The dropped cross-wave edge must NOT also produce a spurious GR2001 unknown-dependency.
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.UnknownDependency);
    }

    [Fact]
    public void UnknownIntraWaveDependsOn_StillReportsGR2001()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce", dependsOn: ["99-nope"]);

        // Unknown-dependency is a validator (semantic) check; the loader qualifies the phantom edge to
        // this wave so GR2001 fires normally.
        PlanLoadResult result = plan.Load();
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.CrossWaveDependency);
        Assert.Contains(Validate(result.Plan!), d => d.Code == DiagnosticCodes.UnknownDependency);
    }

    // --- GR2022 wave-aware state-read branch ------------------------------------------

    [Fact]
    public void EarlierWaveStateRead_IsSatisfiedByTheBarrier_NoGR2022()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-02-build", "01-consume",
                actionBody: "#!/bin/sh\necho \"$state['wave-01-scaffold/01-produce']\"\n");

        IReadOnlyList<Diagnostic> diags = Validate(plan.Load().Plan!);
        Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.CrossTaskStateReferenceWithoutDependency);
    }

    [Fact]
    public void LaterWaveStateRead_ReportsGR2022()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-early",
                actionBody: "#!/bin/sh\necho \"$state['wave-02-build/01-late']\"\n")
            .Task("wave-02-build", "01-late");

        IReadOnlyList<Diagnostic> diags = Validate(plan.Load().Plan!);
        Diagnostic d = Assert.Single(diags, x => x.Code == DiagnosticCodes.CrossTaskStateReferenceWithoutDependency);
        Assert.Contains("LATER wave", d.Message);
    }

    [Fact]
    public void SameWaveStateReadWithoutDependency_StillReportsGR2022()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-01-scaffold", "02-consume",
                actionBody: "#!/bin/sh\necho \"$state['wave-01-scaffold/01-produce']\"\n"); // no dependsOn edge

        IReadOnlyList<Diagnostic> diags = Validate(plan.Load().Plan!);
        Assert.Contains(diags, d => d.Code == DiagnosticCodes.CrossTaskStateReferenceWithoutDependency);
    }

    [Fact]
    public void SameWaveStateReadWithDependency_NoGR2022()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-produce")
            .Task("wave-01-scaffold", "02-consume", dependsOn: ["01-produce"],
                actionBody: "#!/bin/sh\necho \"$state['wave-01-scaffold/01-produce']\"\n");

        IReadOnlyList<Diagnostic> diags = Validate(plan.Load().Plan!);
        Assert.DoesNotContain(diags, d => d.Code == DiagnosticCodes.CrossTaskStateReferenceWithoutDependency);
    }
}
