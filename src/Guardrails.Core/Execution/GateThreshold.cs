using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The ONE effective-threshold rule (design 41 §2.1): a per-gate <c>gateThresholds</c> override when
/// present, else the run-wide <c>escalationThreshold</c>. Spelled twice today — in
/// <c>CriticalityJudge.EffectiveThreshold</c> and <c>Scheduler.EffectiveThresholdToken</c> — which is
/// two places for one rule to drift apart. Both are replaced by this.
/// </summary>
public static class GateThreshold
{
    /// <param name="autonomy">The run's autonomy block, or null when there is none.</param>
    /// <param name="gate">The gate whose threshold is being resolved.</param>
    public static EscalationThreshold Effective(AutonomyConfig? autonomy, CriticalityGate gate)
        => throw new NotImplementedException();
}
