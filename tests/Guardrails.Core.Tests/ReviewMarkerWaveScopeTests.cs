using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Review;

namespace Guardrails.Core.Tests;

/// <summary>
/// The PER-WAVE review marker (SSOT §13 <em>Multi-wave plans</em>, issues #471/#472/#488). §13 has
/// specified this since #254 and the code never implemented it: <c>ReviewMarker</c> was plan-root only, so
/// one plan-level <see cref="PlanDefinitionHash"/>-keyed marker attested every wave at once.
///
/// <para>Because <see cref="PlanDefinitionHash"/> folds EVERY wave's <c>guardrails/**</c> and
/// <c>preflights/**</c> (§7.3 step 5, #386) and a JIT breakdown authors exactly those folders for wave N+1,
/// every SUCCESSFUL breakdown de-attested wave N — reviewed, stamped, run, green, unchanged (#488). The
/// load-bearing test here is <see cref="AuthoringADownstreamWave_LeavesTheUpstreamWavesMarkerValid"/>: it
/// asserts the upstream marker survives, and pins the mechanism (the plan hash really does move) so it
/// cannot pass vacuously.</para>
/// </summary>
public sealed class ReviewMarkerWaveScopeTests
{
    private const string PassingGate = "#!/bin/sh\nexit 0\n";
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-provision";

    private static PlanDefinition Load(WavePlanBuilder builder)
    {
        PlanLoadResult result = builder.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Plan!;
    }

    private static WaveNode Wave(PlanDefinition plan, string dir) =>
        plan.Waves.Single(w => string.Equals(w.Dir, dir, StringComparison.Ordinal));

    /// <summary>
    /// The targets a nudge is actually SURFACED for, named: the wave dir, or <c>&lt;plan&gt;</c> for a
    /// whole-plan evaluation — so a test that expects only wave lines fails loudly if a plan-level one
    /// creeps back in (that regression IS #488).
    /// </summary>
    private static List<string> WarnedTargets(PlanDefinition plan) =>
        ReviewMarker.EvaluateAll(plan).Where(e => e.ShouldWarn).Select(e => e.WaveDir ?? "<plan>").ToList();

    // ── #488 — the regression that proves the fix rather than the plumbing ──────────────────────────

    [Fact]
    public void AuthoringADownstreamWave_LeavesTheUpstreamWavesMarkerValid()
    {
        using var builder = new WavePlanBuilder()
            .Task(Wave1, "01-init")
            .WaveGuardrail(Wave1, "90-exit.sh", PassingGate)
            .WaveStub(Wave2);

        PlanDefinition before = Load(builder);

        // The reviewer's stamp: wave 1 only — what /guardrails-review does per wave (§13). The plan-level
        // marker is ALSO written, because that is what the pre-#488 flow did and it makes the old
        // behaviour visible in the same test.
        ReviewMarker.Write(before, DateTimeOffset.UtcNow, Wave(before, Wave1));
        ReviewMarker.Write(before, DateTimeOffset.UtcNow);
        string planHashAtReview = PlanDefinitionHash.Compute(before);

        // A SUCCESSFUL JIT breakdown of wave 2: its tasks and its own entry/exit gates. Nothing under
        // wave 1 is touched.
        builder.Task(Wave2, "01-provision")
               .WavePreflight(Wave2, "10-entry.sh", PassingGate)
               .WaveGuardrail(Wave2, "90-exit.sh", PassingGate);

        PlanDefinition after = Load(builder);

        // The mechanism is real: the plan hash DID move, so this test cannot pass vacuously...
        Assert.NotEqual(planHashAtReview, PlanDefinitionHash.Compute(after));
        // ...and the plan-level marker is now stale — the exact GR2025 #471/#488 measured.
        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(after).State);

        // THE ASSERTION: wave 1 — reviewed, stamped, and byte-for-byte unchanged — is STILL attested.
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(after, Wave(after, Wave1)).State);

        // And nothing is surfaced for wave 1. Only the freshly authored, never-reviewed wave 2 is nudged.
        Assert.Equal([Wave2], WarnedTargets(after));
    }

    [Fact]
    public void AWaveMarkerLivesInThatWavesStateFolder_AndKeysOnItsWaveDefinitionHash()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);
        WaveNode wave1 = Wave(plan, Wave1);

        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, wave1);

        string markerPath = Path.Combine(builder.PlanDir, Wave1, "state", "guardrails-review.json");
        Assert.True(File.Exists(markerPath), $"expected the per-wave marker at {markerPath}");
        Assert.False(File.Exists(Path.Combine(builder.PlanDir, "state", "guardrails-review.json")));

        ReviewMarker? marker = ReviewMarker.Read(wave1.Directory);
        Assert.NotNull(marker);
        // Keyed on the ALREADY-SHIPPED wave hash — not PlanDefinitionHash, and not a fourth hash (§8.3).
        Assert.Equal(WaveDefinitionHash.Compute(wave1), marker.PlanHash);
        Assert.NotEqual(PlanDefinitionHash.Compute(plan), marker.PlanHash);
    }

    [Fact]
    public void StampingWaveOne_LeavesWaveTwoUnstamped()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);

        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));

        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(plan, Wave(plan, Wave1)).State);
        // Over-attestation is the other half of #472: one stamp must never vouch for a wave nobody read.
        Assert.Equal(ReviewState.Missing, ReviewMarker.EvaluateWave(plan, Wave(plan, Wave2)).State);
        Assert.Equal([Wave2], WarnedTargets(plan));
    }

    [Fact]
    public void EditingOneWavesGuardrailBody_StalesOnlyThatWave()
    {
        using var builder = new WavePlanBuilder()
            .Task(Wave1, "01-init")
            .WaveGuardrail(Wave1, "90-exit.sh", PassingGate)
            .Task(Wave2, "01-provision");

        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave2));

        // The #260 case the marker exists for: a post-review weakening of a reviewed guardrail body.
        builder.EditWaveGuardrail(Wave1, "90-exit.sh", "#!/bin/sh\nexit 0 # was a real check\n");
        PlanDefinition edited = Load(builder);

        Assert.Equal(ReviewState.Stale, ReviewMarker.EvaluateWave(edited, Wave(edited, Wave1)).State);
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(edited, Wave(edited, Wave2)).State);
        Assert.Equal([Wave1], WarnedTargets(edited));
    }

    [Fact]
    public void EditingTheSharedConfig_StalesNoWave()
    {
        // Open Decision C, already honoured by WaveDefinitionHash: a config edit must not re-stale every
        // already-run upstream wave. Reusing the shipped wave hash inherits that property for free.
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave2));

        builder.EditConfig("""{ "version": 1, "maxParallelism": 1, "defaultRetries": 2 }""");
        PlanDefinition edited = Load(builder);

        Assert.Empty(WarnedTargets(edited));
    }

    // ── The accepted residual, pinned rather than hidden (design 20 §8.3) ───────────────────────────

    [Fact]
    public void EditingAWaveBrief_ReStalesThatWavesMarker_TheAcceptedResidual()
    {
        // WaveDefinitionHash folds brief.md (§14.10) while PlanDefinitionHash excludes it as breakdown
        // INPUT. So a brief edit after review re-stales that wave — ACCEPTED, because it is a HUMAN edit
        // inside the wave (the complaint in #471/#488 is staling from a MACHINE side effect) and it errs
        // toward under-attestation. FLIP CONDITION: if this becomes a routine source of GR2025 noise,
        // split a WaveReviewHash that omits the brief and pin both against each other here.
        using var builder = new WavePlanBuilder()
            .Task(Wave1, "01-init")
            .WaveBrief(Wave1, "# Wave 1\n\nAuthor the scaffold.\n");

        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(plan, Wave(plan, Wave1)).State);

        builder.WaveBrief(Wave1, "# Wave 1\n\nAuthor the scaffold AND the migration.\n");
        PlanDefinition edited = Load(builder);

        Assert.Equal(ReviewState.Stale, ReviewMarker.EvaluateWave(edited, Wave(edited, Wave1)).State);
    }

    // ── Back-compat: the plan-level marker as a fresh VOUCHER (design 20 §8.4) ──────────────────────

    [Fact]
    public void AFreshPlanLevelMarker_VouchesForEveryWave()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);

        // Today's corpus: a waved plan stamped only at plan level (the only thing that worked, #472).
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(plan, Wave(plan, Wave1)).State);
        Assert.Equal(ReviewState.Reviewed, ReviewMarker.EvaluateWave(plan, Wave(plan, Wave2)).State);
        Assert.Empty(WarnedTargets(plan));
    }

    [Fact]
    public void AStalePlanLevelMarker_VouchesForNothing()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);

        builder.Task(Wave2, "02-configure");
        PlanDefinition edited = Load(builder);

        Assert.Equal(ReviewState.Stale, ReviewMarker.Evaluate(edited).State);
        Assert.Equal(ReviewState.Missing, ReviewMarker.EvaluateWave(edited, Wave(edited, Wave1)).State);
        Assert.Equal(ReviewState.Missing, ReviewMarker.EvaluateWave(edited, Wave(edited, Wave2)).State);
    }

    [Fact]
    public void EditingThePlanRootGate_SurfacesWhileOnlyPlanLevelAttested_ButNotOnceWavesAreStamped()
    {
        // The named residual, pinned in BOTH directions so it cannot quietly widen. The plan-root
        // guardrails/preflights of a waved plan are folded by no wave hash.
        using var builder = new WavePlanBuilder()
            .Task(Wave1, "01-init")
            .PlanGuardrail("90-terminal.sh", PassingGate);

        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow);
        Assert.Empty(WarnedTargets(plan));

        // (a) Attested at plan level only: a shell edit DOES still surface — the plan marker stales and
        //     every wave falls through to its own (missing) marker.
        builder.PlanGuardrail("90-terminal.sh", "#!/bin/sh\nexit 0 # weakened\n");
        PlanDefinition afterShellEdit = Load(builder);
        Assert.Equal([Wave1], WarnedTargets(afterShellEdit));

        // (b) Once the wave carries its OWN marker, a further shell edit re-stales nothing. This is the
        //     residual. Flip condition: a PlanShellDefinitionHash-keyed plan-level marker for waved plans.
        ReviewMarker.Write(afterShellEdit, DateTimeOffset.UtcNow, Wave(afterShellEdit, Wave1));
        builder.PlanGuardrail("90-terminal.sh", "#!/bin/sh\nexit 0 # weakened again\n");
        Assert.Empty(WarnedTargets(Load(builder)));
    }

    // ── Surfacing rules ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnauthoredJitStub_IsNotNudged()
    {
        // A wave with no tasks and no gates has nothing a review could attest; nudging to review a wave
        // that does not exist yet is the wolf-cry this change exists to stop. It starts being surfaced the
        // moment the breakdown authors it.
        using var builder = new WavePlanBuilder()
            .Task(Wave1, "01-init")
            .WaveStub(Wave2)
            .WaveBrief(Wave2, "# Wave 2\n\nProvision the environment.\n");

        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));

        Assert.Empty(WarnedTargets(plan));

        builder.Task(Wave2, "01-provision");
        Assert.Equal([Wave2], WarnedTargets(Load(builder)));
    }

    [Fact]
    public void AWavedPlan_EmitsNoPlanLevelNudge()
    {
        // Emitting BOTH would re-introduce #488 verbatim: the plan-level line fires on every healthy JIT
        // run. Every surfaced evaluation on a waved plan is wave-scoped.
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);

        Assert.All(ReviewMarker.EvaluateAll(plan), e => Assert.NotNull(e.WaveDir));
    }

    [Fact]
    public void AFlatPlan_IsEvaluatedExactlyAsBefore()
    {
        using var builder = new WavePlanBuilder().FlatTask("01-init");
        PlanDefinition plan = Load(builder);

        IReadOnlyList<ReviewEvaluation> all = ReviewMarker.EvaluateAll(plan);

        ReviewEvaluation only = Assert.Single(all);
        Assert.Null(only.WaveDir);
        Assert.Equal(ReviewMarker.Evaluate(plan), only);
        Assert.Equal(ReviewState.Missing, only.State);
    }

    [Fact]
    public void AWaveNudge_NamesTheWaveAndTheWaveScopedRemedy()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);
        ReviewMarker.Write(plan, DateTimeOffset.UtcNow, Wave(plan, Wave1));

        string message = Assert.Single(
            PlanValidator.ReviewMarkerDiagnostics(plan, ReviewNudgeSurface.Validate)).Message;

        Assert.Contains(Wave2, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Wave1, message, StringComparison.Ordinal);
        // `mark-reviewed <plan>` would stamp the WHOLE plan and over-attest every other wave — the very
        // thing #472 forced on reviewers. The remedy must name the wave.
        Assert.Contains($"mark-reviewed <plan>/{Wave2}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachWaveNudge_IsLocatedAtThatWavesFolder()
    {
        using var builder = new WavePlanBuilder().Task(Wave1, "01-init").Task(Wave2, "01-provision");
        PlanDefinition plan = Load(builder);

        IReadOnlyList<Diagnostic> nudges =
            PlanValidator.ReviewMarkerDiagnostics(plan, ReviewNudgeSurface.Validate);

        Assert.Equal(2, nudges.Count);
        Assert.All(nudges, d => Assert.Equal(DiagnosticCodes.ReviewMarkerMissingOrStale, d.Code));
        Assert.All(nudges, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(Wave(plan, Wave1).Directory, nudges[0].Path);
        Assert.Equal(Wave(plan, Wave2).Directory, nudges[1].Path);
    }
}
