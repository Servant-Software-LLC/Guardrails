using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §3.1 — the deterministic certification gate that REPLACES
/// <c>OverwatchSupplyAutoResolve.Resolve</c> (design 41 §3.4, issue #712): the old premise — an
/// already-staged file drained onto <c>plan.Workspace</c> — was the defect, not the evidence.
/// <see cref="OverwatchSupplyAutoResolve.Certify"/> is a PURE function (no git, no journal, no
/// filesystem): a prompt may PROPOSE a resource-supply fix; only this gate may CERTIFY it, against facts
/// the harness already established (<see cref="MissingResourceCandidate"/>, design 41 §2.2).
/// <para>
/// One row per refusal token, checked in the order §3.1 lists them, plus the certified row. For a row to
/// observe the refusal it is named for, every EARLIER check must already pass, so each row below sets up
/// a dial that is otherwise valid.
/// </para>
/// <para>
/// Also pinned here: the shared effective-threshold rule (design 41 §2.1, <see cref="GateThreshold"/>) —
/// it has no test class of its own, and <c>Certify</c> is its first consumer.
/// </para>
/// <para>
/// TDD red: <see cref="OverwatchSupplyAutoResolve.Certify"/> and <see cref="GateThreshold.Effective"/>
/// currently throw <see cref="NotImplementedException"/> unconditionally, so every row below is expected
/// to FAIL against this tree. Do not add <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers —
/// that would make these pass against the stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchSupplyAutoResolveTests
{
    private const string Path = "vendor/resource.js";
    private const string SourceSha = "1111111111111111111111111111111111111111";

    // ── check 1: policy == Auto, the block is present, and the effective threshold is Critical ─────

    [Theory]
    [InlineData(AutonomyPolicy.Halt, true, EscalationThreshold.Critical)]
    [InlineData(AutonomyPolicy.Prompt, true, EscalationThreshold.Critical)]
    [InlineData(AutonomyPolicy.Auto, false, EscalationThreshold.Critical)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.Low)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.Moderate)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.High)]
    public void Certify_RefusesWhenTheDialIsNotCritical(
        AutonomyPolicy policy, bool autonomyBlockPresent, EscalationThreshold threshold)
    {
        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            policy, autonomyBlockPresent, Dial(threshold), [Candidate()], Proposal());

        Assert.False(result.Certified);
        Assert.Equal("dial-not-critical", result.Reason);
    }

    [Fact]
    public void Certify_RefusesWhenThePerGateNeedsHumanThresholdIsBelowCritical()
    {
        // The operator asked for caution at exactly this gate; a gate that read `escalationThreshold`
        // directly would ignore that and supply anyway. The unit twin of the real-path proof's control C7.
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.Critical,
            GateThresholds = new GateThresholds { NeedsHuman = EscalationThreshold.High }
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], Proposal());

        Assert.False(result.Certified);
        Assert.Equal("dial-not-critical", result.Reason);
    }

    [Fact]
    public void Certify_UsesThePerGateNeedsHumanThreshold_WhenItRaisesToCritical()
    {
        // The other direction: proves GateThreshold.Effective is actually consulted rather than
        // escalationThreshold being read directly.
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.High,
            GateThresholds = new GateThresholds { NeedsHuman = EscalationThreshold.Critical }
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], Proposal());

        Assert.True(result.Certified);
    }

    // ── check 2: the review gate must not be proceed-unreviewed ──────────────────────────────────────

    [Fact]
    public void Certify_RefusesWhenTheReviewGateIsProceedUnreviewed()
    {
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.Critical,
            GateThresholds = new GateThresholds { ReviewGate = ReviewGateDecision.ProceedUnreviewed }
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], Proposal());

        Assert.False(result.Certified);
        Assert.Equal("proceed-unreviewed", result.Reason);
    }

    // ── check 3: a proposal must exist and be classified retryable ───────────────────────────────────

    [Fact]
    public void Certify_RefusesADoomedProposal()
    {
        SupplyCertification result = Run(Proposal(classification: OverwatchClassification.Doomed));

        Assert.False(result.Certified);
        Assert.Equal("doomed", result.Reason);
    }

    // ── check 4: at least one resource-supply op ─────────────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesAProposalWithNoResourceSupplyOp()
    {
        SupplyCertification result = Run(new OverwatchProposal
        {
            Classification = OverwatchClassification.Retryable,
            Diagnosis = "no fix offered",
            Fixes = []
        });

        Assert.False(result.Certified);
        Assert.Equal("no-resource-supply-op", result.Reason);
    }

    // ── check 5: every proposed path, after normalization, must be a candidate ──────────────────────

    [Fact]
    public void Certify_RefusesAPathThatIsNotACandidate()
    {
        // The model cannot introduce a path the facts did not establish.
        SupplyCertification result = Run(Proposal("vendor/other.js"));

        Assert.False(result.Certified);
        Assert.Equal("not-a-candidate", result.Reason);
    }

    // ── check 6: no path proposed twice ──────────────────────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesADuplicatePath()
    {
        SupplyCertification result = Run(Proposal(Path, Path));

        Assert.False(result.Certified);
        Assert.Equal("duplicate-path", result.Reason);
    }

    [Fact]
    public void Certify_NormalizesALeadingDotSlashBeforeMatchingACandidate()
    {
        // Without normalization the agent's own documented root-file spelling would be refused as
        // not-a-candidate.
        SupplyCertification result = Run(Proposal("./" + Path));

        Assert.True(result.Certified);
    }

    // ── the certified row ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Certify_CertifiesEveryProposedOpWithItsCandidateSourceSha()
    {
        // A proposal cannot contribute a sha, and a gate that carried one from the proposal would be
        // certifying the model's claim about where bytes came from.
        SupplyCertification result = Run(Proposal());

        Assert.True(result.Certified);
        CertifiedSupply supply = Assert.Single(result.Supplies);
        Assert.Equal(Path, supply.Path);
        Assert.Equal(SourceSha, supply.SourceCommit);
    }

    // ── the shared effective-threshold rule (§2.1) — Certify is its first consumer ──────────────────

    [Fact]
    public void GateThresholdEffective_FallsBackToTheRunWideDial_WhenNoPerGateOverride() =>
        Assert.Equal(
            EscalationThreshold.Critical,
            GateThreshold.Effective(Dial(EscalationThreshold.Critical), CriticalityGate.NeedsHuman));

    [Fact]
    public void GateThresholdEffective_PrefersThePerGateOverride()
    {
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.Critical,
            GateThresholds = new GateThresholds { NeedsHuman = EscalationThreshold.Low }
        };

        Assert.Equal(EscalationThreshold.Low, GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman));
    }

    [Fact]
    public void GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent()
    {
        // A null block resolves to the documented default (High), never to Critical — returning Critical
        // for "no configuration at all" would open this gate on every unconfigured run.
        Assert.Equal(EscalationThreshold.High, GateThreshold.Effective(null, CriticalityGate.NeedsHuman));
    }

    // ── shared fixtures ───────────────────────────────────────────────────────────────────────────────

    private static SupplyCertification Run(OverwatchProposal proposal) =>
        OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, Dial(EscalationThreshold.Critical),
            [Candidate()], proposal);

    private static AutonomyConfig Dial(EscalationThreshold threshold) =>
        new() { EscalationThreshold = threshold };

    private static MissingResourceCandidate Candidate() =>
        new() { Path = Path, SourceCommit = SourceSha };

    private static OverwatchProposal Proposal(params string[] paths) =>
        Proposal(OverwatchClassification.Retryable, paths);

    private static OverwatchProposal Proposal(
        OverwatchClassification classification = OverwatchClassification.Retryable,
        params string[] paths) =>
        new()
        {
            Classification = classification,
            Diagnosis = "the task cannot embed a runtime that is not on its base",
            Fixes = (paths.Length == 0 ? new[] { Path } : paths)
                .Select(p => new OverwatchFixOp { Kind = OverwatchFixKind.ResourceSupply, TargetPath = p })
                .ToList()
        };
}
