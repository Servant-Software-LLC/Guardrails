// INVALID sample for 03-tests-do-not-pin-deleted-members.ps1 — expect exit 1.
// IDENTICAL to the .valid sample except for ONE defect: the last row was carried over from the
// shipped file and still calls OverwatchSupplyAutoResolve.Resolve(...), a method task 05 DELETES
// and whose writeScope cannot reach this file. Every required clause is still satisfied — Certify
// is called, GateThreshold.Effective is called, the OverwatchSupply trait is present — so a
// non-zero exit here can only come from the forbidden-present clause, which is the half that has to
// stay anchored on the CALL rather than the bare word 'AutoResolve'.
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §3.1 — the deterministic certification gate. A prompt may propose; only this may
/// certify. One row per refusal token, plus the certified row.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchSupplyAutoResolveTests
{
    private const string Path = "vendor/resource.js";
    private const string SourceSha = "1111111111111111111111111111111111111111";

    [Fact]
    public void Certify_RefusesWhenTheDialIsNotCritical()
    {
        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, Dial(EscalationThreshold.High),
            [Candidate()], Proposal());

        Assert.False(result.Certified);
        Assert.Equal("dial-not-critical", result.Reason);
    }

    [Fact]
    public void Certify_RefusesWhenThePerGateNeedsHumanThresholdIsBelowCritical()
    {
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
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.High,
            GateThresholds = new GateThresholds { NeedsHuman = EscalationThreshold.Critical }
        };

        SupplyCertification result = OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, autonomy, [Candidate()], Proposal());

        Assert.True(result.Certified);
    }

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

        Assert.Equal("proceed-unreviewed", result.Reason);
    }

    [Fact]
    public void Certify_RefusesADoomedProposal()
    {
        SupplyCertification result = Run(Proposal(classification: OverwatchClassification.Doomed));

        Assert.Equal("doomed", result.Reason);
    }

    [Fact]
    public void Certify_RefusesAProposalWithNoResourceSupplyOp()
    {
        SupplyCertification result = Run(new OverwatchProposal
        {
            Classification = OverwatchClassification.Retryable,
            Diagnosis = "no fix offered",
            Fixes = []
        });

        Assert.Equal("no-resource-supply-op", result.Reason);
    }

    [Fact]
    public void Certify_RefusesAPathThatIsNotACandidate()
    {
        SupplyCertification result = Run(Proposal("vendor/other.js"));

        Assert.Equal("not-a-candidate", result.Reason);
    }

    [Fact]
    public void Certify_RefusesADuplicatePath()
    {
        SupplyCertification result = Run(Proposal(Path, Path));

        Assert.Equal("duplicate-path", result.Reason);
    }

    [Fact]
    public void Certify_NormalizesALeadingDotSlashBeforeMatchingACandidate()
    {
        SupplyCertification result = Run(Proposal("./" + Path));

        Assert.True(result.Certified);
    }

    [Fact]
    public void Certify_CertifiesEveryProposedOpWithItsCandidateSourceSha()
    {
        SupplyCertification result = Run(Proposal());

        Assert.True(result.Certified);
        CertifiedSupply supply = Assert.Single(result.Supplies);
        Assert.Equal(Path, supply.Path);
        Assert.Equal(SourceSha, supply.SourceCommit);
    }

    // ── the shared effective-threshold rule (§2.1) ─────────────────────────────────────────────
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
    public void GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent() =>
        Assert.Equal(EscalationThreshold.High, GateThreshold.Effective(null, CriticalityGate.NeedsHuman));

    // THE ONE DEFECT: carried over from the shipped file. Task 05 deletes Resolve and cannot edit
    // this file to follow, so this line becomes a compile error in a file nothing in scope can fix.
    [Fact]
    public void BelowCritical_ProposesTheCommandSequence_AndDoesNotRunIt()
    {
        OverwatchDecision decision = OverwatchSupplyAutoResolve.Resolve(
            AutonomyPolicy.Halt, autonomyBlockPresent: false, null, null!, null!, null!, null!);

        Assert.NotNull(decision.RichHaltSummary);
    }

    private static SupplyCertification Run(OverwatchProposal proposal) =>
        OverwatchSupplyAutoResolve.Certify(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, Dial(EscalationThreshold.Critical),
            [Candidate()], proposal);

    private static AutonomyConfig Dial(EscalationThreshold threshold) =>
        new() { EscalationThreshold = threshold };

    private static MissingResourceCandidate Candidate() =>
        new() { Path = Path, SourceCommit = SourceSha };

    private static OverwatchProposal Proposal(
        params string[] paths) => Proposal(OverwatchClassification.Retryable, paths);

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
