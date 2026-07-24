using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Semantic VALIDATION of the criticality dial's OPTIONAL <c>autonomy</c> block in <c>guardrails.json</c>
/// (issue #361, doc 12 §3.4/§3.5/§5.2; decided §10 M). Two codes:
/// <list type="bullet">
/// <item><see cref="DiagnosticCodes.InvalidAutonomyDialValue"/> (GR2039) — an <c>escalationThreshold</c> or
///   a <c>gateThresholds</c> value that is not one of its recognised tokens.</item>
/// <item><see cref="DiagnosticCodes.IncompatibleAutonomyCompoundConfig"/> (GR2040) — the forbidden compound
///   config: <c>review-gate: proceed-unreviewed</c> AND a reachable <c>critical</c> end-state (run-wide OR
///   per-gate, Finding 3).</item>
/// </list>
/// Builds a tiny plan folder on disk (mirroring <see cref="AutonomyConfigTests"/> /
/// <see cref="AutonomyPolicyConfigTests"/>) and asserts on the diagnostics <c>guardrails validate</c> would
/// report — see <see cref="LoadAndValidate"/>.
///
/// TDD RED. Authored BEFORE the validator learns the <c>autonomy</c> block. The POSITIVE cases
/// (<c>*_EmitsGR2039</c> / <c>*_EmitsGR2040</c>) assert a code that NOTHING emits today, so they FAIL (red)
/// until the implement task (<c>05-implement-autonomy-validation</c>) wires the rules. The NEGATIVE cases
/// (<c>*_EmitsNeitherCode</c> / <c>*_EmitsNoGR2040</c>) assert ABSENCE — they pass today and are green guards
/// that must STAY green through implementation, pinning that a valid block (and the permitted
/// <c>proceed-unreviewed</c> at the cautious dials) is NOT over-flagged. The file must COMPILE (the GR2039/
/// GR2040 constants exist); the validation itself is NOT implemented here.
/// </summary>
public sealed class AutonomyValidatorTests : IDisposable
{
    private readonly string _root;

    public AutonomyValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-autonomy-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Write <paramref name="guardrailsJson"/> as the plan's config beside one minimal, valid task.</summary>
    private string PlanWith(string guardrailsJson)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);
        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");
        return _root;
    }

    /// <summary>
    /// Load + validate the plan and return EVERY diagnostic — the loader's PLUS the semantic validator's —
    /// exactly as <c>guardrails validate</c> surfaces them (mirrors <c>PlanProbe.LoadAndValidate</c>: the
    /// validator runs only when the load produced a model with no fatal errors). Asserting on this combined
    /// surface keys the matrix on the OBSERVABLE end-to-end behaviour rather than on which component the
    /// implement task homes each code in — GR2039 needs the raw value the parse discards (see
    /// <c>PlanLoader.MapAutonomy</c>, which falls an unrecognised value back to the default), while GR2040
    /// keys on the parsed end-state. <see cref="FakeExecutableProbe.All"/> resolves every interpreter so no
    /// interpreter diagnostics fire; the fixtures set <c>maxParallelism: 1</c> so the git-root / terminal-gate
    /// checks (GR2015 / GR2028, worktree-mode-only) stay silent.
    /// </summary>
    private IReadOnlyList<Diagnostic> LoadAndValidate(string guardrailsJson)
    {
        PlanLoadResult load = new PlanLoader().Load(PlanWith(guardrailsJson));

        var all = new List<Diagnostic>(load.Diagnostics);
        if (load.Plan is not null && !load.HasErrors)
        {
            all.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan));
        }

        return all;
    }

    private static string ConfigWithAutonomy(string autonomyBlock) =>
        $$"""
        {
          "version": 1,
          "maxParallelism": 1,
          "autonomy": {{autonomyBlock}}
        }
        """;

    // ── GR2039: an unrecognised escalationThreshold / gateThresholds value (doc 12 §3.4/§3.5) ────────────

    [Fact]
    public void InvalidEscalationThreshold_EmitsGR2039()
    {
        // "aggressive" is not one of low/moderate/high/critical.
        IReadOnlyList<Diagnostic> diagnostics =
            LoadAndValidate(ConfigWithAutonomy("""{ "escalationThreshold": "aggressive" }"""));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue && d.Severity == DiagnosticSeverity.Error);
        // Pin the wire value the contract promises.
        Assert.Equal("GR2039", DiagnosticCodes.InvalidAutonomyDialValue);
    }

    [Fact]
    public void InvalidNeedsHumanGateValue_EmitsGR2039()
    {
        // needs-human is a criticality level; "sometimes" is not one.
        IReadOnlyList<Diagnostic> diagnostics =
            LoadAndValidate(ConfigWithAutonomy("""{ "gateThresholds": { "needs-human": "sometimes" } }"""));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void InvalidWaveCheckpointGateValue_EmitsGR2039()
    {
        // wave-checkpoint is a criticality level; "urgent" is not one.
        IReadOnlyList<Diagnostic> diagnostics =
            LoadAndValidate(ConfigWithAutonomy("""{ "gateThresholds": { "wave-checkpoint": "urgent" } }"""));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void InvalidReviewGateValue_EmitsGR2039()
    {
        // review-gate is NOT a criticality level: only "escalate" / "proceed-unreviewed" are valid.
        IReadOnlyList<Diagnostic> diagnostics =
            LoadAndValidate(ConfigWithAutonomy("""{ "gateThresholds": { "review-gate": "auto-approve" } }"""));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue && d.Severity == DiagnosticSeverity.Error);
    }

    // ── GR2040 run-wide: proceed-unreviewed + escalationThreshold: critical (doc 12 §5.2, decided §10 M/A) ─

    [Fact]
    public void ProceedUnreviewedWithCriticalDial_EmitsGR2040()
    {
        // review-gate: proceed-unreviewed AND a run-wide critical dial — the reachable end-state best-guesses
        // a critical hard call under an unreviewed wave. A load-time error ("Guardrails with no guardrails").
        IReadOnlyList<Diagnostic> diagnostics = LoadAndValidate(ConfigWithAutonomy(
            """
            {
              "escalationThreshold": "critical",
              "gateThresholds": { "review-gate": "proceed-unreviewed" }
            }
            """));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.IncompatibleAutonomyCompoundConfig && d.Severity == DiagnosticSeverity.Error);
        // Pin the wire value the contract promises.
        Assert.Equal("GR2040", DiagnosticCodes.IncompatibleAutonomyCompoundConfig);
    }

    // ── GR2040 per-gate route-around (Finding 3): a per-gate critical cannot escape the gate ────────────

    [Fact]
    public void PerGateCriticalUnderProceedUnreviewed_StillEmitsGR2040()
    {
        // Finding 3: GR2040 keys on the REACHABLE END-STATE, so a per-gate needs-human: critical STILL trips
        // it even though the run-wide dial is only "high" — a per-gate override must not route around it.
        IReadOnlyList<Diagnostic> diagnostics = LoadAndValidate(ConfigWithAutonomy(
            """
            {
              "escalationThreshold": "high",
              "gateThresholds": { "needs-human": "critical", "review-gate": "proceed-unreviewed" }
            }
            """));

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.IncompatibleAutonomyCompoundConfig && d.Severity == DiagnosticSeverity.Error);
    }

    // ── Negatives: neither code fires for a well-formed / permitted block (green guards) ────────────────

    [Fact]
    public void ValidBlock_EmitsNeitherCode()
    {
        // A well-formed block: recognised dials, review-gate: escalate (the recommended --autonomous default).
        IReadOnlyList<Diagnostic> diagnostics = LoadAndValidate(ConfigWithAutonomy(
            """
            {
              "escalationThreshold": "high",
              "gateThresholds": { "needs-human": "moderate", "wave-checkpoint": "high", "review-gate": "escalate" }
            }
            """));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.IncompatibleAutonomyCompoundConfig);
    }

    [Fact]
    public void ProceedUnreviewedAtHighDial_EmitsNoGR2040()
    {
        // proceed-unreviewed is a valid named opt-in at the cautious / high dials (doc 12 §5.1): with
        // escalationThreshold: high and NO per-gate critical, there is no reachable critical hard call, so
        // GR2040 must NOT fire — and every value is recognised, so GR2039 must not fire either.
        IReadOnlyList<Diagnostic> diagnostics = LoadAndValidate(ConfigWithAutonomy(
            """
            {
              "escalationThreshold": "high",
              "gateThresholds": { "review-gate": "proceed-unreviewed" }
            }
            """));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.IncompatibleAutonomyCompoundConfig);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidAutonomyDialValue);
    }

    [Fact]
    public void CriticalDialWithEscalateReviewGate_EmitsNoGR2040()
    {
        // GR2040 keys on BOTH conjuncts: a critical dial with the review gate left at escalate (review is NOT
        // bypassed) is allowed. Proves the gate is not merely "dial == critical".
        IReadOnlyList<Diagnostic> diagnostics = LoadAndValidate(ConfigWithAutonomy(
            """
            {
              "escalationThreshold": "critical",
              "gateThresholds": { "review-gate": "escalate" }
            }
            """));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.IncompatibleAutonomyCompoundConfig);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
