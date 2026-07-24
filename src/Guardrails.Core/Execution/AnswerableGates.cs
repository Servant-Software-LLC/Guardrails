namespace Guardrails.Core.Execution;

/// <summary>
/// The SINGLE source of truth for which escalation gates an answer file may bind (doc 12 §7.3, SSOT §7.2) and
/// which hard calls the <c>proceed-unreviewed</c> clamp makes NON-answerable (doc 12 §5.2/§7.3, Blocker 1).
/// Extracted so EVERY writer/reader of the reply channel — <see cref="AnswerFileConsumer"/> (the resume-time
/// consumer) AND <see cref="EscalationPick"/> (the interactive / web pick surfaces, issue #387) — enforces the
/// EXACT same non-answerable floor, rather than each re-deriving the set. Reusing one predicate is the whole
/// point: a pick surface can never offer (or write) an answer for a gate the consumer would reject.
/// </summary>
public static class AnswerableGates
{
    /// <summary>The task-level answerable gate — an agent-emitted <c>{"needsHuman": …}</c> judgment call.</summary>
    public const string NeedsHumanGate = "needs-human";

    /// <summary>The wave-boundary answerable gate — a JIT wave checkpoint (<c>proceed</c>/<c>hold</c>).</summary>
    public const string WaveCheckpointGate = "wave-checkpoint";

    /// <summary>The NON-answerable review gate (§7.5): no answer kind exists that resolves it; listed here only so callers can name it.</summary>
    public const string ReviewGate = "review-gate";

    /// <summary>The two — and only two — gates an answer file may bind (§7.3): everything else is terminal / non-answerable.</summary>
    private static readonly HashSet<string> Gates = new(StringComparer.Ordinal)
    {
        NeedsHumanGate,
        WaveCheckpointGate
    };

    /// <summary>The clamped hard-call criticalities (§5.2/§7.3, Blocker 1): NON-answerable under <c>proceed-unreviewed</c>.</summary>
    private static readonly HashSet<string> ClampedCriticalities = new(StringComparer.OrdinalIgnoreCase)
    {
        "high",
        "critical"
    };

    /// <summary>True when <paramref name="gate"/> is one of the two answerable gates (§7.3). A <c>review-gate</c> / <c>blocker</c> / terminal gate is NOT answerable.</summary>
    public static bool IsAnswerable(string gate) => Gates.Contains(gate);

    /// <summary>
    /// True when a <c>high</c>/<c>critical</c> hard call inside an explicitly-unreviewed wave is clamped
    /// NON-answerable (§5.2/§7.3, Blocker 1): under <c>proceed-unreviewed</c> such a call keeps a HUMAN in the
    /// loop, so no answer file (nor pick surface) may auto-clear it — else the "Guardrails without Guardrails"
    /// loop re-opens. Inert (false) when the run is NOT under <c>proceed-unreviewed</c>.
    /// </summary>
    public static bool IsClampedHardCall(string? criticality, bool proceedUnreviewed) =>
        proceedUnreviewed && criticality is not null && ClampedCriticalities.Contains(criticality);
}
