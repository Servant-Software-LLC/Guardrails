using System.Reflection;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// The two WARNING diagnostics design 39 §1c/§5 adds for per-wave delivery (issue #360's
/// incremental-delivery follow-on): <see cref="DiagnosticCodes.PostDeliveryWaveMissingEntryPreflight"/>
/// (GR2078 — a wave follows a delivery point with no entry preflight of its own) and
/// <see cref="DiagnosticCodes.DeliveringWaveMissingExitGate"/> (GR2079 — a wave sets <c>delivers: true</c>
/// but carries no exit gate, so it cannot deliver). Both are WARNINGS, deliberately: an ERROR would fail
/// plans that are correct-but-unguarded.
///
/// <para><b>Neither check is implemented yet</b> — this task only reserves the two codes and pins the
/// tests that prove them; task 04 wires the checks. The two FIRING rows below are true TDD red: on this
/// tree nothing emits either code, so <c>GR2078_FiresWhenAPostDeliveryWaveHasNoEntryPreflight</c> and
/// <c>GR2079_FiresWhenADeliveringWaveHasNoExitGate</c> fail until then. The remaining five rows are
/// DECLARED EXEMPT from the red census: a diagnostic that does not exist yet is silent and emits no exit
/// code, so every SILENT assertion — and the GR2077 reservation, which this task's own numbering
/// guardrail already enforces — passes by construction on this stub tree.</para>
/// </summary>
public sealed class WaveDeliveryDiagnosticsTests
{
    private const string DeliversTrueBrief = "---\ndelivers: true\n---\n\nintent\n";

    private static IReadOnlyList<Diagnostic> Validate(WavePlanBuilder builder)
    {
        PlanLoadResult result = builder.Load();
        Assert.NotNull(result.Plan);
        Assert.False(result.HasErrors, Dump(result.Diagnostics));

        return new PlanValidator(FakeExecutableProbe.All, BannedPatternRegistry.Load(), NullScriptSyntaxProbe.Instance)
            .Validate(result.Plan!);
    }

    private static string Dump(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.Code} {d.Severity}: {d.Message}"));

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2078_FiresWhenAPostDeliveryWaveHasNoEntryPreflight()
    {
        // wave-01 is a REAL delivery point: delivers:true AND an exit gate, so IsDeliveryPoint is true.
        // wave-02 follows it and declares no entry preflight of its own — exactly the shape GR2078 names.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief)
            .Task("wave-02-build", "01-compile");

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.PostDeliveryWaveMissingEntryPreflight);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2078_IsSilentWhenTheWaveHasOne()
    {
        // DECLARED EXEMPT from the red census: GR2078 is not emitted anywhere yet, so this is silent by
        // construction. Same delivery-point setup as the firing row, but wave-02 declares its own entry
        // preflight, so once GR2078 is wired it must stay silent here.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief)
            .Task("wave-02-build", "01-compile")
            .WavePreflight("wave-02-build", "01-entry.sh", "exit 0\n");

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PostDeliveryWaveMissingEntryPreflight);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2078_IsSilentForAWaveThatFollowsNoDelivery()
    {
        // DECLARED EXEMPT from the red census, and the LOAD-BEARING negative: GR2078 must not fire on
        // every wave, only on one that follows an ACTUAL delivery point. wave-01 sets delivers:true but
        // has no exit gate at all, so it never delivers (IsDeliveryPoint stays false) — a delivers:true
        // wave with no exit gate never delivers, so nothing follows it as a delivery. wave-02 still
        // declares no entry preflight, which is exactly what a wrong "fire on every wave boundary"
        // implementation would flag.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief)
            .Task("wave-02-build", "01-compile");

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PostDeliveryWaveMissingEntryPreflight);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2079_FiresWhenADeliveringWaveHasNoExitGate()
    {
        // wave-01 declares delivers:true and carries no exit gate at all — the exact shape GR2079 must
        // name (it reads the DECLARED Delivers flag, never IsDeliveryPoint).
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.DeliveringWaveMissingExitGate);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2079_IsSilentWhenTheWaveHasAnExitGate()
    {
        // DECLARED EXEMPT from the red census: GR2079 is not emitted anywhere yet, so this is silent by
        // construction. Same delivers:true wave as the firing row, but it also carries an exit gate — it
        // CAN deliver, so once wired the check must stay silent here.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief);

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.DeliveringWaveMissingExitGate);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void BothAreWarnings_AndDoNotMoveTheExitCode()
    {
        // DECLARED EXEMPT from the red census: neither code is emitted anywhere yet, so every assertion
        // here holds vacuously on this stub tree. Once task 04 wires the checks this same plan trips BOTH
        // at once — wave-02 follows wave-01's real delivery point with no entry preflight (GR2078) AND
        // itself declares delivers:true with no exit gate (GR2079) — and the point stands regardless of
        // when it starts holding non-vacuously: a warning that silently became an error would fail a plan
        // that is correct-but-unguarded.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WaveBrief("wave-01-scaffold", DeliversTrueBrief)
            .Task("wave-02-build", "01-compile")
            .WaveBrief("wave-02-build", DeliversTrueBrief);

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        IEnumerable<Diagnostic> underTest = diagnostics.Where(d =>
            d.Code is DiagnosticCodes.PostDeliveryWaveMissingEntryPreflight
                or DiagnosticCodes.DeliveringWaveMissingExitGate);

        Assert.All(underTest, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void GR2077_RemainsReservedAndUnallocated()
    {
        // DECLARED EXEMPT from the red census: this task's own third guardrail (the numbering check)
        // refuses a "GR2077" constant outright, so on every tree that passes it this reservation test is
        // green by construction. GR2077 is reserved BY NAME in a comment only (issue #587 check B,
        // UnownedRequiredChange — designed and declined), which is exactly why the pre-existing catalogue
        // tests cannot catch taking it: this is the one test that can.
        IEnumerable<string> declaredCodes = typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.DoesNotContain("GR2077", declaredCodes);
    }
}
