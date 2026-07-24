namespace Guardrails.Cli;

/// <summary>Process exit codes (SSOT §7).</summary>
public static class ExitCodes
{
    /// <summary>Everything green.</summary>
    public const int Success = 0;

    /// <summary>Harness or validation error (the plan could not be run).</summary>
    public const int HarnessError = 1;

    /// <summary>The run completed but at least one task needs a human (or was blocked).</summary>
    public const int TaskFailed = 2;

    /// <summary>The run was cancelled (Ctrl+C); in-flight tasks were journaled back to pending.</summary>
    public const int Cancelled = 3;

    /// <summary>
    /// The run halted with unresolved escalations — an autonomous-mode answer-required halt (§7.1): the wired
    /// escalation sink left at least one <c>logs/&lt;runId&gt;/escalations/</c> record <c>open</c>, awaiting a
    /// firstmate answer. Distinct from <see cref="TaskFailed"/> so a consumer can tell an answer-required halt
    /// apart from a plain needs-human and never read either as green.
    /// </summary>
    public const int EscalationsPending = 4;

    /// <summary>
    /// The run drained WHOLLY GREEN but PROCEEDED THROUGH one or more waves UNREVIEWED — an autonomous-mode
    /// review-gate resolution (§7.1; Option P, §5.2): with <c>autonomy.gateThresholds.review-gate:
    /// proceed-unreviewed</c> an unreviewed wave RAN with no human review and a <c>proceeded-unreviewed</c>
    /// decision was recorded. Distinct from <see cref="Success"/> so a firstmate consumer can tell a
    /// proceed-unreviewed run apart from clean green (0), a plain needs-human (2), and an answer-required halt
    /// (4) — and never read it as an ordinary success. Its verified work is delivery-suppressed
    /// (<c>mergeOnSuccess</c> forced off, #340), sitting on the plan branch. The harness NEVER forged a review
    /// marker (§5 floor 3). An unresolved escalation still takes precedence (that path is non-green ⇒
    /// <see cref="EscalationsPending"/>); this code is for the otherwise-green case only.
    /// </summary>
    public const int ProceededUnreviewed = 5;
}
