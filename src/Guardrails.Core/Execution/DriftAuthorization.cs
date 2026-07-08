namespace Guardrails.Core.Execution;

/// <summary>
/// The operator-authorized safe-rewind plan captured by the CLI's pre-DAG drift probe and threaded into
/// the Scheduler (issue #274 Part C, SSOT §7.2 — the consent-vs-execution + compare-and-swap fix). The
/// default <c>autonomyPolicy: "prompt"</c> shows the operator a preview computed from THIS captured
/// decision; a <c>y</c> authorizes exactly THIS — so the Scheduler executes the CAPTURED set and
/// CAS-verifies the plan-branch tip still equals <see cref="ExpectedTip"/>, rather than re-deriving a
/// possibly-different plan from files an operator edited (or a commit a concurrent session added) during the
/// blocking prompt. If the fresh decision diverges from the captured one, or the tip has moved, the
/// Scheduler HALTS — it never rewinds a set the human did not approve. <c>null</c> = not pre-authorized (an
/// <c>auto</c> run auto-resolves on its own fresh decision; a <c>halt</c> or unconfirmed <c>prompt</c> run
/// halts).
/// </summary>
public sealed record DriftAuthorization
{
    /// <summary>The safe set S the operator approved (plan order).</summary>
    public required IReadOnlyList<string> SafeSet { get; init; }

    /// <summary>The approved rewind target (the <see cref="SafeSuffixDecision.ResetTarget"/> shown in the preview); null for a journal-only reset.</summary>
    public string? ResetTarget { get; init; }

    /// <summary>The plan-branch tip the operator saw when approving — the compare-and-swap anchor the Scheduler re-verifies before rewinding.</summary>
    public required string ExpectedTip { get; init; }
}
