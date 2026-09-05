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
    public static string? NextRung(string? servedRung)
    {
        int index = RungIndex(servedRung);

        return index < 0 || index + 1 >= ActionTiers.All.Count ? null : ActionTiers.All[index + 1];
    }

    /// <summary>
    /// <paramref name="rung"/>'s position on <see cref="ActionTiers.All"/>, or -1 when it is null or not
    /// on the ladder — the same defensive residual <c>TierResolver.RungIndex</c> computes, kept local
    /// here rather than exposed, since <see cref="NextRung"/> is this file's only consumer.
    /// </summary>
    private static int RungIndex(string? rung)
    {
        for (int i = 0; i < ActionTiers.All.Count; i++)
        {
            if (string.Equals(ActionTiers.All[i], rung, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// What the retry loop calls before launching an attempt: given the resolution the FIRST attempt of
    /// this task served (<paramref name="route"/>) and how many of this task's attempts have already
    /// failed their guardrails (<paramref name="escalations"/>), returns the resolution this attempt
    /// should serve.
    /// </summary>
    public static TierResolution Apply(RunConfig config, TierResolution route, int escalations)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(route);

        if (escalations <= 0 || route.Pinned || route.Legacy || route.NoRoute || route.Tier is null)
        {
            return route;
        }

        string servedFromOriginal = route.Tier;
        TierResolution current = route;

        for (int i = 0; i < escalations; i++)
        {
            if (NextRung(current.Tier) is not { } nextRung)
            {
                break; // Top of the ladder — keep what we have.
            }

            TierResolution candidate = TierResolver.SelectCandidate(config, nextRung);

            if (candidate.NoRoute)
            {
                break; // Nothing at or above the next rung routes — keep what we have.
            }

            current = candidate;
        }

        return ReferenceEquals(current, route) ? route : current with { EscalatedFrom = servedFromOriginal };
    }
}
