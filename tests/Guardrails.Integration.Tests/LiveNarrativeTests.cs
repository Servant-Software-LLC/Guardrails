using Guardrails.Cli.Ui;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Design 37 §7.3 — the PURE seams of the live narrative pane (#372). None of these needs a clock, a timer
/// or a terminal: the whole scrollback decision (§4.2), the budget arithmetic (§4.3) and the coalescing rule
/// (§4.5) are testable as data, which is the point of extracting <see cref="LiveNarrative"/> at all.
/// The rendered-frame half of the fix lives in <see cref="LiveNarrativeCompositeTests"/>.
/// </summary>
public sealed class LiveNarrativeTests
{
    private static NarrativeEntry Plain(string markup) => new(markup, null, 1);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // BudgetFor — §4.3
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, LiveNarrative.DefaultBudget)]
    [InlineData(100, LiveNarrative.DefaultBudget)]
    [InlineData(80, LiveNarrative.DefaultBudget)]
    [InlineData(60, LiveNarrative.DefaultBudget)]  // the boundary is inclusive on the wide side
    [InlineData(59, LiveNarrative.NarrowBudget)]
    [InlineData(56, LiveNarrative.NarrowBudget)]   // §5.5's worked narrow example
    [InlineData(1, LiveNarrative.NarrowBudget)]
    public void BudgetFor_HalvesBelowSixtyColumns(int width, int expected) =>
        Assert.Equal(expected, LiveNarrative.BudgetFor(width));

    [Fact]
    public void Budget_IsEightAndFour_TheNumbersTheArithmeticInSection43Depends_On()
    {
        // Not round numbers: 8 is "the worst same-instant burst is 6, leave 2-3 of headroom so no burst is
        // ever elided mid-burst"; 4 is 8 halved because a sub-60-column console wraps each entry to two rows.
        Assert.Equal(8, LiveNarrative.DefaultBudget);
        Assert.Equal(4, LiveNarrative.NarrowBudget);
        Assert.Equal(60, LiveNarrative.NarrowConsoleWidth);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Append — the bound, and the eviction direction
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Append_UnderBudget_KeepsEveryEntryInArrivalOrder()
    {
        IReadOnlyList<NarrativeEntry> buffer = [];
        foreach (string line in new[] { "one", "two", "three" })
        {
            buffer = LiveNarrative.Append(buffer, Plain(line), LiveNarrative.DefaultBudget);
        }

        Assert.Equal(["one", "two", "three"], buffer.Select(e => e.Markup));
    }

    [Fact]
    public void Append_OverBudget_DropsTheOLDEST_KeepingTheMostRecentN()
    {
        IReadOnlyList<NarrativeEntry> buffer = [];
        for (int i = 1; i <= 12; i++)
        {
            buffer = LiveNarrative.Append(buffer, Plain($"line-{i}"), LiveNarrative.DefaultBudget);
        }

        Assert.Equal(LiveNarrative.DefaultBudget, buffer.Count);
        Assert.Equal(
            ["line-5", "line-6", "line-7", "line-8", "line-9", "line-10", "line-11", "line-12"],
            buffer.Select(e => e.Markup));
    }

    [Fact]
    public void Append_NeverMutatesTheInputList_SoTheCallerCanCountEvictions()
    {
        IReadOnlyList<NarrativeEntry> before = [Plain("a"), Plain("b")];
        IReadOnlyList<NarrativeEntry> after = LiveNarrative.Append(before, Plain("c"), budget: 2);

        Assert.Equal(2, before.Count);
        Assert.Equal(["a", "b"], before.Select(e => e.Markup));
        Assert.Equal(["b", "c"], after.Select(e => e.Markup));
    }

    [Fact]
    public void Append_WhenTheBudgetSHRANK_TrimsDownToTheNewBudget()
    {
        // The operator narrowed the terminal mid-run: BudgetFor now returns 4, and the 8 already-buffered
        // entries must come down to 4 rather than sitting over budget until eight more events fire.
        IReadOnlyList<NarrativeEntry> buffer = [];
        for (int i = 1; i <= 8; i++)
        {
            buffer = LiveNarrative.Append(buffer, Plain($"line-{i}"), LiveNarrative.DefaultBudget);
        }

        buffer = LiveNarrative.Append(buffer, Plain("line-9"), LiveNarrative.NarrowBudget);

        Assert.Equal(["line-6", "line-7", "line-8", "line-9"], buffer.Select(e => e.Markup));
    }

    [Fact]
    public void Append_WithANonPositiveBudget_EmptiesTheBuffer_RatherThanThrowing()
    {
        IReadOnlyList<NarrativeEntry> buffer = [Plain("a")];
        Assert.Empty(LiveNarrative.Append(buffer, Plain("b"), budget: 0));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Coalescing — §4.5
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Append_Coalescing_ReplacesINPLACE_AndDoesNotMoveTheEntryToTheBottom()
    {
        // A line that jumps to the bottom every time it recurs is more distracting than the information is
        // worth (§4.5), so the fold is positional as well as textual.
        IReadOnlyList<NarrativeEntry> buffer =
        [
            new("advisory (1)", LiveNarrative.VerifierAdvisoryKey, 1),
            Plain("Wave wave-01: completed"),
            Plain("Wave 2/2: wave-02 — 5 task(s)")
        ];

        IReadOnlyList<NarrativeEntry> after = LiveNarrative.Append(
            buffer,
            new NarrativeEntry("advisory (2)", LiveNarrative.VerifierAdvisoryKey, 2),
            LiveNarrative.DefaultBudget);

        Assert.Equal(3, after.Count);
        Assert.Equal("advisory (2)", after[0].Markup);
        Assert.Equal(2, after[0].Count);
        Assert.Equal("Wave 2/2: wave-02 — 5 task(s)", after[2].Markup);
    }

    [Fact]
    public void Append_Coalescing_HoldsTheBufferFlat_SoATwentyFourTaskAdvisoryBurstCannotEvictThePane()
    {
        // The dependency §4.3 states explicitly: without coalescing, 8 is the wrong number and the design
        // does not hold. This is that claim, asserted.
        IReadOnlyList<NarrativeEntry> buffer = [Plain("Wave 1/2: wave-01 — 3 task(s)")];
        for (int i = 1; i <= 24; i++)
        {
            buffer = LiveNarrative.Append(
                buffer,
                new NarrativeEntry($"advisory ({i})", LiveNarrative.VerifierAdvisoryKey, i),
                LiveNarrative.DefaultBudget);
        }

        Assert.Equal(2, buffer.Count);
        Assert.Equal("Wave 1/2: wave-01 — 3 task(s)", buffer[0].Markup); // NOT evicted
        Assert.Equal(24, buffer[1].Count);
    }

    [Fact]
    public void Append_DifferentKeys_DoNotFoldIntoEachOther()
    {
        IReadOnlyList<NarrativeEntry> buffer = [];
        buffer = LiveNarrative.Append(
            buffer, new NarrativeEntry("v", LiveNarrative.VerifierAdvisoryKey, 1), LiveNarrative.DefaultBudget);
        buffer = LiveNarrative.Append(
            buffer, new NarrativeEntry("o", LiveNarrative.OverwatchNoVerdictKey, 1), LiveNarrative.DefaultBudget);
        buffer = LiveNarrative.Append(
            buffer, new NarrativeEntry("m", LiveNarrative.ModelMismatchKey, 1), LiveNarrative.DefaultBudget);

        Assert.Equal(["v", "o", "m"], buffer.Select(e => e.Markup));
    }

    [Fact]
    public void Append_KeylessEntries_NEVERCoalesce_BecauseEachIsADistinctEvent()
    {
        IReadOnlyList<NarrativeEntry> buffer = [];
        buffer = LiveNarrative.Append(buffer, Plain("Wave wave-01: completed"), LiveNarrative.DefaultBudget);
        buffer = LiveNarrative.Append(buffer, Plain("Wave wave-02: completed"), LiveNarrative.DefaultBudget);

        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void CoalesceIndexOf_IsMinusOneForANullKey_AndForAnAbsentKey()
    {
        IReadOnlyList<NarrativeEntry> buffer =
            [Plain("wave"), new("advisory", LiveNarrative.VerifierAdvisoryKey, 1)];

        Assert.Equal(-1, LiveNarrative.CoalesceIndexOf(buffer, null));
        Assert.Equal(-1, LiveNarrative.CoalesceIndexOf(buffer, LiveNarrative.ModelMismatchKey));
        Assert.Equal(1, LiveNarrative.CoalesceIndexOf(buffer, LiveNarrative.VerifierAdvisoryKey));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Render — the pane, and the elision line (§5.4)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_WithNothingElided_IsExactlyTheEntries_NoHeaderNoFooter()
    {
        IReadOnlyList<string> lines = LiveNarrative.Render(
            [Plain("one"), Plain("two")], elidedCount: 0, planDirectory: "docs/plans/x", runId: "run-1");

        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public void Render_Empty_IsEmpty_SoTheCompositeCanFallBackToTheBareTable()
    {
        Assert.Empty(LiveNarrative.Render([], elidedCount: 0, planDirectory: null, runId: null));
    }

    [Fact]
    public void Render_WithElisions_LeadsWithTheAttachReplayPointer()
    {
        IReadOnlyList<string> lines = LiveNarrative.Render(
            [Plain("Wave 5/6: wave-05-hardening — 7 task(s)")],
            elidedCount: 14,
            planDirectory: "docs/plans/model-tiering-stage-2",
            runId: "2026-09-05T09-14-02Z-a41c");

        Assert.Equal(2, lines.Count);
        Assert.Equal(
            "[grey]… 14 earlier lines — replay with: guardrails attach docs/plans/model-tiering-stage-2[/]",
            lines[0]);
    }

    [Fact]
    public void Render_WithNoPlanDirectory_DegradesToNamingTheObserverJsonl()
    {
        IReadOnlyList<string> lines = LiveNarrative.Render(
            [Plain("x")], elidedCount: 14, planDirectory: null, runId: "2026-09-05T09-14-02Z-a41c");

        Assert.Equal(
            "[grey]… 14 earlier lines — see logs/2026-09-05T09-14-02Z-a41c/observer.jsonl[/]", lines[0]);
    }

    [Fact]
    public void Render_WithNeitherPlanDirectoryNorRunId_StatesTheCountAlone_NotAPathThatMayNotExist()
    {
        IReadOnlyList<string> lines =
            LiveNarrative.Render([Plain("x")], elidedCount: 3, planDirectory: null, runId: null);

        Assert.Equal("[grey]… 3 earlier lines[/]", lines[0]);
    }

    [Fact]
    public void Render_ElisionLine_IsSingularForOne()
    {
        IReadOnlyList<string> lines =
            LiveNarrative.Render([Plain("x")], elidedCount: 1, planDirectory: null, runId: null);

        Assert.Equal("[grey]… 1 earlier line[/]", lines[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The three coalesced wordings (§4.5) — singular stays byte-identical to the pre-#372 line.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifierAdvisoryLine_Singular_IsTheLineThisSurfacePrintedBefore()
    {
        Assert.Equal(
            "[yellow]verifier advisory[/] [grey]05-implement[/]: judge 'meets-spec' has no verifier condition",
            LiveRunObserver.VerifierAdvisoryLine(1, "05-implement", "judge 'meets-spec' has no verifier condition"));
    }

    [Fact]
    public void VerifierAdvisoryLine_Counted_NamesTheTotalAndTheLatest()
    {
        Assert.Equal(
            "[yellow]verifier advisory[/] — 7 task(s), latest [grey]wave-02-consumers/05-implement[/]: "
            + "judge 'meets-spec' has no verifier condition",
            LiveRunObserver.VerifierAdvisoryLine(
                7, "wave-02-consumers/05-implement", "judge 'meets-spec' has no verifier condition"));
    }

    [Fact]
    public void OverwatchNoVerdictLine_SingularAndCounted()
    {
        Assert.Equal(
            "[yellow]overwatch: no verdict[/] [grey]03-wire[/] — model returned no JSON block",
            LiveRunObserver.OverwatchNoVerdictLine(1, "03-wire", "model returned no JSON block"));

        Assert.Equal(
            "[yellow]overwatch: no verdict[/] — 4 task(s), latest [grey]wave-02-consumers/03-wire[/]: "
            + "model returned no JSON block",
            LiveRunObserver.OverwatchNoVerdictLine(
                4, "wave-02-consumers/03-wire", "model returned no JSON block"));
    }

    [Fact]
    public void ModelMismatchLine_SingularAndCounted_BothCarryTheSharedAttemptModelSummaryWording()
    {
        Assert.Equal(
            "[yellow]model[/] [grey]02-implement[/] attempt 1: "
            + "[yellow]claude-sonnet-4-5 — MISMATCH: the route requested claude-opus-4-1[/]",
            LiveRunObserver.ModelMismatchLine(1, "02-implement", 1, "claude-sonnet-4-5", "claude-opus-4-1"));

        Assert.Equal(
            "[yellow]model MISMATCH[/] — 3 attempt(s), latest [grey]wave-01/02-implement[/]: "
            + "[yellow]claude-sonnet-4-5 — MISMATCH: the route requested claude-opus-4-1[/]",
            LiveRunObserver.ModelMismatchLine(
                3, "wave-01/02-implement", 2, "claude-sonnet-4-5", "claude-opus-4-1"));
    }

    [Fact]
    public void CoalescedWordings_EscapeHarnessStrings_SoABracketIsShownNotInterpreted()
    {
        Assert.Contains("[[meets-spec]]", LiveRunObserver.VerifierAdvisoryLine(1, "t", "judge [meets-spec]"));
        Assert.Contains("[[x]]", LiveRunObserver.OverwatchNoVerdictLine(2, "t", "reason [x]"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // AttemptDetailCell — §4.4 #1, the emitter that contributed ~30 of the ~60 corrupting lines.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttemptDetailCell_OnSuccess_IsNull_SoNothingIsWritten()
    {
        // TaskFinished's `succeeded` status arrives milliseconds later and would overwrite it anyway.
        Assert.Null(LiveRunObserver.AttemptDetailCell(AttemptOutcome.Succeeded, 1, null));
        Assert.Null(LiveRunObserver.AttemptDetailCell(AttemptOutcome.Succeeded, 3, "[link=x]view log[/]"));
    }

    [Theory]
    [InlineData(AttemptOutcome.GuardrailFailed, "attempt 1 GuardrailFailed")]
    [InlineData(AttemptOutcome.ActionFailed, "attempt 1 ActionFailed")]
    [InlineData(AttemptOutcome.Timeout, "attempt 1 Timeout")]
    public void AttemptDetailCell_OnFailure_UsesAttemptOutcomesOwnWords(AttemptOutcome outcome, string expected) =>
        Assert.Equal(expected, LiveRunObserver.AttemptDetailCell(outcome, 1, null));

    [Fact]
    public void AttemptDetailCell_AppendsTheLiveLogLinkWhenOneIsWired()
    {
        // §5.1's retry row: `retry 2/3 1:12 │ attempt 1 GuardrailFailed · view log`.
        Assert.Equal(
            "attempt 1 GuardrailFailed · [link=http://x/1]view log[/]",
            LiveRunObserver.AttemptDetailCell(
                AttemptOutcome.GuardrailFailed, 1, "[link=http://x/1]view log[/]"));
    }
}
