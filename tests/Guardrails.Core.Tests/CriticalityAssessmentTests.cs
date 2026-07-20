using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD (red) for the criticality-assessment decider (issue #361 Phase 3, doc 12 §3.2/§3.3, §4.3, §5.2). The
/// <see cref="CriticalityJudge"/> takes an injected <see cref="IPromptRunner"/> (a FAKE here — never a real
/// prompt call) + an <see cref="AutonomyConfig"/> + the gate context, runs its advisory assessment through
/// the reserved read-only <c>overwatch</c> profile (exactly as the overwatcher's diagnose does, §10 H), and
/// RETURNS a <see cref="CriticalityDecision"/> — escalate | proceed-best-guess, with criticality, confidence,
/// best-guess, rationale. It DECIDES only; it does not call the escalation sink or inject the best-guess.
///
/// These tests pin: the threshold-compare boundary (run-wide + per-gate override, §3.3/§3.5); malformed /
/// absent ⇒ escalate (invariant 1, §4.3); the <c>proceed-unreviewed</c> clamp (§5.2 / Blocker 1); and the
/// run-level <c>maxJudgeWidenings</c> cap on unknown-failure widening (§4.3). They fail against the throwing
/// stub in <see cref="CriticalityJudge.AssessAsync"/>.
/// </summary>
public sealed class CriticalityAssessmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- fakes & helpers -----------------------------------------------------------------------

    /// <summary>
    /// A FAKE <see cref="IPromptRunner"/> (the reserved <c>overwatch</c> profile): every call returns a canned
    /// <see cref="PromptResult"/> — NO real Claude process is ever spawned, so the decider's logic is
    /// unit-tested deterministically. <see cref="Invocations"/> proves the fake (not a real prompt) ran.
    /// </summary>
    private sealed class FakePromptRunner(string? resultText, bool completed = true, bool isError = false) : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new PromptResult
            {
                Completed = completed,
                IsError = isError,
                ResultText = resultText,
                Summary = "fake criticality assessment"
            });
        }
    }

    /// <summary>A canned judgment-call assessment body (the reserved profile's result text).</summary>
    private static string Assess(string criticality, string confidence = "moderate",
        string bestGuess = "use the conservative default", string rationale = "fake self-report")
        => $$"""{"criticality":"{{criticality}}","confidence":"{{confidence}}","bestGuess":"{{bestGuess}}","rationale":"{{rationale}}"}""";

    /// <summary>A canned unknown-failure widening body (§4.3): the judge's retryable/not read + rationale.</summary>
    private static string Widen(bool retryable, string rationale)
    {
        string r = retryable ? "true" : "false";
        return $$"""{"retryable":{{r}},"rationale":"{{rationale}}"}""";
    }

    private static CriticalityGateContext NeedsHuman(string detail = "Which JSON serializer should I use?")
        => new() { Gate = CriticalityGate.NeedsHuman, Detail = detail };

    private static CriticalityGateContext Unknown(string detail = "unrecognized failure")
        => new() { Gate = CriticalityGate.UnknownFailure, Detail = detail };

    private static AutonomyConfig Dial(EscalationThreshold threshold, GateThresholds? gates = null, int maxWidenings = 3)
        => new() { EscalationThreshold = threshold, GateThresholds = gates, MaxJudgeWidenings = maxWidenings };

    // ---- threshold compare — the boundary (§3.3) -----------------------------------------------

    /// <summary>
    /// The rule (§3.3): <c>escalate ⟺ assessedCriticality ≥ escalationThreshold</c>. At the run-wide dial
    /// <c>high</c>, an assessed <c>high</c> (AT threshold) escalates, one below (<c>moderate</c>) proceeds.
    /// </summary>
    [Theory]
    [InlineData("critical", CriticalityOutcome.Escalate)]
    [InlineData("high", CriticalityOutcome.Escalate)]            // AT threshold ⇒ escalate
    [InlineData("moderate", CriticalityOutcome.ProceedBestGuess)] // one below ⇒ proceed-best-guess
    [InlineData("low", CriticalityOutcome.ProceedBestGuess)]
    public async Task Escalates_iff_assessed_criticality_at_or_above_run_wide_threshold(
        string criticality, CriticalityOutcome expected)
    {
        var runner = new FakePromptRunner(Assess(criticality));
        var judge = new CriticalityJudge(runner, Dial(EscalationThreshold.High));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);

        Assert.Equal(expected, decision.Outcome);
        Assert.Equal(1, runner.Invocations); // the FAKE ran — deterministic, no real prompt call
    }

    /// <summary>A proceed-best-guess decision surfaces the assessed criticality, confidence, and best-guess (§6.2).</summary>
    [Fact]
    public async Task Proceed_best_guess_decision_carries_criticality_confidence_and_best_guess()
    {
        var runner = new FakePromptRunner(Assess("moderate", confidence: "high", bestGuess: "default to UTC"));
        var judge = new CriticalityJudge(runner, Dial(EscalationThreshold.High));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);

        Assert.Equal(CriticalityOutcome.ProceedBestGuess, decision.Outcome);
        Assert.Equal(EscalationThreshold.Moderate, decision.Criticality);
        Assert.Equal(JudgeConfidence.High, decision.Confidence);
        Assert.Equal("default to UTC", decision.BestGuess);
    }

    /// <summary>
    /// A per-gate <see cref="GateThresholds"/> override governs its gate OVER the run-wide dial (§3.5): a
    /// <c>needs-human: high</c> override lowers the bar so an assessed <c>high</c> escalates even though the
    /// run-wide dial <c>critical</c> would have best-guessed it (the control assertion proves that baseline).
    /// </summary>
    [Fact]
    public async Task Per_gate_override_applies_over_the_run_wide_dial()
    {
        var gates = new GateThresholds { NeedsHuman = EscalationThreshold.High };
        var judge = new CriticalityJudge(new FakePromptRunner(Assess("high")),
            Dial(EscalationThreshold.Critical, gates));

        CriticalityDecision overridden = await judge.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.Escalate, overridden.Outcome);

        // Control: WITHOUT the per-gate override, run-wide `critical` best-guesses the same `high` call —
        // proving it is the override, not the dial, forcing the escalation above.
        var control = new CriticalityJudge(new FakePromptRunner(Assess("high")), Dial(EscalationThreshold.Critical));
        CriticalityDecision baseline = await control.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, baseline.Outcome);
    }

    /// <summary>
    /// A per-gate override binds ONLY to its own gate: a <c>wave-checkpoint</c> override does not govern a
    /// <c>needs-human</c> gate, which still falls back to the run-wide dial (§3.5).
    /// </summary>
    [Fact]
    public async Task Per_gate_override_binds_only_to_its_own_gate()
    {
        var gates = new GateThresholds { WaveCheckpoint = EscalationThreshold.High };
        var judge = new CriticalityJudge(new FakePromptRunner(Assess("high")),
            Dial(EscalationThreshold.Critical, gates));

        // The wave-checkpoint override is irrelevant to a needs-human gate ⇒ run-wide `critical` governs ⇒ proceed.
        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, decision.Outcome);
    }

    // ---- malformed / absent assessment ⇒ escalate (invariant 1, §4.3) --------------------------

    /// <summary>
    /// Invariant 1 (§4.3): the judge is NEVER the verdict authority. An unparseable / empty / criticality-less
    /// assessment ⇒ the decider ESCALATES (the safe default) — even at the BOLDEST legal dial (<c>critical</c>),
    /// and it records no criticality.
    /// </summary>
    [Theory]
    [InlineData("")]                                // empty
    [InlineData("   ")]                             // whitespace only
    [InlineData("not json at all — {oops")]         // unparseable
    [InlineData("{}")]                              // valid JSON, but no criticality field
    [InlineData("""{"criticality":"bogus"}""")]     // unknown criticality token
    public async Task Malformed_or_absent_assessment_escalates(string resultText)
    {
        var runner = new FakePromptRunner(resultText);
        var judge = new CriticalityJudge(runner, Dial(EscalationThreshold.Critical));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);

        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
        Assert.Null(decision.Criticality); // nothing parseable ⇒ no assessed level
    }

    /// <summary>An assessment whose runner did not complete (or reported an error) is treated as absent ⇒ escalate (§4.3).</summary>
    [Fact]
    public async Task Errored_or_incomplete_assessment_runner_escalates()
    {
        // The result text LOOKS like a low-criticality best-guess, but the runner did not complete —
        // the decider must not trust it; escalate.
        var runner = new FakePromptRunner(Assess("low"), completed: false);
        var judge = new CriticalityJudge(runner, Dial(EscalationThreshold.Critical));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);

        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
    }

    // ---- the proceed-unreviewed clamp (§5.2, Blocker 1) ----------------------------------------

    /// <summary>
    /// The clamp (§5.2 / Blocker 1): under <c>review-gate: proceed-unreviewed</c>, an assessed <c>high</c> or
    /// <c>critical</c> ALWAYS escalates — here overriding a permissive per-gate <c>needs-human: critical</c>
    /// that would otherwise best-guess the <c>high</c> call.
    /// </summary>
    [Theory]
    [InlineData("high")]
    [InlineData("critical")]
    public async Task Clamp_forces_high_or_critical_to_escalate_under_proceed_unreviewed(string criticality)
    {
        var gates = new GateThresholds
        {
            NeedsHuman = EscalationThreshold.Critical, // would best-guess `high` — but the clamp overrides it
            ReviewGate = ReviewGateDecision.ProceedUnreviewed
        };
        var judge = new CriticalityJudge(new FakePromptRunner(Assess(criticality)),
            Dial(EscalationThreshold.High, gates));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
    }

    /// <summary>The clamp touches only the hard calls: under <c>proceed-unreviewed</c> a <c>low</c>/<c>moderate</c> call is unaffected (§5.2).</summary>
    [Theory]
    [InlineData("low")]
    [InlineData("moderate")]
    public async Task Clamp_leaves_low_or_moderate_calls_unaffected_under_proceed_unreviewed(string criticality)
    {
        var gates = new GateThresholds
        {
            NeedsHuman = EscalationThreshold.Critical, // best-guess everything below critical…
            ReviewGate = ReviewGateDecision.ProceedUnreviewed
        };
        var judge = new CriticalityJudge(new FakePromptRunner(Assess(criticality)),
            Dial(EscalationThreshold.High, gates));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, decision.Outcome); // …low/moderate proceed; the clamp only bites high/critical
    }

    /// <summary>
    /// The clamp is triggered BY <c>proceed-unreviewed</c>: the identical config WITHOUT it best-guesses the
    /// <c>high</c> call (the per-gate <c>critical</c> override governs); WITH it, the clamp forces escalate.
    /// </summary>
    [Fact]
    public async Task Clamp_engages_only_under_proceed_unreviewed()
    {
        // Same permissive per-gate override, WITHOUT proceed-unreviewed ⇒ high best-guesses.
        var open = new GateThresholds { NeedsHuman = EscalationThreshold.Critical };
        var openJudge = new CriticalityJudge(new FakePromptRunner(Assess("high")),
            Dial(EscalationThreshold.High, open));
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, (await openJudge.AssessAsync(NeedsHuman(), Ct)).Outcome);

        // Add proceed-unreviewed ⇒ the clamp flips the SAME high call to escalate.
        var clamped = new GateThresholds
        {
            NeedsHuman = EscalationThreshold.Critical,
            ReviewGate = ReviewGateDecision.ProceedUnreviewed
        };
        var clampedJudge = new CriticalityJudge(new FakePromptRunner(Assess("high")),
            Dial(EscalationThreshold.High, clamped));
        Assert.Equal(CriticalityOutcome.Escalate, (await clampedJudge.AssessAsync(NeedsHuman(), Ct)).Outcome);
    }

    /// <summary>
    /// The clamp overrides ANY per-gate override value (the literal §3.5 example — "even a per-gate
    /// <c>needs-human: low</c>"): under <c>proceed-unreviewed</c>, an assessed <c>high</c> escalates whatever
    /// the per-gate override is.
    /// </summary>
    [Theory]
    [InlineData(EscalationThreshold.Low)]
    [InlineData(EscalationThreshold.Moderate)]
    [InlineData(EscalationThreshold.High)]
    [InlineData(EscalationThreshold.Critical)]
    public async Task Clamp_escalates_high_regardless_of_per_gate_override_value(EscalationThreshold perGateOverride)
    {
        var gates = new GateThresholds
        {
            NeedsHuman = perGateOverride,
            ReviewGate = ReviewGateDecision.ProceedUnreviewed
        };
        var judge = new CriticalityJudge(new FakePromptRunner(Assess("high")),
            Dial(EscalationThreshold.High, gates));

        CriticalityDecision decision = await judge.AssessAsync(NeedsHuman(), Ct);
        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
    }

    // ---- the maxJudgeWidenings run-level cap (§4.3) ---------------------------------------------

    /// <summary>
    /// §4.3: the judge may WIDEN an unknown failure to retryable only up to <c>maxJudgeWidenings</c> times
    /// across the run (a run-level counter, threaded via the injected <see cref="WideningLedger"/>). Under the
    /// cap it proceeds (retry) and counts; once the cap is spent a further unknown failure escalates
    /// deterministically — even though the judge still wants to widen.
    /// </summary>
    [Fact]
    public async Task Judge_may_widen_an_unknown_failure_only_up_to_the_run_level_cap()
    {
        var ledger = new WideningLedger();
        var judge = new CriticalityJudge(
            new FakePromptRunner(Widen(retryable: true, "intermittent 502 from upstream")),
            Dial(EscalationThreshold.High, maxWidenings: 2),
            ledger);

        CriticalityDecision first = await judge.AssessAsync(Unknown(), Ct);
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, first.Outcome);
        Assert.True(first.Widened);
        Assert.Equal(1, ledger.Count);

        CriticalityDecision second = await judge.AssessAsync(Unknown(), Ct);
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, second.Outcome);
        Assert.True(second.Widened);
        Assert.Equal(2, ledger.Count);

        // Cap now spent ⇒ a third unknown failure escalates deterministically; the escalation does NOT widen.
        CriticalityDecision third = await judge.AssessAsync(Unknown(), Ct);
        Assert.Equal(CriticalityOutcome.Escalate, third.Outcome);
        Assert.False(third.Widened);
        Assert.Equal(2, ledger.Count); // unchanged — the escalated call spent no widening
    }

    /// <summary>
    /// A ledger already at the cap (driven there directly) forces the next unknown failure to escalate — even
    /// at the boldest legal dial and even though the judge still reports retryable (§4.3).
    /// </summary>
    [Fact]
    public async Task A_spent_widening_cap_escalates_a_further_unknown_failure_deterministically()
    {
        var ledger = new WideningLedger(count: 3); // cap already spent
        var judge = new CriticalityJudge(
            new FakePromptRunner(Widen(retryable: true, "looks transient")),
            Dial(EscalationThreshold.Critical, maxWidenings: 3),
            ledger);

        CriticalityDecision decision = await judge.AssessAsync(Unknown(), Ct);

        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
        Assert.False(decision.Widened);
        Assert.Equal(3, ledger.Count); // no further widening spent
    }

    /// <summary>
    /// The §4.3 safe default: an unknown failure the judge DECLINES to widen (retryable=false) escalates and
    /// spends none of the cap — the judge can only ever make the harness MORE patient, never less.
    /// </summary>
    [Fact]
    public async Task An_unknown_failure_the_judge_declines_to_widen_escalates_without_spending_the_cap()
    {
        var ledger = new WideningLedger();
        var judge = new CriticalityJudge(
            new FakePromptRunner(Widen(retryable: false, "structural — no retry will help")),
            Dial(EscalationThreshold.Critical, maxWidenings: 3),
            ledger);

        CriticalityDecision decision = await judge.AssessAsync(Unknown(), Ct);

        Assert.Equal(CriticalityOutcome.Escalate, decision.Outcome);
        Assert.False(decision.Widened);
        Assert.Equal(0, ledger.Count); // no widening ⇒ cap untouched
    }

    /// <summary>
    /// Widening rationale is advisory self-report (§4.3): when the judge widens, the decision CARRIES the
    /// rationale text (for the forensic trail, §6.1), but the record is advisory — it STILL counts against the
    /// run-level cap.
    /// </summary>
    [Fact]
    public async Task A_widening_records_the_advisory_rationale_and_still_counts_against_the_cap()
    {
        const string rationale = "upstream returned 503 twice; a bounded retry should clear it";
        var ledger = new WideningLedger();
        var judge = new CriticalityJudge(
            new FakePromptRunner(Widen(retryable: true, rationale)),
            Dial(EscalationThreshold.High, maxWidenings: 3),
            ledger);

        CriticalityDecision decision = await judge.AssessAsync(Unknown(), Ct);

        // The decision carries the judge's rationale…
        Assert.Equal(CriticalityOutcome.ProceedBestGuess, decision.Outcome);
        Assert.True(decision.Widened);
        Assert.Equal(rationale, decision.Rationale);
        // …but the advisory self-report STILL counts against the cap.
        Assert.Equal(1, ledger.Count);
    }
}
