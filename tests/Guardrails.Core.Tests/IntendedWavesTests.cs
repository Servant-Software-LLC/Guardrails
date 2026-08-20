using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// <c>intendedWaves</c> and GR2062 (issue #477, doc 19 §3.2/§3.3, SSOT §2/§14.1) — the ONE integer that lets
/// a plan folder be ASKED how many waves it was supposed to have.
///
/// <para>The measured incident these pin: a charter settled THREE waves, the wave-2 brief carried the #365
/// one-ahead step verbatim including its own warning, and the JIT breakdown that owed the wave-3 stub
/// TRUNCATED before reaching it. The hand-recovery restored the tasks and missed the stub, "because a stub
/// leaves no forward reference to trip over the way a task does". Then <c>validate</c> clean,
/// <c>graph --check</c> clean, two full review passes clean — and the run drained 20 tasks and $115.32,
/// whole suite passing, conformance 9/9, before failing at the terminal gate on a wave that was never
/// authored. Wave intent was recorded nowhere machine-readable, so nothing could have caught it.</para>
///
/// <para>The load-bearing test in this file is
/// <see cref="Gr2062_IsSilentWhileAWaveStubIsStillPending_TheOneAheadInvariantWorking"/>: without the
/// <c>planIsClosed</c> conjunct this warning would fire on every healthy JIT mid-plan state and be ignored
/// inside a week, which would cost more than the check is worth.</para>
/// </summary>
public sealed class IntendedWavesTests : IDisposable
{
    private readonly WavePlanBuilder _b = new();

    public void Dispose() => _b.Dispose();

    private void Intend(int? waves) =>
        _b.EditConfig(waves is { } n
            ? $$"""{ "version": 1, "maxParallelism": 1, "intendedWaves": {{n}} }"""
            : """{ "version": 1, "maxParallelism": 1 }""");

    private PlanDefinition Load()
    {
        PlanLoadResult result = _b.Load();
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    private IReadOnlyList<Diagnostic> Validate() =>
        new PlanValidator(FakeExecutableProbe.All).Validate(Load());

    private Diagnostic? Gr2062() =>
        Validate().SingleOrDefault(d => d.Code == DiagnosticCodes.IntendedWaveNotDeclared);

    // --- loading -----------------------------------------------------------------------------------

    [Fact]
    public void IntendedWaves_IsOptional_AndAnOmittedKeyStaysDistinguishableFromAnyRecordedCount()
    {
        _b.Task("wave-01-scaffold", "01-config");

        Intend(null);
        Assert.Null(Load().Config.IntendedWaves);

        Intend(3);
        Assert.Equal(3, Load().Config.IntendedWaves);
    }

    // --- GR2062: fires -----------------------------------------------------------------------------

    [Fact]
    public void Gr2062_WarnsWhenEveryDeclaredWaveIsAuthoredAndAWaveIsMissing_TheStage2Shape()
    {
        // Intends 3, declares 2, and BOTH are authored — the exact broken state that survived a clean
        // validate, a clean graph --check and two review passes.
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        Intend(3);

        Diagnostic d = Assert.IsType<Diagnostic>(Gr2062());
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("\"intendedWaves\": 3", d.Message);
        Assert.Contains("2 wave folder(s)", d.Message);
        Assert.Contains("GONE", d.Message);
    }

    [Fact]
    public void Gr2062_AlsoWarnsTheOtherPolarity_WhenThePlanGrewPastItsStatedIntent()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        _b.Task("wave-03-ship", "01-publish");
        Intend(2);

        Diagnostic d = Assert.IsType<Diagnostic>(Gr2062());
        Assert.Contains("grew past its stated intent", d.Message);
    }

    [Fact]
    public void Gr2062_WarnsOnAFlatPlanCarryingTheKey_WithFlatSpecificWording_NotAnArithmeticDeclaresZero()
    {
        // planIsClosed is trivially true with no waves, so both conjuncts hold. It can only fire where an
        // author explicitly wrote a waved-plans-only key into a plan that has no waves — worth saying.
        _b.FlatTask("01-config");
        Intend(2);

        Diagnostic d = Assert.IsType<Diagnostic>(Gr2062());
        Assert.Contains("FLAT plan", d.Message);
        Assert.DoesNotContain("declares 0", d.Message);
    }

    // --- GR2062: silent ----------------------------------------------------------------------------

    [Fact]
    public void Gr2062_IsSilentWhileAWaveStubIsStillPending_TheOneAheadInvariantWorking()
    {
        // Intends 3, declares 3, one of them an un-authored stub: planIsClosed is FALSE. A warning here
        // would fire on every healthy JIT mid-plan run and be ignored — which is the whole conjunct.
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        _b.WaveStub("wave-03-ship");
        Intend(3);

        Assert.Null(Gr2062());
    }

    [Fact]
    public void Gr2062_IsSilentWhenAStubIsPendingEvenThoughTheCountsDisagree()
    {
        // Intends 3, declares 2, and wave-02 is the pending stub — the "one-ahead pending" row of §3.2.
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub("wave-02-build");
        Intend(3);

        Assert.Null(Gr2062());
    }

    [Fact]
    public void Gr2062_IsSkippedEntirelyWhenIntendedWavesIsAbsent_NoPlanIsForcedToMigrate()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        Intend(null);

        Assert.Null(Gr2062());
    }

    [Fact]
    public void Gr2062_IsSilentWhenTheCountsAgree()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        Intend(2);

        Assert.Null(Gr2062());
    }

    [Fact]
    public void Gr2062_IsSilentOnAFlatPlanWithoutTheKey_TheOverwhelminglyCommonCase()
    {
        _b.FlatTask("01-config");
        Intend(null);

        Assert.Null(Gr2062());
    }

    // --- the reporting line (doc 19 §3.2 — "#477's explicit floor") --------------------------------

    [Fact]
    public void WaveIntentSummary_ReportsIntendedVersusDeclared_IncludingThroughTheSilentHealthyState()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.WaveStub("wave-02-build");
        Intend(3);

        // GR2062 is correctly silent here; the LINE is what keeps the shortfall visible rather than
        // letting a pending state read as agreement.
        Assert.Null(Gr2062());
        Assert.Equal("Waves: 3 intended, 2 declared (1 not yet created)", WaveIntentSummary.Describe(Load()));
    }

    [Fact]
    public void WaveIntentSummary_SaysSoWhenTheCountsAgree()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        Intend(2);

        Assert.Equal("Waves: 2 intended, 2 declared", WaveIntentSummary.Describe(Load()));
    }

    [Fact]
    public void WaveIntentSummary_SaysWhenTheDeclaredCountRunsAhead()
    {
        _b.Task("wave-01-scaffold", "01-config");
        _b.Task("wave-02-build", "01-compile");
        Intend(1);

        Assert.Equal("Waves: 1 intended, 2 declared (1 beyond the stated intent)",
            WaveIntentSummary.Describe(Load()));
    }

    [Fact]
    public void WaveIntentSummary_SaysIntentNotRecorded_RatherThanInventingAnIntendedCount()
    {
        _b.Task("wave-01-scaffold", "01-config");
        Intend(null);

        Assert.Equal("Waves: 1 declared (intent not recorded)", WaveIntentSummary.Describe(Load()));
    }

    [Fact]
    public void WaveIntentSummary_IsNullOnAFlatPlan_ThereAreNoWavesToReportOn()
    {
        _b.FlatTask("01-config");
        Intend(null);

        Assert.Null(WaveIntentSummary.Describe(Load()));
    }
}
