using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// How a <see cref="AutonomyPolicy"/>-<c>prompt</c> tier confirms an overwatcher proposal (doc 11 §6). Core
/// never touches the console; the CLI supplies the implementation. In v1 the overwatcher fires from inside a
/// live run/worker, so the production adapter treats every context as non-interactive → honest halt (mid-run
/// TTY confirmation is a v2 UX bet); the seam exists so a fake can exercise the APPLY path in tests and v2
/// can add real interaction WITHOUT touching Core.
/// </summary>
public interface IOverwatchInteraction
{
    /// <summary>
    /// Decide whether to APPLY the sanctioned allowlist change the overwatcher proposes for
    /// <paramref name="task"/> at <paramref name="trigger"/>. <paramref name="sanctionedChangeSummary"/> is
    /// the one-line description of the change (guidance / budget bump) shown to the operator. MUST return
    /// <see cref="OverwatchInteractionResult.NonInteractive"/> (never APPLY) when there is no interactive
    /// operator — the honest-halt invariant (never blocks, never spends unbidden).
    /// </summary>
    OverwatchInteractionResult ConfirmApply(
        OverwatchProposal proposal, TaskNode task, OverwatchTrigger trigger, string sanctionedChangeSummary);

    /// <summary>The default: never interactive → always honest-halt. Wired everywhere except a fake that exercises the apply path.</summary>
    static IOverwatchInteraction NonInteractive { get; } = new NonInteractiveInteraction();

    private sealed class NonInteractiveInteraction : IOverwatchInteraction
    {
        public OverwatchInteractionResult ConfirmApply(
            OverwatchProposal proposal, TaskNode task, OverwatchTrigger trigger, string sanctionedChangeSummary) =>
            OverwatchInteractionResult.NonInteractive;
    }
}

/// <summary>The operator's response to an overwatcher proposal at the <c>prompt</c> tier.</summary>
public enum OverwatchInteractionResult
{
    /// <summary>Apply the sanctioned change and grant one more attempt (<c>prompted-approved</c>).</summary>
    Apply,

    /// <summary>An interactive operator declined — honest halt (<c>prompted-declined</c>).</summary>
    Declined,

    /// <summary>No interactive operator (redirected stdin / live region / CI) — honest halt (<c>halted</c>), never blocks.</summary>
    NonInteractive
}
