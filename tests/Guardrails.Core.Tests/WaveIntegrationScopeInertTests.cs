using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2059 (issue #459): a WAVE-ROOT guardrail declaring <c>scope:"integration"</c> is announcing an
/// intention the harness does not act on, and until #459's contract question is settled the author must
/// at least be TOLD. The per-union re-verify set is task guardrails + the plan-root
/// <c>&lt;plan&gt;/guardrails/</c> folder (SSOT §4.3, #451); a <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/</c>
/// file is the wave EXIT gate (§14.3) and never joins it.
/// <para>
/// The negative cases carry as much weight as the positive one. A lint that fires where the tag genuinely
/// works would train authors to ignore it, so every position that DOES honour <c>scope:"integration"</c> —
/// a task guardrail, the plan root, any flat plan — is pinned silent here, as is the untagged wave gate
/// that is the overwhelmingly common shape.
/// </para>
/// </summary>
public sealed class WaveIntegrationScopeInertTests
{
    private const string IntegrationSidecar = """{ "scope": "integration" }""";
    private const string LocalSidecar = """{ "scope": "local" }""";

    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All, BannedPatternRegistry.Load(), NullScriptSyntaxProbe.Instance)
            .Validate(plan);

    private static string Dump(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}"));

    private static IReadOnlyList<Diagnostic> Gr2059(WavePlanBuilder builder)
    {
        PlanLoadResult result = builder.Load();
        Assert.NotNull(result.Plan);
        Assert.False(result.HasErrors, Dump(result.Diagnostics));

        return Validate(result.Plan!)
            .Where(d => d.Code == DiagnosticCodes.WaveIntegrationScopeInert)
            .ToList();
    }

    [Fact]
    public void WaveRootGuardrail_TaggedIntegration_WarnsGR2059()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-union.sh", "exit 0\n", IntegrationSidecar);

        Diagnostic warning = Assert.Single(Gr2059(plan));

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);

        // The message has to do three jobs: say the tag is inert HERE, say what the file actually does
        // instead, and name the position where the tag would work. A bare "inert" leaves the author with
        // no move.
        Assert.Contains("INERT", warning.Message, StringComparison.Ordinal);
        Assert.Contains("wave-01-scaffold/guardrails/", warning.Message, StringComparison.Ordinal);
        Assert.Contains("<plan>/guardrails/", warning.Message, StringComparison.Ordinal);
        Assert.Contains("UNION-SAFE", warning.Message, StringComparison.Ordinal);
        Assert.Contains("#459", warning.Message, StringComparison.Ordinal);

        // A warning, never an error: the plan is not invalid, and #459 is explicit that the destination
        // is an architect call. Failing the build here would force authors to un-tag before the contract
        // question is even answered.
        Assert.False(plan.Load().HasErrors);
    }

    [Fact]
    public void EveryTaggedWaveGate_IsReported_NotJustTheFirst()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .Task("wave-02-build", "01-compile")
            .WaveGuardrail("wave-01-scaffold", "01-union.sh", "exit 0\n", IntegrationSidecar)
            .WaveGuardrail("wave-02-build", "01-union.sh", "exit 0\n", IntegrationSidecar);

        Assert.Equal(2, Gr2059(plan).Count);
    }

    [Fact]
    public void WaveRootGuardrail_Untagged_IsSilent()
    {
        // The common shape by a wide margin — a wave exit gate with no scope key at all. If GR2059 fired
        // here it would fire on essentially every waved plan ever written.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-gate.sh", "exit 0\n");

        Assert.Empty(Gr2059(plan));
    }

    [Fact]
    public void WaveRootGuardrail_TaggedLocal_IsSilent()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-gate.sh", "exit 0\n", LocalSidecar);

        Assert.Empty(Gr2059(plan));
    }

    [Fact]
    public void PlanRootGuardrail_TaggedIntegration_IsSilent()
    {
        // The plan root is precisely where #451 made the tag work. Warning here would contradict the
        // remedy GR2059's own message recommends.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .PlanGuardrail("01-union.sh", "exit 0\n", IntegrationSidecar);

        Assert.Empty(Gr2059(plan));
    }

    [Fact]
    public void TaskGuardrailInsideAWave_TaggedIntegration_IsSilent()
    {
        // A task guardrail is in the union set regardless of whether its plan is waved.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveTaskGuardrailSidecar("wave-01-scaffold", "01-init", IntegrationSidecar);

        Assert.Empty(Gr2059(plan));
    }

    [Fact]
    public void WaveRootPreflight_TaggedIntegration_IsSilent()
    {
        // Deliberate boundary, not an oversight: whether `scope` means anything in ANY preflights/ folder
        // is a separate and unfiled question. #459 is about the wave EXIT gate, and GR2059 stays there —
        // widening a lint past its filed evidence is how a validator starts crying wolf.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WavePreflight("wave-01-scaffold", "01-entry.sh", "exit 0\n", IntegrationSidecar);

        Assert.Empty(Gr2059(plan));
    }

    [Fact]
    public void FlatPlan_IsUnreachable()
    {
        // A flat plan has no wave-root folder to be wrong about; the check must not find a way to fire.
        PlanLoadResult result = new PlanLoader().Load(TestPaths.Fixture("valid-minimal"));
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsWaved);

        Assert.DoesNotContain(
            Validate(result.Plan),
            d => d.Code == DiagnosticCodes.WaveIntegrationScopeInert);
    }
}
