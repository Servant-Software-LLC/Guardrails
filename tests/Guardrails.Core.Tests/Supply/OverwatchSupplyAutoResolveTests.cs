using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §3.1 — <see cref="OverwatchSupplyAutoResolve.Certify"/>, the deterministic certification gate
/// for a missing-resource auto-resolve: a PURE function (no git, no journal, no filesystem) that either
/// certifies every proposed <c>resource-supply</c> op, each paired with its candidate's source sha, or
/// refuses with one reason token. One row per refusal token, checked in §3.1's order, plus the certified
/// row and its normalization/multi-op variants.
///
/// <para>
/// Also pinned here — because <see cref="OverwatchSupplyAutoResolve.Certify"/> is its first consumer, and it
/// has no test class of its own — is the shared effective-threshold rule, <see cref="GateThreshold.Effective"/>
/// (design 41 §2.1): a per-gate <c>gateThresholds</c> override when present, else the run-wide
/// <c>escalationThreshold</c>.
/// </para>
///
/// <para>
/// TDD red: <see cref="OverwatchSupplyAutoResolve.Certify"/> and <see cref="GateThreshold.Effective"/> both
/// currently throw <see cref="NotImplementedException"/> unconditionally, so every test below is expected to
/// FAIL against this tree. Do not add <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that
/// would make these pass against the stub, which defeats the point of pinning them red.
/// </para>
///
/// <para>
/// This file previously certified a premise no production path reaches: an already-staged file, drained onto
/// <c>plan.Workspace</c>. Design 41 §3.4 and issue #712 establish that premise is the defect, not the
/// evidence — the superseded staged-file machinery this file used to pin is deleted from the source in a
/// later task, so none of its members are named here.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchSupplyAutoResolveTests
{
    private const string CandidatePath = "vendor/resource.js";
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondCandidatePath = "vendor/second.js";
    private const string SecondCandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static MissingResourceCandidate Candidate(string path = CandidatePath, string sha = CandidateSha) =>
        new() { Path = path, SourceCommit = sha };

    private static OverwatchProposal ResourceSupplyProposal(params string[] paths) => new()
    {
        Classification = OverwatchClassification.Retryable,
        Diagnosis = "The candidate resolves the halted task's missing-resource question.",
        Fixes = paths
            .Select(p => new OverwatchFixOp { Kind = OverwatchFixKind.ResourceSupply, TargetPath = p })
            .ToList()
    };

    private static AutonomyConfig Autonomy(
        EscalationThreshold threshold = EscalationThreshold.Critical,
        EscalationThreshold? needsHumanOverride = null,
        ReviewGateDecision? reviewGate = null) => new()
    {
        EscalationThreshold = threshold,
        GateThresholds = needsHumanOverride is null && reviewGate is null
            ? null
            : new GateThresholds { NeedsHuman = needsHumanOverride, ReviewGate = reviewGate }
    };

    // ── §3.1 check 1: the dial itself ───────────────────────────────────────────────────────────────

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
        AutonomyConfig? autonomy = autonomyBlockPresent ? Autonomy(threshold) : null;

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            policy, autonomyBlockPresent, autonomy, [Candidate()], ResourceSupplyProposal(CandidatePath));

        Assert.False(result.Certified);
        Assert.Equal("dial-not-critical", result.Reason);
        Assert.Empty(result.Supplies);
    }

    [Fact]
    public void Certify_RefusesWhenThePerGateNeedsHumanThresholdIsBelowCritical()
    {
        // The operator asked for caution at exactly this gate: a run-wide `critical` with a per-gate
        // `needs-human: high` override must NOT supply — the gate owns the rule, so it cannot be skipped.
        AutonomyConfig autonomy = Autonomy(EscalationThreshold.Critical, needsHumanOverride: EscalationThreshold.High);

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal(CandidatePath));

        Assert.False(result.Certified);
        Assert.Equal("dial-not-critical", result.Reason);
        Assert.Empty(result.Supplies);
    }

    [Fact]
    public void Certify_UsesThePerGateNeedsHumanThreshold_WhenItRaisesToCritical()
    {
        // The other direction: a run-wide `high` with a per-gate `needs-human: critical` override DOES
        // supply — proving GateThreshold.Effective is actually consulted, not escalationThreshold directly.
        AutonomyConfig autonomy = Autonomy(EscalationThreshold.High, needsHumanOverride: EscalationThreshold.Critical);

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal(CandidatePath));

        Assert.True(result.Certified);
        Assert.Null(result.Reason);
        CertifiedSupply supply = Assert.Single(result.Supplies);
        Assert.Equal(CandidatePath, supply.Path);
        Assert.Equal(CandidateSha, supply.SourceCommit);
    }

    // ── §3.1 check 2: the review gate ───────────────────────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesWhenTheReviewGateIsProceedUnreviewed()
    {
        AutonomyConfig autonomy = Autonomy(EscalationThreshold.Critical, reviewGate: ReviewGateDecision.ProceedUnreviewed);

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal(CandidatePath));

        Assert.False(result.Certified);
        Assert.Equal("proceed-unreviewed", result.Reason);
        Assert.Empty(result.Supplies);
    }

    // ── §3.1 check 3: the proposal's classification ─────────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesADoomedProposal()
    {
        AutonomyConfig autonomy = Autonomy();
        OverwatchProposal proposal = new()
        {
            Classification = OverwatchClassification.Doomed,
            Diagnosis = "This is not a missing-resource question at all.",
            Fixes = []
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], proposal);

        Assert.False(result.Certified);
        Assert.Equal("doomed", result.Reason);
        Assert.Empty(result.Supplies);
    }

    // ── §3.1 check 4: at least one resource-supply op ───────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesAProposalWithNoResourceSupplyOp()
    {
        AutonomyConfig autonomy = Autonomy();
        OverwatchProposal proposal = new()
        {
            Classification = OverwatchClassification.Retryable,
            Diagnosis = "Grant a larger turn budget instead.",
            Fixes = [new OverwatchFixOp { Kind = OverwatchFixKind.BudgetOverride, BudgetField = "maxTurns", BudgetValue = 40 }]
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], proposal);

        Assert.False(result.Certified);
        Assert.Equal("no-resource-supply-op", result.Reason);
        Assert.Empty(result.Supplies);
    }

    // ── §3.1 check 5: every proposed path is a candidate ────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesAPathThatIsNotACandidate()
    {
        // The model cannot introduce a path the facts did not establish.
        AutonomyConfig autonomy = Autonomy();

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal("vendor/not-a-candidate.js"));

        Assert.False(result.Certified);
        Assert.Equal("not-a-candidate", result.Reason);
        Assert.Empty(result.Supplies);
    }

    [Fact]
    public void Certify_NormalizesALeadingDotSlashBeforeMatchingACandidate()
    {
        // Without normalization the agent's own documented root-file spelling would be refused as
        // not-a-candidate.
        AutonomyConfig autonomy = Autonomy();

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal("./" + CandidatePath));

        Assert.True(result.Certified);
        CertifiedSupply supply = Assert.Single(result.Supplies);
        Assert.Equal(CandidatePath, supply.Path);
        Assert.Equal(CandidateSha, supply.SourceCommit);
    }

    // ── §3.1 check 6: no path proposed twice ────────────────────────────────────────────────────────

    [Fact]
    public void Certify_RefusesADuplicatePath()
    {
        AutonomyConfig autonomy = Autonomy();

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()],
            ResourceSupplyProposal(CandidatePath, CandidatePath));

        Assert.False(result.Certified);
        Assert.Equal("duplicate-path", result.Reason);
        Assert.Empty(result.Supplies);
    }

    // ── the certified row ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Certify_CertifiesEveryProposedOpWithItsCandidateSourceSha()
    {
        // A proposal cannot contribute a sha, and a gate that carried one from the proposal would be
        // certifying the model's claim about where bytes came from — assert the CANDIDATE's sha survives.
        AutonomyConfig autonomy = Autonomy();
        List<MissingResourceCandidate> candidates =
        [
            Candidate(),
            Candidate(SecondCandidatePath, SecondCandidateSha)
        ];

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, candidates,
            ResourceSupplyProposal(CandidatePath, SecondCandidatePath));

        Assert.True(result.Certified);
        Assert.Null(result.Reason);
        Assert.Equal(2, result.Supplies.Count);
        Assert.Contains(result.Supplies, s => s.Path == CandidatePath && s.SourceCommit == CandidateSha);
        Assert.Contains(result.Supplies, s => s.Path == SecondCandidatePath && s.SourceCommit == SecondCandidateSha);
    }

    // ── the shared rule (design 41 §2.1): GateThreshold.Effective ───────────────────────────────────

    [Fact]
    public void GateThresholdEffective_FallsBackToTheRunWideDial_WhenNoPerGateOverride()
    {
        AutonomyConfig autonomy = new() { EscalationThreshold = EscalationThreshold.Moderate };

        EscalationThreshold effective = GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman);

        Assert.Equal(EscalationThreshold.Moderate, effective);
    }

    [Fact]
    public void GateThresholdEffective_PrefersThePerGateOverride()
    {
        AutonomyConfig autonomy = new()
        {
            EscalationThreshold = EscalationThreshold.Moderate,
            GateThresholds = new GateThresholds { NeedsHuman = EscalationThreshold.Critical }
        };

        EscalationThreshold effective = GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman);

        Assert.Equal(EscalationThreshold.Critical, effective);
    }

    [Fact]
    public void GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent()
    {
        // A null block must resolve to the documented default (High), never to Critical — returning
        // Critical for "no configuration at all" would open this gate on every unconfigured run.
        EscalationThreshold effective = GateThreshold.Effective(null, CriticalityGate.NeedsHuman);

        Assert.Equal(EscalationThreshold.High, effective);
    }
}
