using System.Globalization;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The #349 MODELS-USED summary line, surface 5 of 5. <see cref="JournalModelsUsed"/> is the sibling of
/// <see cref="JournalTierSpend"/> — the same read over the same journal records, grouped by the model each
/// attempt actually ran on (<c>provenance.model</c>) instead of by the rung it resolved on — and renders
/// segments like <c>"claude-sonnet-5-20260101 ×7 (substituted for claude-opus-5) · claude-opus-5 ×2"</c>.
///
/// <para><b>A summary line asserts a NON-EMPTY QUANTITY, which is a hollow-assertion trap.</b> A check that
/// greps for the label, or that the command exited 0, passes a run that aggregated ZERO models and printed
/// an empty list. So <see cref="RenderedLine_NamesEveryRecordedModel_WithAStrictlyPositiveCount"/> parses the
/// count back OUT of the rendered line and requires it to be strictly positive for every model the journal
/// recorded — not that a line was printed.</para>
///
/// <para><b>The suppression rule is inherited verbatim from <see cref="JournalTierSpend"/>'s Invariant 7</b>,
/// and it is why half of these tests assert on the rendered STRING. A naive aggregator that groups by
/// <c>provenance.model ?? "(none)"</c> satisfies every structural assertion about the real models while
/// silently appending a bucket to every deterministic-only user's run report. That regression is only visible
/// in the string, so <see cref="AttemptsWithoutAModel_AreExcluded_WithNoBucketOfTheirOwn"/> asserts on it
/// negatively, and <see cref="RunWithNoRecordedModel_SummarizesAndRendersNull"/> pins the null (an empty list
/// or an empty string is a different, wrong answer — the caller spells suppression as
/// <c>is { } line</c>).</para>
///
/// <para><b><c>requestedModel</c> is present ONLY on a mismatch</b> — there is no flag beside it, its presence
/// IS the signal — so an aggregation that assumes both keys always exist is the specific wrong answer #349's
/// brief names. <see cref="RequestedModel_PresentOnlyOnMismatch_IsCarriedIntoTheSegment"/> is two-sided in one
/// test for exactly that reason: a renderer that always printed the substitution clause, or never printed it,
/// fails it. There is no <c>resolvedModel</c> key to read — Stage 2 refused one, because two fields claiming
/// the same fact is how they drift.</para>
///
/// <para><b>TDD red.</b> Every test here calls <see cref="JournalModelsUsed.Summarize"/> or
/// <see cref="JournalModelsUsed.Render"/>, both of which throw <see cref="NotImplementedException"/> until
/// <c>wave-04-report-and-cleanup/02-implement-models-used-report</c> fills them — so the whole file is red,
/// and none of it can be green by coincidence with a stub's default.</para>
/// </summary>
[Trait("Category", "ModelTieringStage3")]
public sealed class ModelsUsedSummaryTests
{
    /// <summary>
    /// The four model ids the fixtures use. Deliberately chosen so that NONE is a prefix or substring of
    /// another: every per-model assertion below locates its segment by <c>"&lt;model&gt; ×"</c>, and an id
    /// contained in a neighbour would let one segment satisfy — or defeat — another's assertion.
    /// </summary>
    private const string Sonnet = "claude-sonnet-5-20260101";
    private const string Opus = "claude-opus-5";
    private const string Haiku = "claude-haiku-4-5-20251001";
    private const string Fable = "claude-fable-5";

    /// <summary>The segment separator, matching <see cref="JournalTierSpend.Render"/>'s.</summary>
    private const string Separator = " · ";

    /// <summary>The literal that introduces a segment's attempt count.</summary>
    private const string CountMarker = " ×";

    // --- 1. what is counted --------------------------------------------------------------------

    /// <summary>
    /// Attempts across several tasks and several models count PER MODEL, and every attempt counts
    /// independently — retries included.
    ///
    /// <para>Resolution and launch happen per ATTEMPT, so a retry ran a model again; this is the same rule
    /// <see cref="JournalTierSpend"/> already applies to spend, and for the same reason. An aggregator that
    /// folded a task down to its final attempt (or to one row per task) would report <c>sonnet ×2</c> here
    /// instead of <c>×3</c> — under-counting by exactly the retry, which is the volume this line most needs
    /// to show.</para>
    /// </summary>
    [Fact]
    public void Attempts_AcrossTasksAndRetriesCountPerModel()
    {
        JournalDocument document = Document(
            Entry("01-design", Served(Opus)),
            // ONE task, two attempts, the same model both times: the first attempt failed its guardrail and
            // the retry launched sonnet again. Two contributions, not one.
            Entry("02-wire",
                Served(Sonnet, attempt: 1, outcome: AttemptOutcome.GuardrailFailed),
                Served(Sonnet, attempt: 2)),
            Entry("03-rename", Served(Haiku)),
            Entry("04-doc", Served(Sonnet)));

        IReadOnlyList<ModelUsage> summary = SummaryOf(document);

        Assert.Equal(3, Usage(summary, Sonnet).Attempts);   // 02-wire ×2 (the retry counts) + 04-doc ×1
        Assert.Equal(1, Usage(summary, Opus).Attempts);
        Assert.Equal(1, Usage(summary, Haiku).Attempts);

        // Exactly the three models the journal recorded, and every attempt in the journal accounted for.
        Assert.Equal(3, summary.Count);
        Assert.Equal(5, summary.Sum(u => u.Attempts));

        // The retry is visible in the rendered line too, not just in the structure.
        Assert.Equal(3, RenderedCount(RenderOf(document), Sonnet));
    }

    /// <summary>
    /// An attempt with NO model contributes nothing, and gets no row of its own. Two shapes are in the
    /// fixture, and both are shapes an ordinary <c>run.json</c> is full of: a script attempt (no
    /// <c>provenance</c> block at all) and an attempt whose <c>provenance.model</c> is null.
    ///
    /// <para><b>The string assertions carry this test.</b> An aggregator that groups by
    /// <c>provenance.model ?? "(none)"</c> passes every structural assertion about the real models while
    /// appending a bucket to the report — the Invariant 7 breakage <see cref="JournalTierSpend"/> forbids by
    /// name, and the one that would land on every deterministic-only plan the day this ships. It is INVISIBLE
    /// to a structural check that only inspects the models it expects, so the rendered line is required to
    /// carry exactly ONE segment and none of the likely spellings of an anonymous bucket.</para>
    /// </summary>
    [Fact]
    public void AttemptsWithoutAModel_AreExcluded_WithNoBucketOfTheirOwn()
    {
        JournalDocument document = Document(
            Entry("01-prompt", Served(Sonnet)),
            Entry("02-script", NoProvenance()),
            Entry("03-modelless", ProvenanceWithoutAModel()));

        IReadOnlyList<ModelUsage> summary = SummaryOf(document);

        ModelUsage only = Assert.Single(summary);
        Assert.Equal(Sonnet, only.Model);
        Assert.Equal(1, only.Attempts);
        Assert.Equal(1, summary.Sum(u => u.Attempts));   // the two modelless attempts contributed nothing

        string line = RenderOf(document);

        // ONE segment. Not two, and not one plus a trailing bucket.
        Assert.Equal(Sonnet + CountMarker + "1", Assert.Single(line.Split(Separator, StringSplitOptions.None)));

        // ... and no spelling of an anonymous bucket, anywhere on the line.
        Assert.DoesNotContain("none", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CountMarker + "0", line, StringComparison.Ordinal);

        // NOTE for the implementation: these spellings are forbidden as BUCKET NAMES, not as substrings in
        // general. "(cli default)" is a real recorded model id — the sentinel the harness writes when nothing
        // named a model — and it is rendered verbatim like any other. That is why the load-bearing assertion
        // here is the single-segment equality above, not the word list.
    }

    // --- 2. suppression -----------------------------------------------------------------------

    /// <summary>
    /// A journal of script attempts only — a deterministic plan, which is what most shipped plans are —
    /// summarizes and renders to NULL. Not an empty list, not an empty string, not a label with no segments.
    ///
    /// <para><see cref="Assert.Null(object?)"/> on both is the point: an empty string is non-null, so it
    /// satisfies the caller's <c>is { } line</c> and prints a labelled but empty <c>Models used:</c> line on
    /// every deterministic run — the exact "aggregated zero models and printed an empty list" failure the
    /// wave brief calls out. The null is also what lets the caller spell suppression as a pattern match
    /// rather than as an emptiness test on rendered text, which stays correct the day the renderer grows a
    /// prefix.</para>
    /// </summary>
    [Fact]
    public void RunWithNoRecordedModel_SummarizesAndRendersNull()
    {
        JournalDocument document = Document(
            Entry("01-script", NoProvenance()),
            Entry("02-script", NoProvenance()),
            // a prompt attempt from a journal written before provenance carried a model
            Entry("03-modelless", ProvenanceWithoutAModel()));

        Assert.Null(JournalModelsUsed.Summarize(document));
        Assert.Null(JournalModelsUsed.Render(document));
    }

    // --- 3. the strictly-positive quantity ----------------------------------------------------

    /// <summary>
    /// The wave brief's central requirement: the line NAMES EVERY MODEL the journal recorded, each with its
    /// own STRICTLY POSITIVE count, and no zero count appears anywhere on it.
    ///
    /// <para>The counts are parsed back out of the rendered text rather than read off the structure, because
    /// the failure this guards is a rendering one: a line that named a model with <c>×0</c>, or that dropped a
    /// model it counted, is invisible to a structural assertion and is exactly what "greps for the heading"
    /// certifies. Both directions are pinned — every recorded model is named (nothing dropped) and every
    /// named segment belongs to a recorded model (nothing invented).</para>
    /// </summary>
    [Fact]
    public void RenderedLine_NamesEveryRecordedModel_WithAStrictlyPositiveCount()
    {
        JournalDocument document = MultiModelRun();
        string line = RenderOf(document);

        // Every model the journal recorded is named, with its own strictly positive count.
        foreach ((string model, int expected) in new[] { (Sonnet, 5), (Haiku, 2), (Opus, 2) })
        {
            int rendered = RenderedCount(line, model);
            Assert.True(
                rendered > 0,
                $"'{model}' is rendered with a non-positive count ({rendered}) in: {line}. A models-used "
                + "line asserts a non-empty quantity — a model the journal recorded ran at least once.");
            Assert.Equal(expected, rendered);
        }

        // No zero count anywhere, in any segment — a row that counted nothing is never produced.
        Assert.DoesNotContain(CountMarker + "0", line, StringComparison.Ordinal);

        // Nothing dropped and nothing invented: the segments ARE the three recorded models.
        Assert.Equal(3, line.Split(Separator, StringSplitOptions.None).Length);

        // The same rule at the structure level: every returned row is strictly positive.
        foreach (ModelUsage usage in SummaryOf(document))
        {
            Assert.True(
                usage.Attempts > 0,
                $"row '{usage.Model}' was returned with {usage.Attempts} attempts — a row that counted "
                + "nothing must never be produced.");
        }
    }

    // --- 4. the mismatch ----------------------------------------------------------------------

    /// <summary>
    /// <c>requestedModel</c> is written ONLY when the runner served something other than the route asked
    /// for, so it is ABSENT on an ordinary attempt. Both sides are asserted in ONE test on purpose: its
    /// PRESENCE is the mismatch signal — there is no flag beside it — so a renderer that always printed the
    /// substitution clause, or never printed it, must fail here. Either failure throws away the entire reason
    /// the field exists, and neither is visible to a test that checks only one side.
    ///
    /// <para>The substituted model in the fixture was requested TWICE as <see cref="Opus"/> and once as
    /// <see cref="Haiku"/>, so the clause also pins de-duplication and ordinal-ascending order of the
    /// requested ids (<c>claude-haiku-…</c> before <c>claude-opus-5</c>). The agreeing model carries no
    /// clause at all.</para>
    /// </summary>
    [Fact]
    public void RequestedModel_PresentOnlyOnMismatch_IsCarriedIntoTheSegment()
    {
        JournalDocument document = Document(
            // The route asked for opus twice and got sonnet twice: ONE distinct requested id, not two.
            Entry("01-substituted",
                Served(Sonnet, requested: Opus, attempt: 1, outcome: AttemptOutcome.GuardrailFailed),
                Served(Sonnet, requested: Opus, attempt: 2)),
            // A different route asked for haiku and also got sonnet: a second distinct requested id.
            Entry("02-substituted-differently", Served(Sonnet, requested: Haiku)),
            // The ordinary attempt: the route got what it asked for, so NO requestedModel was written.
            Entry("03-agreeing", Served(Fable)));

        IReadOnlyList<ModelUsage> summary = SummaryOf(document);

        ModelUsage substituted = Usage(summary, Sonnet);
        Assert.Equal(3, substituted.Attempts);
        Assert.Equal(new[] { Haiku, Opus }, substituted.RequestedModels);   // de-duplicated, ordinal-ascending

        ModelUsage agreeing = Usage(summary, Fable);
        Assert.Equal(1, agreeing.Attempts);
        Assert.Empty(agreeing.RequestedModels);

        string line = RenderOf(document);

        // The mismatch side: the clause names the requested id(s), once each, ordinal-ascending.
        Assert.Equal(
            $"{Sonnet}{CountMarker}3 (substituted for {Haiku}, {Opus})",
            Segment(line, Sonnet));

        // The agreeing side: NO clause. A renderer that always printed one fails this half; a renderer that
        // never printed one fails the half above.
        string agreeingSegment = Segment(line, Fable);
        Assert.Equal($"{Fable}{CountMarker}1", agreeingSegment);
        Assert.DoesNotContain("substituted", agreeingSegment, StringComparison.OrdinalIgnoreCase);
    }

    // --- 5. deterministic order ---------------------------------------------------------------

    /// <summary>
    /// Segments read in DESCENDING attempt count, then ordinal-ascending model name, so the line does not
    /// shuffle between runs of the same plan.
    ///
    /// <para>The fixture is built so that the documented order is the answer NO plausible accident gives.
    /// <see cref="Sonnet"/> (5) leads, and <see cref="Haiku"/> (2) and <see cref="Opus"/> (2) are a genuine
    /// TIE, so the name tie-break is actually exercised. Dictionary/first-appearance order is
    /// <c>opus, haiku, sonnet</c>; pure name-ascending is <c>haiku, opus, sonnet</c>; count-ascending is
    /// <c>haiku, opus, sonnet</c>; count-descending-then-name-DESCENDING is <c>sonnet, opus, haiku</c>. All
    /// four differ from the expectation, so this test discriminates rather than merely agreeing with
    /// whatever the implementation happened to do.</para>
    ///
    /// <para>The second half is the "does not shuffle" clause proper: the SAME attempt totals, journalled in
    /// a DIFFERENT task/insertion order, must render the IDENTICAL line. A comparison of two renders of one
    /// document would pass on any stable-but-arbitrary enumeration; two documents is what makes the order a
    /// property of the data rather than of the dictionary.</para>
    /// </summary>
    [Fact]
    public void SegmentOrder_IsDeterministic_AndDoesNotShuffle()
    {
        JournalDocument document = MultiModelRun();

        Assert.Equal(new[] { Sonnet, Haiku, Opus }, SummaryOf(document).Select(u => u.Model));

        string line = RenderOf(document);
        Assert.Equal(
            $"{Sonnet}{CountMarker}5{Separator}{Haiku}{CountMarker}2{Separator}{Opus}{CountMarker}2",
            line);

        // The same totals, journalled in a different order, render identically.
        Assert.Equal(line, RenderOf(MultiModelRunReordered()));
    }

    // --- fixtures -----------------------------------------------------------------------------

    /// <summary>
    /// Five attempts on <see cref="Sonnet"/>, two on <see cref="Haiku"/>, two on <see cref="Opus"/> — with
    /// opus journalled FIRST and sonnet LAST, so first-appearance/dictionary order is a different answer from
    /// the documented one.
    ///
    /// <para>Some attempts resolved a rung and some did not (a pin, a legacy-fallback route). Both are
    /// counted: this line groups by MODEL, so an attempt that named a model while resolving no rung — one
    /// <see cref="JournalTierSpend"/> cannot see at all — still belongs here.</para>
    /// </summary>
    private static JournalDocument MultiModelRun() => Document(
        Entry("01-opus-a", Served(Opus, tier: ActionTiers.Hard)),
        Entry("02-opus-b", Served(Opus)),                                 // pinned: a model, no rung
        Entry("03-haiku-a", Served(Haiku, tier: ActionTiers.Easy)),
        Entry("04-haiku-b", Served(Haiku)),                               // legacy fallback: a model, no rung
        Entry("05-sonnet-a",
            Served(Sonnet, attempt: 1, outcome: AttemptOutcome.GuardrailFailed, tier: ActionTiers.Medium),
            Served(Sonnet, attempt: 2, outcome: AttemptOutcome.GuardrailFailed, tier: ActionTiers.Medium),
            Served(Sonnet, attempt: 3, tier: ActionTiers.Medium)),
        Entry("06-sonnet-b", Served(Sonnet, tier: ActionTiers.Medium)),
        Entry("07-sonnet-c", Served(Sonnet)));                            // pinned onto the same model

    /// <summary>
    /// The SAME per-model attempt totals as <see cref="MultiModelRun"/> — sonnet 5, haiku 2, opus 2 — spread
    /// over different tasks and journalled in a different order. The rendered line must be identical.
    /// </summary>
    private static JournalDocument MultiModelRunReordered() => Document(
        Entry("01-sonnet",
            Served(Sonnet, attempt: 1, outcome: AttemptOutcome.GuardrailFailed),
            Served(Sonnet, attempt: 2)),
        Entry("02-haiku", Served(Haiku)),
        Entry("03-sonnet",
            Served(Sonnet, attempt: 1, outcome: AttemptOutcome.GuardrailFailed),
            Served(Sonnet, attempt: 2, outcome: AttemptOutcome.GuardrailFailed),
            Served(Sonnet, attempt: 3)),
        Entry("04-opus", Served(Opus, attempt: 1, outcome: AttemptOutcome.GuardrailFailed), Served(Opus, attempt: 2)),
        Entry("05-haiku", Served(Haiku)));

    // --- builders -----------------------------------------------------------------------------

    /// <summary>
    /// An attempt that recorded the model it ran on.
    ///
    /// <para><paramref name="requested"/> is the model the ROUTE asked for and is written ONLY when it
    /// differs from what was served — the mismatch case — so it defaults to null, which is what every
    /// ordinary attempt looks like.</para>
    ///
    /// <para><paramref name="tier"/> defaults to NULL, i.e. an attempt that names a model while resolving no
    /// rung: a pin (D31) or a legacy-fallback route (D30). That is deliberate. This line groups by MODEL, not
    /// by rung, so such an attempt is counted here even though it is invisible to
    /// <see cref="JournalTierSpend"/> — and a fixture where every attempt carried a tier would let an
    /// aggregation that keyed off <c>provenance.tier</c> stay green.</para>
    /// </summary>
    private static AttemptRecord Served(
        string model,
        string? requested = null,
        int attempt = 1,
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        string? tier = null) =>
        Attempt(attempt, outcome) with
        {
            Provenance = new AttemptProvenance
            {
                Runner = "routed",
                Kind = "claude",
                Model = model,
                RequestedModel = requested,
                Tier = tier,
                TierSource = tier is null ? null : TierSource.Task
            }
        };

    /// <summary>A script attempt, or any attempt from a journal written before provenance existed.</summary>
    private static AttemptRecord NoProvenance() => Attempt(1, AttemptOutcome.Succeeded) with { Provenance = null };

    /// <summary>
    /// An attempt that HAS a provenance block but no model in it — a script action in a segment worktree
    /// records the branch it ran on and nothing about a model. Distinct from
    /// <see cref="NoProvenance"/> on purpose: an aggregation that only guards against a null
    /// <c>provenance</c> would trip over this one.
    /// </summary>
    private static AttemptRecord ProvenanceWithoutAModel() =>
        Attempt(1, AttemptOutcome.Succeeded) with
        {
            Provenance = new AttemptProvenance { SegmentBranch = "guardrails/test/03-modelless/attempt-1" }
        };

    private static AttemptRecord Attempt(int attempt, AttemptOutcome outcome) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = outcome,
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

    // --- assertions ---------------------------------------------------------------------------

    private static IReadOnlyList<ModelUsage> SummaryOf(JournalDocument document)
    {
        IReadOnlyList<ModelUsage>? summary = JournalModelsUsed.Summarize(document);
        Assert.NotNull(summary);
        return summary!;
    }

    private static string RenderOf(JournalDocument document)
    {
        string? line = JournalModelsUsed.Render(document);
        Assert.NotNull(line);
        return line!;
    }

    private static ModelUsage Usage(IReadOnlyList<ModelUsage> summary, string model)
    {
        ModelUsage? usage = summary.SingleOrDefault(u => u.Model == model);
        Assert.NotNull(usage);
        return usage!;
    }

    /// <summary>
    /// The ONE model's slice of the rendered line, so a per-model assertion cannot be satisfied — or
    /// defeated — by a neighbouring model's text. Located by <c>"&lt;model&gt; ×"</c> rather than by the id
    /// alone: the count marker is what makes the match a segment HEAD rather than a mention anywhere.
    /// </summary>
    private static string Segment(string line, string model)
    {
        string? segment = line
            .Split(Separator, StringSplitOptions.None)
            .SingleOrDefault(s => s.StartsWith(model + CountMarker, StringComparison.Ordinal));

        Assert.NotNull(segment);
        return segment!;
    }

    /// <summary>
    /// The attempt count as the LINE renders it, parsed back out of the text. Reading the count from the
    /// string is deliberate: "a model named with ×0" and "a recorded model dropped from the line" are
    /// rendering failures a structural assertion cannot see, and they are exactly what a check that greps for
    /// the label certifies.
    /// </summary>
    private static int RenderedCount(string line, string model)
    {
        string segment = Segment(line, model);
        string tail = segment[(segment.IndexOf(CountMarker, StringComparison.Ordinal) + CountMarker.Length)..];
        string digits = new(tail.TakeWhile(char.IsAsciiDigit).ToArray());

        Assert.True(
            digits.Length > 0,
            $"segment '{segment}' names no attempt count after '{CountMarker}' — the line must report a "
            + "quantity, not just a model id.");

        return int.Parse(digits, CultureInfo.InvariantCulture);
    }
}
