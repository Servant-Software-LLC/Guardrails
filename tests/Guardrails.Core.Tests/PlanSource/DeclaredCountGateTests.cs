using Guardrails.Core.Breakdown;

namespace Guardrails.Core.Tests.PlanSource;

/// <summary>
/// RED tests for <see cref="DeclaredCountGate"/> (docs/plans/24-plan-source-provenance.md §4). The
/// harness compares what the plan source DECLARED (N, a sibling task's deliverable, taken here as a
/// plain <see langword="int"/> input) against what the produced plan folder RECORDED (M — the number of
/// <c>## DECISION</c> sections in <c>decisions.md</c>, and 0 when that file does not exist). The gate
/// fails when N &gt;= 1 and M != N; a plan declaring 0 is not evidence of anything, so it always passes.
/// Tagged Category=PlanSourceProvenance (class-level, inherited by every case) so the plan's baseline
/// preflight can exclude this deliberately-red suite via <c>--filter "Category!=PlanSourceProvenance"</c>.
/// </summary>
[Trait("Category", "PlanSourceProvenance")]
public sealed class DeclaredCountGateTests : IDisposable
{
    private readonly string _planFolder;

    public DeclaredCountGateTests()
    {
        _planFolder = Path.Combine(Path.GetTempPath(), "gr-declared-count-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_planFolder, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Writes decisions.md with <paramref name="sectionCount"/> reserved <c>## DECISION \`id\`</c>
    /// headings — the shape the plan-breakdown skill's own decisions.md format contract reserves for
    /// Charter-delegated ids (SKILL.md 0d.4). Deliberately minimal: this gate only counts sections, it
    /// does not load or validate a plan.
    /// </summary>
    private void WriteDecisions(int sectionCount)
    {
        var lines = new List<string> { "# Delegated decisions", "" };
        for (int i = 0; i < sectionCount; i++)
        {
            lines.Add($"## DECISION `d{i}` — a question");
            lines.Add("");
        }

        File.WriteAllText(Path.Combine(_planFolder, "decisions.md"), string.Join(Environment.NewLine, lines));
    }

    [Fact]
    public void FailsWhenTheFolderRecordsFewerThanTheDeclaredCount()
    {
        WriteDecisions(1);

        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(2, _planFolder);

        Assert.False(result.Passed);
    }

    [Fact]
    public void FailsWhenTheFolderRecordsMoreThanTheDeclaredCount()
    {
        // The rule is M != N, not M < N - the agent and the plan disagree in either direction.
        WriteDecisions(3);

        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(2, _planFolder);

        Assert.False(result.Passed);
    }

    [Fact]
    public void FailsWhenNoDecisionsFileExistsAndThePlanDeclaresOne()
    {
        // No decisions.md written at all - the never-scanned breakdown, the case the plan-root
        // preflight cannot see because that preflight is authored by the very agent it polices.
        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(1, _planFolder);

        Assert.False(result.Passed);
    }

    [Fact]
    public void PassesWhenTheRecordedCountEqualsTheDeclaredCount()
    {
        WriteDecisions(2);

        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(2, _planFolder);

        Assert.True(result.Passed);
    }

    [Fact]
    public void PassesWhenThePlanDeclaresZero()
    {
        // N = 0 passes regardless of what the folder records - the gate binds only at N >= 1.
        WriteDecisions(5);

        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(0, _planFolder);

        Assert.True(result.Passed);
    }

    [Fact]
    public void CountsOneDecisionPerSectionInTheDecisionsFile()
    {
        // Isolates the counting mechanics from the pass/fail relationship: M must be the number of
        // sections, not lines and not mere file existence, independent of what N happens to be.
        WriteDecisions(3);

        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(99, _planFolder);

        Assert.Equal(3, result.RecordedCount);
    }

    [Fact]
    public void FailureMessageNamesTheDeclaredAndRecordedCounts()
    {
        // No decisions.md - N = 2, M = 0. A failure that doesn't say 2 and 0 sends the reader to count
        // by hand.
        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(2, _planFolder);

        Assert.NotNull(result.FailureMessage);
        Assert.Contains("2", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("0", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMessageStatesBothLimitsOfTheCheck()
    {
        DeclaredCountGateResult result = DeclaredCountGate.Evaluate(1, _planFolder);

        Assert.NotNull(result.FailureMessage);
        // (a) it proves only the count, never that a decision was made well.
        Assert.Contains("not", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("well", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        // (b) it depends on Charter's count-line guarantee - markers with no count line is a Charter bug.
        Assert.Contains("Charter", result.FailureMessage, StringComparison.Ordinal);
    }
}
