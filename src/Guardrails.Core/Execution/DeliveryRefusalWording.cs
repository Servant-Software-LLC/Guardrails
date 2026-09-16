namespace Guardrails.Core.Execution;

/// <summary>
/// The fixed phrases a <c>branch-moved</c> delivery refusal's DETAIL is built from (design 39 §1c/§4,
/// issues #588/#726) — shared by the producer (<see cref="GitWorktreeProvider"/>, which composes the
/// detail) and the reader (<c>Scheduler.DeliveryRefusedRemedyDetail</c>, which picks the operator's
/// remedy out of it).
/// <para>
/// <b>Why a shared constant rather than two literals.</b> <c>branch-moved</c> is ONE outcome token covering
/// causes whose remedies are opposite — check a branch out again, versus simply resume — so the remedy can
/// only be chosen from the detail's own prose. That made the two files' string literals a silent coupling:
/// rewording a detail would have quietly started handing every refusal the generic remedy, with no test and
/// no compiler complaining. Naming the phrases here turns that into a compile-time coupling.
/// </para>
/// </summary>
internal static class DeliveryRefusalWording
{
    /// <summary>
    /// Opens a detail whose delivery target is this process's own run-start pin — nothing has landed yet, so
    /// the branch the run started on IS the branch it must deliver to.
    /// </summary>
    public const string RunStartedOn = "run started on";

    /// <summary>
    /// Opens a detail whose delivery target was RECORDED by an earlier delivery (issue #726) rather than
    /// pinned by this process. Saying "run started on ..." here would name the branch <c>HEAD</c> is standing
    /// on now, which is the thing being refused.
    /// </summary>
    public const string EarlierDeliveryLandedOn = "an earlier delivery landed on";

    /// <summary>
    /// Marks the OTHER <c>branch-moved</c> cause: the checkout is still on the delivery target, but that
    /// branch itself moved after the trial was built. Remedy: resume — the next trial carries the new
    /// commits. Telling the operator to check anything out there would be actively wrong.
    /// </summary>
    public const string AfterTheTrialWasBuilt = "after the trial was built";
}
