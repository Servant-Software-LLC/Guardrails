using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the deterministic gate classifier (issue #361 Phase 3, doc 12 §4 / §4.1 — the classify-then-act
/// table). The classifier is a PURE function: it maps an ALREADY-OBSERVED signal to a
/// <see cref="GateClass"/> and never runs a prompt or makes a judgment. The dial governs class
/// <see cref="GateClass.JudgmentCall"/> ONLY; the hard-blocker classes and the
/// <see cref="GateClass.Floor"/> are NOT dial-eligible (invariant 1). The load-bearing negative:
/// an UNKNOWN/ambiguous signal defaults to <see cref="GateClass.HardBlockerPermanent"/> — the safe
/// default is escalate, NOT spin (§4.3).
///
/// <para>These are TDD-red tests: they COMPILE against the stub and FAIL until the mapping is
/// implemented by a later wave-03 task.</para>
/// </summary>
public sealed class GateClassifierTests
{
    // ── Class (b): hard blocker, retryable/transient ──────────────────────────────────────────────

    [Fact]
    public void Transient_IsHardBlockerRetryable()
    {
        // §4.1: PromptFailureKind.Transient (429/503/529, overloaded, rate/session/usage limit) is an
        // external, recoverable condition — bounded wait + backoff, NOT the dial. Reused verbatim.
        GateSignal signal = GateSignal.PromptFailure(PromptFailureKind.Transient);
        Assert.Equal(GateClass.HardBlockerRetryable, GateClassifier.Classify(signal));
    }

    // ── Class (c): hard blocker, permanent OR retry-exhausted ─────────────────────────────────────

    [Fact]
    public void PermissionWall_Halt_IsHardBlockerPermanent()
    {
        // §4.1: a permission wall (#266 / #86 / #104) already halts unconditionally — no best-guess
        // fixes a missing granted path. Build a genuine halt decision via the shipped tracker.
        var tracker = new PermissionWallTracker();
        tracker.Observe([".claude/skills/certify-knowledge/SKILL.md"]);
        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.True(decision.Halt);   // precondition: this IS a wall

        GateSignal signal = GateSignal.PermissionWall(decision);
        Assert.Equal(GateClass.HardBlockerPermanent, GateClassifier.Classify(signal));
    }

    [Fact]
    public void InfrastructureFault_RunAbort_IsHardBlockerPermanent()
    {
        // §4.1: an infrastructure fault / RunAbort (#150 — git unavailable, executor threw) is an
        // honest abort; no retry clears it → halt-and-escalate unconditionally.
        var abort = new RunAbort
        {
            Headline = "git is unavailable",
            Remedy = "restore git connectivity, then resume the run",
            Detail = "System.ComponentModel.Win32Exception: The system cannot find the file specified.",
        };
        GateSignal signal = GateSignal.InfrastructureFault(abort);
        Assert.Equal(GateClass.HardBlockerPermanent, GateClassifier.Classify(signal));
    }

    [Fact]
    public void PreflightFailure_IsHardBlockerPermanent()
    {
        // §4.1: a plan/wave preflight failure (SSOT §3.3/§14.3 — a dependency not materialized) means the
        // environment is not ready; a best-guess cannot fix it → escalate.
        GateSignal signal =
            GateSignal.PreflightFailure("upstream artifact 'src/output.txt' was not materialized");
        Assert.Equal(GateClass.HardBlockerPermanent, GateClassifier.Classify(signal));
    }

    // ── Class (a): judgment call (the ONLY dial-eligible class) ───────────────────────────────────

    [Fact]
    public void AgentNeedsHuman_DesignQuestion_IsJudgmentCall()
    {
        // §4.1: an agent-emitted {"needsHuman": "..."} is an explicit design question — a judgment call
        // with a best-guess, so the DIAL applies (escalate ≥ threshold, else proceed-with-best-guess).
        GateSignal signal = GateSignal.AgentNeedsHuman(
            "Should the cache key include the tenant id? No precedent exists in the codebase.");
        Assert.Equal(GateClass.JudgmentCall, GateClassifier.Classify(signal));
    }

    [Fact]
    public void WaveCheckpoint_NextWaveUnauthored_IsJudgmentCall()
    {
        // §4.1: the JIT wave-checkpoint (SSOT §14.4, #360 — next wave unauthored) is a dial-eligible
        // judgment call, NOT an unconditional halt. This wave-checkpoint + needsHuman are the only
        // genuinely NEW rows the autonomous dial adds to the classify-then-act table.
        GateSignal signal = GateSignal.WaveCheckpoint(WaveHaltKind.NextWaveUnauthored);
        Assert.Equal(GateClass.JudgmentCall, GateClassifier.Classify(signal));
    }

    // ── Floor: the dial may NEVER lower it (invariant 5) ──────────────────────────────────────────

    [Theory]
    // §4.1 floor row: the overwatcher's deterministic floor (doc 11 §8) — a terminal-exhaustion
    // needs-human (a task that could not converge to green) and a no-op deadlock. A doomed/exhausted
    // run is NEVER best-guessed past, at ANY dial threshold including `critical`.
    [InlineData(OverwatchTrigger.TerminalExhaustion)]
    [InlineData(OverwatchTrigger.NoOpDeadlock)]
    public void OverwatcherDeterministicFloor_IsFloor(OverwatchTrigger trigger)
    {
        GateSignal signal = GateSignal.Overwatch(trigger);
        Assert.Equal(GateClass.Floor, GateClassifier.Classify(signal));
    }

    // ── The load-bearing negative: unknown ⇒ escalate, never spin ─────────────────────────────────

    [Fact]
    public void UnknownSignal_DefaultsToHardBlockerPermanent_NeverRetryable()
    {
        // §4.3: an UNKNOWN/ambiguous failure is NOT silently treated as retryable — it defaults to
        // class (c) → escalate. The safe default is escalate, NOT spin. Asserting it is NOT retryable
        // guards the abuse mode (spin-to-ceiling on every unknown gate).
        GateSignal signal =
            GateSignal.Unknown("an unrecognized terminal signal with no classified failure kind");

        GateClass result = GateClassifier.Classify(signal);
        Assert.Equal(GateClass.HardBlockerPermanent, result);
        Assert.NotEqual(GateClass.HardBlockerRetryable, result);
    }
}
