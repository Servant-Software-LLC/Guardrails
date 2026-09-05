using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The escalation ladder (DoR issue #228): the pure rung-selection step the retry loop calls when a
/// task's PREVIOUS attempt failed its guardrails — "this attempt should resolve one rung stronger than
/// the last" — layered on top of §6.2 candidate selection (<see cref="TierResolver.SelectCandidate"/>)
/// rather than duplicating it.
///
/// <para>Task <c>02-implement-escalation-ladder</c> fills in both members below. Both currently throw
/// <see cref="NotImplementedException"/> so the tests in
/// <c>Guardrails.Core.Tests.Escalation.EscalationLadderTests</c> compile and fail red.</para>
/// </summary>
public static class EscalationLadder
{
    /// <summary>
    /// The rung immediately ABOVE <paramref name="servedRung"/> on <see cref="ActionTiers.All"/>, or
    /// <c>null</c> when there is none — <paramref name="servedRung"/> is the top rung, is null, or is
    /// not on the ladder at all.
    /// </summary>
    public static string? NextRung(string? servedRung) => throw new NotImplementedException();

    /// <summary>
    /// What the retry loop calls before launching an attempt: given the resolution the FIRST attempt of
    /// this task served (<paramref name="route"/>) and how many of this task's attempts have already
    /// failed their guardrails (<paramref name="escalations"/>), returns the resolution this attempt
    /// should serve.
    /// </summary>
    public static TierResolution Apply(RunConfig config, TierResolution route, int escalations) =>
        throw new NotImplementedException();
}
