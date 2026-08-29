namespace Guardrails.Core.Breakdown;

/// <summary>
/// The outcome of <see cref="DeclaredCountGate.Evaluate"/>: whether the plan source's declared
/// delegated-decision count (N) agrees with what the produced plan folder recorded (M) —
/// docs/plans/24-plan-source-provenance.md §4.
/// </summary>
public sealed record DeclaredCountGateResult
{
    /// <summary>True when the gate passes — N &lt; 1, or M == N.</summary>
    public required bool Passed { get; init; }

    /// <summary>N — the delegated-decision count the plan source declared.</summary>
    public required int DeclaredCount { get; init; }

    /// <summary>M — the number of <c>## DECISION</c> sections the plan folder's decisions.md records (0 when the file does not exist).</summary>
    public required int RecordedCount { get; init; }

    /// <summary>Human-readable failure detail; null when <see cref="Passed"/> is true.</summary>
    public string? FailureMessage { get; init; }
}

/// <summary>
/// Compares what a plan source DECLARED (N delegated decisions) against what a produced breakdown
/// RECORDED (M <c>## DECISION</c> sections in <c>&lt;planFolder&gt;/decisions.md</c>) —
/// docs/plans/24-plan-source-provenance.md §4. The one case the plan-root preflight (authored by the
/// same breakdown it polices) structurally cannot catch: a breakdown that never scanned for delegated
/// decisions produces no decisions.md, so M = 0.
/// </summary>
public static class DeclaredCountGate
{
    /// <summary>
    /// Evaluate the gate for a produced <paramref name="planFolder"/> against the
    /// <paramref name="declaredDelegatedDecisions"/> the plan source declared (N). Fails when N &gt;= 1
    /// and the recorded count differs from N; passes otherwise (including whenever N is 0).
    /// </summary>
    public static DeclaredCountGateResult Evaluate(int declaredDelegatedDecisions, string planFolder)
    {
        int recordedCount = CountDecisionSections(planFolder);

        if (declaredDelegatedDecisions < 1 || recordedCount == declaredDelegatedDecisions)
        {
            return new DeclaredCountGateResult
            {
                Passed = true,
                DeclaredCount = declaredDelegatedDecisions,
                RecordedCount = recordedCount,
            };
        }

        string message =
            $"Plan source declared {declaredDelegatedDecisions} delegated decision(s), but " +
            $"{planFolder}\\decisions.md recorded {recordedCount}. This gate proves only that the " +
            "counts agree, not that any decision was made well. It relies on Charter emitting a " +
            "count line whenever the count is at least 1; markers present with no count line is a " +
            "bug to file against Charter, not a defect in this plan.";

        return new DeclaredCountGateResult
        {
            Passed = false,
            DeclaredCount = declaredDelegatedDecisions,
            RecordedCount = recordedCount,
            FailureMessage = message,
        };
    }

    private static int CountDecisionSections(string planFolder)
    {
        string decisionsPath = Path.Combine(planFolder, "decisions.md");
        if (!File.Exists(decisionsPath))
        {
            return 0;
        }

        int count = 0;
        foreach (string line in File.ReadLines(decisionsPath))
        {
            if (line.StartsWith("## DECISION", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
