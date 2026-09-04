using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// WHICH SITE supplied an attempt's rung (DoR <c>docs/plans/17-model-tiering.md</c> §12.4, D31) — the
/// one mapping from a §6.1 resolution branch plus the origin <c>PlanLoader</c> recorded at load, onto
/// the journal's <see cref="TierSource"/> enum.
///
/// <para><b>It lives here, beside <see cref="TierResolver"/>, because it has TWO readers.</b> The
/// attempt launcher records it in per-attempt provenance; <c>guardrails run --dry-run</c> prints it in
/// the preview's TIER column (issue #549). A second copy of this four-line switch is the D22a
/// divergence in miniature — and it would stay invisible until the day the preview attributed a rung
/// to the plan that the run attributed to the task.</para>
///
/// <para><b>The origin is READ, never reconstructed.</b> Deriving it by comparing the action's own tier
/// against the plan-wide default is wrong in the most ordinary case there is: a task that explicitly
/// writes the same token the plan already defaults to would be attributed to the plan.
/// <see cref="ActionDefinition.TierOrigin"/> exists precisely so this mapping is a lookup.</para>
/// </summary>
public static class TierProvenance
{
    /// <summary>
    /// The <see cref="TierSource"/> for <paramref name="route"/>:
    /// <list type="bullet">
    ///   <item>a full pin ⇒ <see cref="TierSource.Override"/>. "Bypasses tier resolution entirely"
    ///     governs what is SELECTED, not what is LOGGED: §12.4 gives each v1 value exactly one
    ///     producer, and a pin is override's. The rung stays absent beside it, because none
    ///     resolved.</item>
    ///   <item><see cref="TierOrigin.Task"/> ⇒ <see cref="TierSource.Task"/>,
    ///     <see cref="TierOrigin.PlanDefault"/> ⇒ <see cref="TierSource.PlanDefault"/> — with the rung
    ///     that was served recorded beside it.</item>
    ///   <item>the LEGACY path (no rung anywhere) ⇒ ABSENT. Nothing resolved and nothing was
    ///     overridden, and §12.4 deliberately has no enum value for it — "absent" and "override" are
    ///     different facts about how the attempt got its model, and a reader must be able to tell them
    ///     apart.</item>
    ///   <item>a null <paramref name="route"/> (a SCRIPT action) ⇒ ABSENT: no route, no rung, nothing
    ///     to source.</item>
    /// </list>
    /// </summary>
    public static TierSource? SourceFor(ActionDefinition action, TierResolution? route)
    {
        ArgumentNullException.ThrowIfNull(action);

        return route switch
        {
            null => null,
            { Pinned: true } => TierSource.Override,
            { Legacy: true } => null,
            _ => action.TierOrigin switch
            {
                TierOrigin.Task => TierSource.Task,
                TierOrigin.PlanDefault => TierSource.PlanDefault,
                // TierOrigin.None means no tier was written anywhere, which cannot co-exist with a
                // tier-resolved route in a loaded plan. Defensive, and absent is the honest answer.
                _ => null
            }
        };
    }
}
