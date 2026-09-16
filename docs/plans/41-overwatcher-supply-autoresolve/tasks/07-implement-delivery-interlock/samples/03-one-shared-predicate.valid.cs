// Sample: the CORRECT shape 03-one-shared-predicate.ps1 must accept -> must exit 0.
//
// Kept COMPLETE rather than a fragment (usings, namespace, type, and the real construct): an
// incomplete valid sample fails for a DIFFERENT reason and masks the one the check is about.
//
// It deliberately carries the two traps a valid half exists to expose:
//   1. XML doc comments that NAME DecisionTokens.AutoSupplied and DecisionTokens.ProceededBestGuess.
//      The clauses read comment-stripped source, so these must NOT count toward the "exactly once"
//      totals. If a future edit drops the comment strip, this half turns red and says so.
//   2. ProceededUnreviewed appearing TWICE - once in the shared predicate, once in the wave count -
//      which is legitimate and must not be flagged.
using Guardrails.Core.Execution;

namespace Guardrails.Core.Execution;

/// <summary>
/// The pure end-of-run policy over a run's recorded decision stream. The delivery-interlock token set is
/// written in exactly ONE place - see <see cref="HoldsDelivery"/> - so a new suppressing token such as
/// DecisionTokens.AutoSupplied cannot be added to the run-end spelling and missed by the wave-barrier one
/// (design 41 fact 11). Note this comment names DecisionTokens.ProceededBestGuess too, and must not count.
/// </summary>
public static class RunOutcomePolicy
{
    /// <summary>
    /// THE delivery-interlock token set, spelled once. True for a best guess, an unreviewed wave, and a
    /// certified missing-resource auto-supply; false for every other token, including auto-applied.
    /// </summary>
    private static bool HoldsDelivery(DecisionEntry decision) =>
        decision.Decision == DecisionTokens.ProceededBestGuess ||
        decision.Decision == DecisionTokens.ProceededUnreviewed ||
        decision.Decision == DecisionTokens.AutoSupplied;

    /// <summary>The first decision that holds delivery at RUN END, or null when none does.</summary>
    public static DecisionEntry? SuppressingDecision(IEnumerable<DecisionEntry> decisions) =>
        decisions.FirstOrDefault(HoldsDelivery);

    /// <summary>True when the run recorded a decision that holds delivery.</summary>
    public static bool SuppressesDelivery(IEnumerable<DecisionEntry> decisions) =>
        SuppressingDecision(decisions) is not null;

    /// <summary>
    /// The delivery-scoped counterpart: the first decision that holds delivery AND whose wave this
    /// delivery carries (or which is unattributed, so the check fails closed).
    /// </summary>
    public static DecisionEntry? SuppressingDecisionForDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        decisions.FirstOrDefault(d => HoldsDelivery(d) && (d.Wave is null || coveredWaves.Contains(d.Wave)));

    /// <summary>True when a decision holds THIS delivery.</summary>
    public static bool SuppressesDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        SuppressingDecisionForDelivery(decisions, coveredWaves) is not null;

    /// <summary>
    /// The count of unreviewed waves - the distinct-exit trigger. NOT part of the interlock, which is why
    /// it names DecisionTokens.ProceededUnreviewed a second time and the guardrail must tolerate that.
    /// </summary>
    public static int ProceededUnreviewedWaveCount(IEnumerable<DecisionEntry> decisions) =>
        decisions.Count(d => d.Decision == DecisionTokens.ProceededUnreviewed);
}
