// Sample: the ONE defect 03-one-shared-predicate.ps1 exists to catch -> must exit NON-ZERO.
//
// THE DEFECT: no shared predicate. Each spelling lists the interlock tokens itself, and auto-supplied
// has been added to BOTH. This is the shape that passes every test in the pair - the run-end rows are
// green, the wave-barrier rows are green, BothSpellingsAgreeOnEveryToken is green - and leaves design
// 41 fact 11 exactly where it was. The NEXT suppressing token is the one that gets added to one list
// and not the other, and nothing then fails until machine-decided work is already on the user's branch.
//
// Note the trap it carries, pointing the other way from the valid half: the XML doc below CLAIMS the
// token set is shared. A reader skimming the comments would believe it. The clauses read the code.
using Guardrails.Core.Execution;

namespace Guardrails.Core.Execution;

/// <summary>
/// The pure end-of-run policy. The delivery-interlock token set is shared between the run-end and
/// wave-barrier spellings via a single predicate, so a new token cannot be missed by one of them.
/// </summary>
public static class RunOutcomePolicy
{
    /// <summary>The first decision that holds delivery at RUN END, or null when none does.</summary>
    public static DecisionEntry? SuppressingDecision(IEnumerable<DecisionEntry> decisions) =>
        decisions.FirstOrDefault(d =>
            d.Decision == DecisionTokens.ProceededBestGuess ||
            d.Decision == DecisionTokens.ProceededUnreviewed ||
            d.Decision == DecisionTokens.AutoSupplied);

    /// <summary>True when the run recorded a decision that holds delivery.</summary>
    public static bool SuppressesDelivery(IEnumerable<DecisionEntry> decisions) =>
        SuppressingDecision(decisions) is not null;

    /// <summary>The delivery-scoped counterpart, with its own copy of the same token set.</summary>
    public static DecisionEntry? SuppressingDecisionForDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        decisions.FirstOrDefault(d =>
            (d.Decision == DecisionTokens.ProceededBestGuess ||
             d.Decision == DecisionTokens.ProceededUnreviewed ||
             d.Decision == DecisionTokens.AutoSupplied) &&
            (d.Wave is null || coveredWaves.Contains(d.Wave)));

    /// <summary>True when a decision holds THIS delivery.</summary>
    public static bool SuppressesDelivery(
        IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
        SuppressingDecisionForDelivery(decisions, coveredWaves) is not null;

    /// <summary>The count of unreviewed waves - the distinct-exit trigger.</summary>
    public static int ProceededUnreviewedWaveCount(IEnumerable<DecisionEntry> decisions) =>
        decisions.Count(d => d.Decision == DecisionTokens.ProceededUnreviewed);
}
