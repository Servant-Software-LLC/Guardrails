using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Config parsing of the criticality dial's OPTIONAL <c>autonomy</c> block in <c>guardrails.json</c> (issue
/// #361, doc 12 §3.3/§3.4/§3.5; decided values §10 F/G/I/N). A NEW ORTHOGONAL block that composes with — and
/// never redefines — the shipped <c>autonomyPolicy</c> (SSOT §2.1). Builds a tiny plan folder on disk,
/// mirroring <see cref="AutonomyPolicyConfigTests"/> / <see cref="CostCapConfigTests"/>.
///
/// TDD RED. These tests are authored BEFORE the real raw→<see cref="RunConfig.Autonomy"/> parse exists:
/// <list type="bullet">
/// <item><see cref="PopulatedBlock_ParsesEveryField_IntoNonNullAutonomy"/>,
///   <see cref="PresentBlock_OmittedFields_ApplyDecidedDefaults"/>, and
///   <see cref="Autonomy_ComposesWith_AutonomyPolicy_Independently"/> load a config that CONTAINS the block —
///   the stubbed <c>PlanLoader.MapAutonomy</c> throws <see cref="NotImplementedException"/> for a present
///   block, so these FAIL (red) until the implement task wires the parse.</item>
/// <item><see cref="AbsentBlock_IsInert_AndShiftsNoOtherField"/> and
///   <see cref="EscalationThreshold_OrdersLowToCritical"/> exercise ONLY the parts the stub genuinely
///   satisfies (the block-absent inert path and the ordered enum), so they PASS today — the inert case is the
///   load-bearing back-compat guarantee (doc 12 §3.2) and must stay green.</item>
/// </list>
/// The stubs must COMPILE (a non-compiling "test" is a mistake); the parse itself is NOT implemented here.
/// </summary>
public sealed class AutonomyConfigTests : IDisposable
{
    private readonly string _root;

    public AutonomyConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-autonomy-block-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

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

    // ── Populated block: every field maps (doc 12 §3.4/§3.5) ─────────────────────────────────────

    [Fact]
    public void PopulatedBlock_ParsesEveryField_IntoNonNullAutonomy()
    {
        // A fully-specified autonomy block must load into a non-null RunConfig.Autonomy with EVERY field
        // mapped. NON-default values (7/1200/4, and a mid gate override) are used deliberately so the test
        // proves the JSON is READ, not that the record's own defaults happen to line up.
        const string json =
            """
            {
              "version": 1,
              "autonomyPolicy": "auto",
              "autonomy": {
                "escalationThreshold": "high",
                "gateThresholds": {
                  "needs-human": "moderate",
                  "wave-checkpoint": "high",
                  "review-gate": "escalate"
                },
                "blockerRetry": { "maxAttempts": 7, "totalWaitSeconds": 1200 },
                "maxJudgeWidenings": 4
              }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        AutonomyConfig autonomy = result.Plan!.Config.Autonomy!;
        Assert.Equal(EscalationThreshold.High, autonomy.EscalationThreshold);

        GateThresholds gates = autonomy.GateThresholds!;
        Assert.Equal(EscalationThreshold.Moderate, gates.NeedsHuman);
        Assert.Equal(EscalationThreshold.High, gates.WaveCheckpoint);
        Assert.Equal(ReviewGateDecision.Escalate, gates.ReviewGate);

        Assert.Equal(7, autonomy.BlockerRetry.MaxAttempts);
        Assert.Equal(1200, autonomy.BlockerRetry.TotalWaitSeconds);

        Assert.Equal(4, autonomy.MaxJudgeWidenings);
    }

    // ── Defaults: a present-but-empty block resolves the decided values (doc 12 §3.4, §10 I/N) ────

    [Fact]
    public void PresentBlock_OmittedFields_ApplyDecidedDefaults()
    {
        // A present autonomy block with its fields OMITTED yields the decided defaults: escalationThreshold =
        // high (§10 N), blockerRetry = { maxAttempts: 5, totalWaitSeconds: 900 } (§10 I), maxJudgeWidenings = 3
        // (§10 I). The block is PRESENT, so the dial is not inert — these are real applied defaults.
        const string json =
            """
            {
              "version": 1,
              "autonomyPolicy": "auto",
              "autonomy": {}
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        AutonomyConfig autonomy = result.Plan!.Config.Autonomy!;
        Assert.Equal(EscalationThreshold.High, autonomy.EscalationThreshold);
        Assert.Equal(5, autonomy.BlockerRetry.MaxAttempts);
        Assert.Equal(900, autonomy.BlockerRetry.TotalWaitSeconds);
        Assert.Equal(3, autonomy.MaxJudgeWidenings);
    }

    // ── Ordered enum: low < moderate < high < critical (doc 12 §3.3, §10 F) ──────────────────────

    [Fact]
    public void EscalationThreshold_OrdersLowToCritical()
    {
        // The dial value is "the lowest criticality that still escalates" — a threshold compared with ≥ — so
        // the enum's natural order MUST be the criticality order. This exercises the DATA the stub already
        // defines, so it passes today and pins the ordering the parse will rely on.
        Assert.True(EscalationThreshold.Low < EscalationThreshold.Moderate);
        Assert.True(EscalationThreshold.Moderate < EscalationThreshold.High);
        Assert.True(EscalationThreshold.High < EscalationThreshold.Critical);
    }

    // ── Inert-by-default / no field shifted (doc 12 §3.2 — the back-compat guarantee) ────────────

    [Fact]
    public void AbsentBlock_IsInert_AndShiftsNoOtherField()
    {
        // REQUIRED (doc 12 §3.2). With NO autonomy block, RunConfig.Autonomy is null/inert AND every
        // pre-existing field keeps its documented default — the block-absent load is byte-identical to today.
        // This is the load-bearing back-compat guarantee; it must stay GREEN (the stub returns null, no throw).
        PlanLoadResult result = new PlanLoader().Load(PlanWith("""{ "version": 1 }"""));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        RunConfig config = result.Plan!.Config;

        // (a) the new axis is inert.
        Assert.Null(config.Autonomy);

        // (b) no pre-existing field shifted — the full documented default surface (RunConfig, SSOT §2).
        Assert.Equal(1, config.Version);
        Assert.Equal(3, config.MaxParallelism);
        Assert.Equal(2, config.DefaultRetries);
        Assert.Null(config.MaxCostUsd);
        Assert.Equal(1800, config.DefaultTimeoutSeconds);
        Assert.Equal(14400, config.TransientPauseBudgetSeconds);
        Assert.Equal(GuardrailMode.FailFast, config.GuardrailMode);
        Assert.Equal("..", config.Workspace);
        Assert.Null(config.WorktreeRoot);
        Assert.False(config.RunOnCurrentBranch);
        Assert.True(config.MergeOnSuccess);
        Assert.Null(config.MergeOnSuccessExplicit);
        Assert.False(config.TriageAutoFile);
        Assert.Equal(AutonomyPolicy.Prompt, config.AutonomyPolicy);
        Assert.True(config.PreserveAttemptsForSalvage);
        Assert.Empty(config.Interpreters);
        Assert.Empty(config.PromptRunnerNames);
        Assert.Null(config.DefaultPromptRunner);
        Assert.Empty(config.PromptRunners);
    }

    // ── Composition: the dial and autonomyPolicy are independent axes (doc 12 §3.1/§3.2, §10 G) ──

    [Fact]
    public void Autonomy_ComposesWith_AutonomyPolicy_Independently()
    {
        // The autonomy block is a NEW ORTHOGONAL axis: it composes with, and never redefines, the shipped
        // autonomyPolicy. Set BOTH from two distinct JSON keys and assert each surfaces on its OWN property
        // with its own value — proving separate storage, not one field reinterpreting the other.
        const string json =
            """
            {
              "version": 1,
              "autonomyPolicy": "auto",
              "autonomy": { "escalationThreshold": "moderate" }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(PlanWith(json));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        RunConfig config = result.Plan!.Config;
        Assert.Equal(AutonomyPolicy.Auto, config.AutonomyPolicy);            // the shipped field, unchanged parse
        Assert.Equal(EscalationThreshold.Moderate, config.Autonomy!.EscalationThreshold); // the new orthogonal dial
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
