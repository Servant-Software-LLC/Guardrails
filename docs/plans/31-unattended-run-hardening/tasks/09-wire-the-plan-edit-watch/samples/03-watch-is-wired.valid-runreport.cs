// Companion sample: the RunReport.cs half of 03-watch-is-wired.ps1's clauses.
namespace Guardrails.Core.Execution;

public sealed record RunReport
{
    /// <summary>The pre-DAG drift decision this run took. Singular by design.</summary>
    public DecisionEntry? Decision { get; init; }

    /// <summary>Things the harness NOTICED rather than decided - N per run.</summary>
    public IReadOnlyList<DecisionEntry> Observations { get; init; } = [];
}
