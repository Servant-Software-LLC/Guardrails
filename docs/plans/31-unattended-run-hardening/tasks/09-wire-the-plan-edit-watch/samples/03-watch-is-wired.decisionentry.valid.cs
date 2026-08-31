// Companion sample: the DecisionEntry.cs half of 03-watch-is-wired.ps1's clauses.
namespace Guardrails.Core.Execution;

public static class DecisionBoundaries
{
    public const string Drift = "drift";
    public const string PlanEdit = "plan-edit";
}

public static class DecisionTokens
{
    public const string Halted = "halted";
    // "observed" - the harness noticed and reported at this boundary; nothing was decided and nothing
    // changed. Outcome-inert because RunOutcomePolicy branches on the DECISION token only.
    public const string Observed = "observed";
}
