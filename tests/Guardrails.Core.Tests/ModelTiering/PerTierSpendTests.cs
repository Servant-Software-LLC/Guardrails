using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The #230-lite PER-TIER SPEND line (DoR §9.3) — "the single most important v1 deliverable after the
/// routing itself: it is the evidence base for whether the deferred subsystems (probes, ladder, steering)
/// are ever worth building". <see cref="JournalTierSpend"/> aggregates the SAME journal
/// <see cref="JournalCost"/> already totals, split by the rung each attempt resolved on
/// (<c>provenance.tier</c>), and renders §9.3's worked example
/// <c>"hard: 42k tok / $3.12 · easy: 180k tok / $0"</c> — ascending here, so the line is stable.
///
/// <para><b>Why tokens at all.</b> A costless local provider or a flat-rate subscription honestly reports
/// <c>$0</c> for a run that did enormous work. Spend alone therefore cannot be the evidence base, so a rung
/// DEGRADES to tokens-only where no cost was reported rather than printing <c>$0.00</c> — a number the
/// runner never said.</para>
///
/// <para><b>INVARIANT 7 is why this class exists as its own unit, and why half these tests assert on the
/// rendered TEXT.</b> §9.3 is stricter than "add a per-tier section": on a tiering-inactive run the summary
/// is NOTHING — not an empty section, not a header with no rows, and above all not an <c>untiered:</c>
/// bucket. A naive aggregator that groups by <c>provenance.tier ?? "untiered"</c> passes every structural
/// assertion about the tiered rungs while silently appending a new section to EVERY existing user's run
/// report. That regression is only visible in the string, so
/// <see cref="MixedTieredAndUntieredAttempts_RenderNoUntieredBucket"/> and
/// <see cref="TieringInactiveRun_RendersExactlyTodaysCostLine"/> assert on the string, negatively.</para>
///
/// <para><b>TDD red (#155).</b> Every test here calls <see cref="JournalTierSpend.Summarize"/> or
/// <see cref="JournalTierSpend.Render"/>, both of which throw <see cref="NotImplementedException"/> until
/// <c>wave-02-attempt-launch-wiring/11</c> fills them — so the whole file is red, and none of it can be
/// green by coincidence with a stub's default.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class PerTierSpendTests
{
    // --- 1. aggregation ------------------------------------------------------------------------

    /// <summary>
    /// Attempts across several tasks and several rungs sum PER RUNG — cost and both token counts. The
    /// fixture is §9.3's own worked example: hard is two attempts of two different tasks that add up to
    /// $3.12 / 42k tokens, easy is 180k tokens at a recorded $0.
    /// </summary>
    [Fact]
    public void AttemptsAcrossTasksAndRungs_SumPerRung()
    {
        IReadOnlyList<TierSpend> summary = SummaryOf(WorkedExample());

        TierSpend hard = Rung(summary, ActionTiers.Hard);
        Assert.Equal(3.12m, hard.CostUsd);      // 2.00 + 1.12, from two DIFFERENT tasks
        Assert.Equal(36_000L, hard.InputTokens);
        Assert.Equal(6_000L, hard.OutputTokens);
        Assert.Equal(42_000L, hard.TotalTokens);

        TierSpend easy = Rung(summary, ActionTiers.Easy);
        Assert.Equal(0m, easy.CostUsd);
        Assert.Equal(180_000L, easy.TotalTokens);

        TierSpend medium = Rung(summary, ActionTiers.Medium);
        Assert.Equal(0.50m, medium.CostUsd);
        Assert.Equal(10_000L, medium.TotalTokens);
    }

    /// <summary>
    /// Two attempts of the SAME task at the same rung both count. Resolution runs per ATTEMPT — a retry
    /// resolves and spends again — so an aggregator that folds a task down to its final attempt (or to one
    /// row per task) under-reports the rung by exactly the retry cost, which is the spend the measurement
    /// most needs to see.
    /// </summary>
    [Fact]
    public void TwoAttemptsOfTheSameTaskAtTheSameRung_BothCount()
    {
        JournalDocument document = Document(
            Entry(
                "01-flaky",
                Tiered(ActionTiers.Medium, cost: 0.20m, input: 1_000, output: 500, attempt: 1),
                Tiered(ActionTiers.Medium, cost: 0.30m, input: 2_000, output: 500, attempt: 2)));

        IReadOnlyList<TierSpend> summary = SummaryOf(document);

        TierSpend medium = Assert.Single(summary);
        Assert.Equal(ActionTiers.Medium, medium.Tier);
        Assert.Equal(0.50m, medium.CostUsd);
        Assert.Equal(3_000L, medium.InputTokens);
        Assert.Equal(1_000L, medium.OutputTokens);
        Assert.Equal(4_000L, medium.TotalTokens);
    }

    // --- 2. rung ordering ----------------------------------------------------------------------

    /// <summary>
    /// Rungs come back ascending — <see cref="ActionTiers.All"/>'s order — not in dictionary/first-appearance
    /// order, so the line does not shuffle between runs of the same plan. The fixture inserts hard FIRST
    /// precisely so "whatever order the journal happened to enumerate in" is a different answer.
    ///
    /// <para>The expectation is spelled with the LITERAL wire tokens rather than the
    /// <see cref="ActionTiers"/> constants: the ordering contract is <c>easy</c> then <c>medium</c> then
    /// <c>hard</c>, and writing it in terms of the same constants the implementation reads would let a
    /// re-ordered (or re-spelled) <see cref="ActionTiers.All"/> move both sides together and stay green. The
    /// second assertion then pins that the implementation's own order really is that list.</para>
    /// </summary>
    [Fact]
    public void Rungs_AreOrderedAscending_NotByFirstAppearance()
    {
        IReadOnlyList<TierSpend> summary = SummaryOf(WorkedExample());

        Assert.Equal(new[] { "easy", "medium", "hard" }, summary.Select(s => s.Tier));
        Assert.Equal(ActionTiers.All.Where(t => summary.Any(s => s.Tier == t)), summary.Select(s => s.Tier));
    }

    /// <summary>
    /// A rung nothing resolved on is ABSENT, not an empty row: the ascending order is a filter over
    /// <see cref="ActionTiers.All"/>, not a fixed three-row table.
    /// </summary>
    [Fact]
    public void RungWithNoAttempts_IsAbsent_NotAnEmptyRow()
    {
        JournalDocument document = Document(
            Entry("01-hard", Tiered(ActionTiers.Hard, cost: 1.00m, input: 10_000, output: 2_000)),
            Entry("02-easy", Tiered(ActionTiers.Easy, cost: 0.05m, input: 4_000, output: 1_000)));

        IReadOnlyList<TierSpend> summary = SummaryOf(document);

        Assert.Equal(new[] { "easy", "hard" }, summary.Select(s => s.Tier));
        Assert.DoesNotContain("medium", RenderOf(document), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rendered line IS §9.3's worked example, with the rungs ascending and separated by <c>" · "</c>.
    /// Asserted by segment rather than by whole-string equality so the money's decimal precision stays the
    /// renderer's choice (the doc recommends <c>F4</c>, matching the <c>Total prompt cost</c> line above it).
    /// </summary>
    [Fact]
    public void RenderedLine_IsTheWorkedExample_Ascending()
    {
        string line = RenderOf(WorkedExample());

        Assert.Contains("easy: 180k tok / $0", line, StringComparison.Ordinal);
        Assert.Contains("medium: 10k tok / $0.50", line, StringComparison.Ordinal);
        Assert.Contains("hard: 42k tok / $3.12", line, StringComparison.Ordinal);
        Assert.Contains(" · ", line, StringComparison.Ordinal);

        Assert.True(
            line.IndexOf("easy:", StringComparison.Ordinal) < line.IndexOf("medium:", StringComparison.Ordinal)
                && line.IndexOf("medium:", StringComparison.Ordinal) < line.IndexOf("hard:", StringComparison.Ordinal),
            $"rungs must read ascending (easy, medium, hard); got: {line}");
    }

    /// <summary>
    /// Each segment is labelled with the rung VERBATIM as the journal recorded it — the lowercase wire token
    /// <c>"easy"</c> / <c>"medium"</c> / <c>"hard"</c>, not a prettified display name. The operator reads
    /// this line next to a <c>tier:</c> field in the manifest and a <c>"tier"</c> in the journal; three
    /// spellings of one concept is how a measurement stops being greppable.
    /// </summary>
    [Fact]
    public void RungLabels_AreTheWireTokensVerbatim_NotDisplayNames()
    {
        string line = RenderOf(WorkedExample());

        Assert.StartsWith("easy:", Segment(line, "easy"), StringComparison.Ordinal);
        Assert.StartsWith("medium:", Segment(line, "medium"), StringComparison.Ordinal);
        Assert.StartsWith("hard:", Segment(line, "hard"), StringComparison.Ordinal);

        // ... and not a title-cased or otherwise re-spelled variant of any of them.
        Assert.DoesNotContain("Easy", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Medium", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Hard", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Token volume reads as an exact count below a thousand and in whole thousands above it — "42k tok",
    /// not "42000 tok" and not "0.64k tok".
    /// </summary>
    [Fact]
    public void TokenVolume_ReadsAsAnExactCountBelowAThousand_AndInThousandsAbove()
    {
        JournalDocument document = Document(
            Entry("01-tiny", Tiered(ActionTiers.Easy, input: 500, output: 140)),
            Entry("02-big", Tiered(ActionTiers.Hard, input: 36_000, output: 6_000)));

        string line = RenderOf(document);

        Assert.Contains("easy: 640 tok", line, StringComparison.Ordinal);
        Assert.Contains("hard: 42k tok", line, StringComparison.Ordinal);
        Assert.DoesNotContain("640k", line, StringComparison.Ordinal);
        Assert.DoesNotContain("42000", line, StringComparison.Ordinal);
    }

    // --- 3. tokens-only degradation ------------------------------------------------------------

    /// <summary>
    /// A rung whose attempts reported TOKENS but no cost renders its volume and omits the money entirely —
    /// NOT <c>$0.00</c>, which asserts a fact the runner never reported. This is the whole reason the tokens
    /// surface exists: a costless local provider still shows how much work it did. The converse leg — cost
    /// recorded but no usage block — renders the money alone.
    /// </summary>
    [Fact]
    public void RungWithTokensButNoCost_RendersVolumeOnly_AndTheConverseRendersMoneyOnly()
    {
        JournalDocument document = Document(
            // a costless local endpoint: usage reported, no cost ever recorded
            Entry("01-local", Tiered(ActionTiers.Easy, cost: null, input: 150_000, output: 30_000)),
            // a runner that bills but reports no usage block
            Entry("02-billed", Tiered(ActionTiers.Hard, cost: 3.12m, input: null, output: null)));

        IReadOnlyList<TierSpend> summary = SummaryOf(document);

        TierSpend easy = Rung(summary, ActionTiers.Easy);
        Assert.Null(easy.CostUsd);
        Assert.Equal(180_000L, easy.TotalTokens);

        TierSpend hard = Rung(summary, ActionTiers.Hard);
        Assert.Equal(3.12m, hard.CostUsd);
        Assert.Null(hard.InputTokens);
        Assert.Null(hard.OutputTokens);
        Assert.Null(hard.TotalTokens);

        string line = RenderOf(document);

        string easySegment = Segment(line, "easy");
        Assert.Contains("180k tok", easySegment, StringComparison.Ordinal);
        Assert.DoesNotContain("$", easySegment, StringComparison.Ordinal);

        string hardSegment = Segment(line, "hard");
        Assert.Contains("$3.12", hardSegment, StringComparison.Ordinal);
        Assert.DoesNotContain("tok", hardSegment, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RECORDED $0 is a reported fact and prints; an ABSENT cost is not and does not. The distinction is
    /// the one <see cref="JournalCost.Total"/> already draws (a $0 attempt makes the total non-null), carried
    /// down to the rung — and it is what stops "we measured zero" and "we measured nothing" from being the
    /// same line.
    /// </summary>
    [Fact]
    public void RecordedZeroCost_PrintsTheMoney_WhileAnAbsentCostOmitsIt()
    {
        JournalDocument document = Document(
            Entry("01-free-tier", Tiered(ActionTiers.Easy, cost: 0m, input: 150_000, output: 30_000)),
            Entry("02-unreported", Tiered(ActionTiers.Hard, cost: null, input: 36_000, output: 6_000)));

        IReadOnlyList<TierSpend> summary = SummaryOf(document);
        Assert.Equal(0m, Rung(summary, ActionTiers.Easy).CostUsd);
        Assert.Null(Rung(summary, ActionTiers.Hard).CostUsd);

        string line = RenderOf(document);
        Assert.Contains("$0", Segment(line, "easy"), StringComparison.Ordinal);
        Assert.DoesNotContain("$", Segment(line, "hard"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A rung that ROUTED but reported neither cost nor tokens is still named — routing happened, and that is
    /// itself evidence — but the segment invents no zero of either kind.
    /// </summary>
    [Fact]
    public void RungThatRoutedButReportedNothing_IsNamedWithoutInventingAZero()
    {
        JournalDocument document = Document(
            Entry("01-easy", Tiered(ActionTiers.Easy, cost: 0.10m, input: 4_000, output: 1_000)),
            // resolved a route, then died before the runner reported any spend at all
            Entry("02-hard", Tiered(ActionTiers.Hard)));

        TierSpend hard = Rung(SummaryOf(document), ActionTiers.Hard);
        Assert.Null(hard.CostUsd);
        Assert.Null(hard.TotalTokens);

        string segment = Segment(RenderOf(document), "hard");
        Assert.Contains("no spend reported", segment, StringComparison.Ordinal);
        Assert.DoesNotContain("$", segment, StringComparison.Ordinal);
        Assert.DoesNotContain("tok", segment, StringComparison.Ordinal);
        Assert.DoesNotContain("0", segment, StringComparison.Ordinal);
    }

    // --- 4. INVARIANT 7 — the suppression rule -------------------------------------------------

    /// <summary>
    /// A journal with ZERO tiered attempts summarizes to null — the first-class "there is nothing to report"
    /// value, so the caller can spell the wiring <c>if (Render(document) is { } line)</c> instead of testing
    /// an empty string. Every attempt in the fixture is one an existing user's run.json is full of: a
    /// legacy-fallback route (D30 — no <c>tier</c>, no <c>tierSource</c>), a pin (D31 — a <c>tierSource</c>
    /// of <c>override</c> but still no rung), a script action, and an attempt written before tiering existed.
    /// </summary>
    [Fact]
    public void JournalWithZeroTieredAttempts_SummarizesToNull()
    {
        JournalDocument document = TieringInactiveRun();

        Assert.Null(JournalTierSpend.Summarize(document));
        Assert.Null(JournalTierSpend.Render(document));
    }

    /// <summary>
    /// The TEXT half of the rule, and the assertion that a structural check cannot make: on a
    /// tiering-inactive run the operator's summary is EXACTLY today's cost line and not one character more —
    /// no per-tier section, no header with no rows, no <c>untiered:</c> bucket. This is the shape every
    /// already-shipped run has, so anything else is a regression the day this wave lands.
    ///
    /// <para>The check is made against the composed summary rather than against
    /// <see cref="JournalTierSpend.Render"/>'s return value alone, because that is where the rule is
    /// actually observable: an empty-string return is non-null, so it satisfies task 11's
    /// <c>is { } line</c> and prints a labelled but empty section — a bug an <c>Assert.Null</c> on the
    /// return value catches only by accident of the <c>null</c> convention, and one an operator would see
    /// immediately.</para>
    /// </summary>
    [Fact]
    public void TieringInactiveRun_RendersExactlyTodaysCostLine()
    {
        JournalDocument document = TieringInactiveRun();

        // The run still reports the total it reports today: 1.50 + 0.75 + 0.25.
        decimal? total = JournalCost.Total(document);
        Assert.Equal(2.50m, total);

        // Task 11's wiring, verbatim — the whole point of a nullable return is that this is spellable.
        string summary = $"Total prompt cost: ${total:F4}"
            + (JournalTierSpend.Render(document) is { } line ? $"\nPer-tier spend: {line}" : string.Empty);

        Assert.Equal("Total prompt cost: $2.5000", summary);
        Assert.DoesNotContain("Per-tier spend", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("untiered", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("easy", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("medium", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hard", summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A journal MIXING tiered and untiered attempts reports the tiered rungs and drops the rest on the
    /// floor — the untiered spend is not re-homed into any rung, and it does not become a bucket of its own.
    /// </summary>
    [Fact]
    public void MixedTieredAndUntieredAttempts_ReportOnlyTheTieredRungs()
    {
        JournalDocument document = MixedTieredAndUntiered();

        IReadOnlyList<TierSpend> summary = SummaryOf(document);

        Assert.Equal(new[] { "easy", "hard" }, summary.Select(s => s.Tier));
        Assert.Equal(0.10m, Rung(summary, ActionTiers.Easy).CostUsd);
        Assert.Equal(2.00m, Rung(summary, ActionTiers.Hard).CostUsd);

        // The $14.99 and 570k tokens of untiered spend land in NO rung — not spread, not appended.
        Assert.Equal(2.10m, summary.Sum(s => s.CostUsd ?? 0m));
        Assert.Equal(45_000L, summary.Sum(s => s.TotalTokens ?? 0L));
    }

    /// <summary>
    /// The same mix, asserted on the rendered TEXT with the negative assertion that carries this test:
    /// the string <c>untiered</c> — or any of its likely spellings — never appears. Grouping by
    /// <c>provenance.tier ?? "untiered"</c> is the single most likely way this wave breaks Invariant 7, and
    /// it is INVISIBLE to a structural assertion that only inspects the tiered rungs.
    /// </summary>
    [Fact]
    public void MixedTieredAndUntieredAttempts_RenderNoUntieredBucket()
    {
        string line = RenderOf(MixedTieredAndUntiered());

        Assert.Contains("easy:", line, StringComparison.Ordinal);
        Assert.Contains("hard:", line, StringComparison.Ordinal);

        Assert.DoesNotContain("untiered", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("none", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", line, StringComparison.OrdinalIgnoreCase);

        // Nor may the untiered spend appear anonymously, folded into a rung or trailing the line.
        Assert.DoesNotContain("$9.99", line, StringComparison.Ordinal);
        Assert.DoesNotContain("$5.00", line, StringComparison.Ordinal);
        Assert.DoesNotContain("$14.99", line, StringComparison.Ordinal);
        Assert.DoesNotContain("570k", line, StringComparison.Ordinal);
        Assert.DoesNotContain("615k", line, StringComparison.Ordinal);
    }

    // --- 5. overhead spend is not a rung -------------------------------------------------------

    /// <summary>
    /// <see cref="JournalDocument.OverheadCostUsd"/> — the overwatcher's diagnose prompts, the AI-merge
    /// worker, the terminal needs-human triage (SSOT §9.2, #269/#314) — is not a task attempt and resolved no
    /// rung, so it must not silently land in one. <see cref="JournalCost.Total"/> keeps folding it into the
    /// run total exactly as it does today: the two aggregations answer different questions, and this asserts
    /// the existing one is unaffected by anything the per-tier line adds.
    /// </summary>
    [Fact]
    public void OverheadSpend_LandsInNoRung_AndLeavesTheExistingTotalUnchanged()
    {
        JournalDocument document = Document(
            Entry("01-hard", Tiered(ActionTiers.Hard, cost: 2.00m, input: 35_000, output: 5_000)),
            Entry("02-easy", Tiered(ActionTiers.Easy, cost: 0.10m, input: 4_000, output: 1_000)))
            with { OverheadCostUsd = 7.00m };

        IReadOnlyList<TierSpend> summary = SummaryOf(document);

        Assert.Equal(new[] { "easy", "hard" }, summary.Select(s => s.Tier));
        Assert.Equal(0.10m, Rung(summary, ActionTiers.Easy).CostUsd);
        Assert.Equal(2.00m, Rung(summary, ActionTiers.Hard).CostUsd);
        Assert.Equal(2.10m, summary.Sum(s => s.CostUsd ?? 0m));   // attempts only — the $7 is nowhere in here

        string line = RenderOf(document);
        Assert.DoesNotContain("$7.00", line, StringComparison.Ordinal);
        Assert.DoesNotContain("$9.10", line, StringComparison.Ordinal);
        Assert.DoesNotContain("overhead", line, StringComparison.OrdinalIgnoreCase);

        // The shipped run-level total still sees the overhead, unchanged: 2.00 + 0.10 + 7.00.
        Assert.Equal(9.10m, JournalCost.Total(document));
    }

    /// <summary>
    /// The corollary, and the case an existing deterministic-only user actually has: a run whose ONLY prompt
    /// spend was overhead (an AI-merge, a terminal triage) has no rung at all, so there is no per-tier line —
    /// while today's total keeps printing that spend.
    /// </summary>
    [Fact]
    public void OverheadOnlyRun_HasNoRungs_AndStillReportsTodaysTotal()
    {
        JournalDocument document = Document(Entry("01-script", NoProvenance())) with { OverheadCostUsd = 0.42m };

        Assert.Null(JournalTierSpend.Summarize(document));
        Assert.Null(JournalTierSpend.Render(document));
        Assert.Equal(0.42m, JournalCost.Total(document));
    }

    // --- fixtures ------------------------------------------------------------------------------

    /// <summary>
    /// DoR §9.3's worked example, built from four tasks and deliberately inserted HARD FIRST so that
    /// journal/dictionary order is a different answer from the ascending one:
    /// easy 180k tok / $0 · medium 10k tok / $0.50 · hard 42k tok / $3.12.
    /// </summary>
    private static JournalDocument WorkedExample() => Document(
        Entry("04-design", Tiered(ActionTiers.Hard, cost: 2.00m, input: 30_000, output: 5_000)),
        Entry("03-cross-cutting", Tiered(ActionTiers.Hard, cost: 1.12m, input: 6_000, output: 1_000)),
        Entry("01-rename", Tiered(ActionTiers.Easy, cost: 0m, input: 150_000, output: 30_000)),
        Entry("02-wire", Tiered(ActionTiers.Medium, cost: 0.50m, input: 9_000, output: 1_000)));

    /// <summary>
    /// Every shape of attempt that carries NO rung: a legacy-fallback route, a pin, a script action, and an
    /// attempt from a journal written before model tiering. This is what an already-shipped run looks like.
    /// </summary>
    private static JournalDocument TieringInactiveRun() => Document(
        Entry("01-legacy", Legacy(cost: 1.50m, input: 80_000, output: 20_000)),
        Entry("02-pinned", Pinned(cost: 0.75m, input: 10_000, output: 2_000)),
        Entry("03-script", NoProvenance()),
        Entry("04-older-journal", NoProvenance(cost: 0.25m)));

    /// <summary>
    /// Tiered and untiered attempts in one journal: $2.10 / 45k tokens across two rungs, alongside $14.99 /
    /// 570k tokens that resolved no rung at all.
    /// </summary>
    private static JournalDocument MixedTieredAndUntiered() => Document(
        Entry("01-tagged-hard", Tiered(ActionTiers.Hard, cost: 2.00m, input: 35_000, output: 5_000)),
        Entry("02-tagged-easy", Tiered(ActionTiers.Easy, cost: 0.10m, input: 4_000, output: 1_000)),
        Entry("03-legacy", Legacy(cost: 9.99m, input: 400_000, output: 100_000)),
        Entry("04-pinned", Pinned(cost: 5.00m, input: 60_000, output: 10_000)),
        Entry("05-script", NoProvenance()));

    // --- builders ------------------------------------------------------------------------------

    /// <summary>An attempt that resolved through routing and recorded the rung it served on.</summary>
    private static AttemptRecord Tiered(
        string tier,
        decimal? cost = null,
        int? input = null,
        int? output = null,
        int attempt = 1) =>
        Attempt(attempt, cost, input, output) with
        {
            Provenance = new AttemptProvenance
            {
                Runner = "routed-" + tier,
                Kind = "claude",
                Model = "model-" + tier,
                Tier = tier,
                TierSource = TierSource.Task
            }
        };

    /// <summary>
    /// The DoR §6.1 item 3 / D30 legacy-fallback attempt: it ran on the default runner, nothing resolved, so
    /// it records neither <c>tier</c> nor <c>tierSource</c>.
    /// </summary>
    private static AttemptRecord Legacy(decimal? cost = null, int? input = null, int? output = null) =>
        Attempt(1, cost, input, output) with
        {
            Provenance = new AttemptProvenance { Runner = "default", Kind = "claude", Model = "legacy-model" }
        };

    /// <summary>
    /// The D31 pinned attempt: an <c>action.runner</c>/<c>action.model</c> pin bypassed resolution, so it
    /// records a <c>tierSource</c> of <c>override</c> and — the part that matters here — NO rung.
    /// </summary>
    private static AttemptRecord Pinned(decimal? cost = null, int? input = null, int? output = null) =>
        Attempt(1, cost, input, output) with
        {
            Provenance = new AttemptProvenance
            {
                Runner = "pinned",
                Kind = "claude",
                Model = "pinned-model",
                TierSource = TierSource.Override
            }
        };

    /// <summary>A script attempt, or any attempt from a journal written before provenance existed.</summary>
    private static AttemptRecord NoProvenance(decimal? cost = null) =>
        Attempt(1, cost, null, null) with { Provenance = null };

    private static AttemptRecord Attempt(int attempt, decimal? cost, int? input, int? output) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = AttemptOutcome.Succeeded,
        CostUsd = cost,
        Usage = input is null && output is null
            ? null
            : new AttemptUsage { InputTokens = input ?? 0, OutputTokens = output ?? 0 },
        LogDir = $"state/logs/x/attempt-{attempt}"
    };

    private static KeyValuePair<string, TaskJournalEntry> Entry(string id, params AttemptRecord[] attempts) =>
        new(id, new TaskJournalEntry { Status = JournalTaskStatus.Succeeded, Attempts = attempts });

    private static JournalDocument Document(params KeyValuePair<string, TaskJournalEntry>[] entries) => new()
    {
        RunId = "test-run",
        PlanHash = "sha256:test",
        NextMergeSequence = 1,
        Tasks = new Dictionary<string, TaskJournalEntry>(entries, StringComparer.Ordinal)
    };

    // --- assertions ----------------------------------------------------------------------------

    private static IReadOnlyList<TierSpend> SummaryOf(JournalDocument document)
    {
        IReadOnlyList<TierSpend>? summary = JournalTierSpend.Summarize(document);
        Assert.NotNull(summary);
        return summary!;
    }

    private static string RenderOf(JournalDocument document)
    {
        string? line = JournalTierSpend.Render(document);
        Assert.NotNull(line);
        return line!;
    }

    private static TierSpend Rung(IReadOnlyList<TierSpend> summary, string tier)
    {
        TierSpend? rung = summary.SingleOrDefault(s => s.Tier == tier);
        Assert.NotNull(rung);
        return rung!;
    }

    /// <summary>The one rung's slice of the rendered line, so a per-rung negative assertion cannot be
    /// satisfied by a neighbouring rung's text.</summary>
    private static string Segment(string line, string tier)
    {
        string? segment = line
            .Split(" · ", StringSplitOptions.None)
            .SingleOrDefault(s => s.StartsWith(tier + ":", StringComparison.Ordinal));

        Assert.NotNull(segment);
        return segment!;
    }
}
