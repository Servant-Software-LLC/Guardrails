using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #385 — the between-wave breakdown invocation (<see cref="WaveBreakdownInvoker"/>) must run under a
/// turn budget generous enough to author a LARGE wave in one pass, so it never truncates mid-authoring into
/// an invalid partial (the #385 GR1001-quarantine incident: a ~11-task wave exhausted the old fixed 120-turn
/// cap). These pin the pure budget math: a generous base that already covers a large wave, PLUS scaling from
/// the wave's <c>brief.md</c> size — never dropping below the old cap, never unbounded.
/// </summary>
public sealed class WaveBreakdownBudgetTests
{
    /// <summary>The old fixed cap that TRUNCATED the ~11-task wave — the new budget must never fall to/below it.</summary>
    private const int OldTruncatingCap = 120;

    // --- ComputeMaxTurns -------------------------------------------------------------------------------

    [Fact]
    public void ComputeMaxTurns_ZeroSignal_IsGenerousBase_WellAboveOldTruncatingCap()
    {
        // Even with NO brief signal (the base case), the budget must be a generous fixed ceiling that on its
        // own covers a large wave — comfortably above the old 120 that truncated.
        int budget = WaveBreakdownInvoker.ComputeMaxTurns(0);
        Assert.True(budget > OldTruncatingCap,
            $"the #385 raise must exceed the old fixed {OldTruncatingCap} that truncated; got {budget}");
        Assert.True(budget >= 400, $"the base budget should be generous (>= 400); got {budget}");
    }

    [Fact]
    public void ComputeMaxTurns_ScalesUpWithBriefSize()
    {
        int small = WaveBreakdownInvoker.ComputeMaxTurns(1);
        int large = WaveBreakdownInvoker.ComputeMaxTurns(20);
        Assert.True(large > small, $"a larger wave must get a larger budget; {large} !> {small}");
    }

    [Fact]
    public void ComputeMaxTurns_MonotonicallyNonDecreasing_InSignalCount()
    {
        int prev = WaveBreakdownInvoker.ComputeMaxTurns(0);
        for (int signals = 1; signals <= 60; signals++)
        {
            int budget = WaveBreakdownInvoker.ComputeMaxTurns(signals);
            Assert.True(budget >= prev, $"budget must be non-decreasing in size; {budget} < {prev} at {signals}");
            prev = budget;
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(50)]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public void ComputeMaxTurns_AlwaysWithinFloorAndCeiling(int signals)
    {
        int budget = WaveBreakdownInvoker.ComputeMaxTurns(signals);
        Assert.True(budget > OldTruncatingCap, $"never below the old cap; got {budget}");
        Assert.True(budget <= 1_000, $"never above the hard ceiling; got {budget}");
    }

    [Fact]
    public void ComputeMaxTurns_HugeBrief_ClampsToCeiling()
    {
        Assert.Equal(1_000, WaveBreakdownInvoker.ComputeMaxTurns(100_000));
    }

    [Fact]
    public void ComputeMaxTurns_NegativeSignal_TreatedAsZero_Base()
    {
        Assert.Equal(WaveBreakdownInvoker.ComputeMaxTurns(0), WaveBreakdownInvoker.ComputeMaxTurns(-42));
    }

    // --- EstimateBriefSignalCount ----------------------------------------------------------------------

    [Fact]
    public void EstimateBriefSignalCount_NullOrWhitespace_IsZero()
    {
        Assert.Equal(0, WaveBreakdownInvoker.EstimateBriefSignalCount(null));
        Assert.Equal(0, WaveBreakdownInvoker.EstimateBriefSignalCount(""));
        Assert.Equal(0, WaveBreakdownInvoker.EstimateBriefSignalCount("   \n\t  \n"));
    }

    [Fact]
    public void EstimateBriefSignalCount_CountsListItemsAndSubHeadings_ExcludesLevel1Title()
    {
        // The real #385 wave-04 brief SHAPE: a level-1 title (excluded), sub-headings, and bullets under them.
        string brief = """
            # Wave 4 — review-gate policy

            > A blockquote note — not a work item.

            ## What this wave must accomplish

            - proceed-unreviewed opt-in
            - gateThresholds.review-gate handling
            - overwatcher auto-tier gating

            ## Upstream this wave builds on

            - Wave 1: attestation
            - Wave 2: the dial
            - Wave 3: classify-then-act
            """;

        // 2 sub-headings (##) + 6 bullets = 8; the level-1 `#` title and the `>` blockquote are excluded.
        Assert.Equal(8, WaveBreakdownInvoker.EstimateBriefSignalCount(brief));
    }

    [Fact]
    public void EstimateBriefSignalCount_CountsNumberedAndAsteriskItems()
    {
        string brief = "1. first\n2) second\n3. third\n* star bullet\n- dash bullet\n";
        Assert.Equal(5, WaveBreakdownInvoker.EstimateBriefSignalCount(brief));
    }

    [Fact]
    public void EstimateBriefSignalCount_IgnoresProseAndMarkerLikeTextMidLine()
    {
        // A "-" or "1." that is NOT a list marker (mid-sentence) must not be miscounted.
        string brief = "This is prose with a - dash and 3.14 pi.\nAnother prose line about wave-04 work.\n";
        Assert.Equal(0, WaveBreakdownInvoker.EstimateBriefSignalCount(brief));
    }

    // --- Integration of the two: a large-wave brief yields a budget far above the truncating cap ---------

    [Fact]
    public void LargeWaveBrief_YieldsBudget_FarAboveOldTruncatingCap()
    {
        // A brief that enumerates ~11 work items (the #385 wave size) must scale the budget WELL above the
        // old 120 that truncated — the end-to-end guarantee the two pure functions compose into.
        string brief = "## Tasks\n\n" +
            string.Join("\n", Enumerable.Range(1, 11).Select(i => $"- Task {i}: author the thing"));

        int signals = WaveBreakdownInvoker.EstimateBriefSignalCount(brief);
        int budget = WaveBreakdownInvoker.ComputeMaxTurns(signals);

        Assert.Equal(12, signals); // 1 sub-heading + 11 bullets
        Assert.True(budget > 400, $"a large-wave brief must scale ABOVE the base; got {budget}");
        Assert.True(budget > OldTruncatingCap * 4, $"far above the old truncating cap; got {budget}");
    }
}
