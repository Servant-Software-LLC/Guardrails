using Spectre.Console;

namespace Guardrails.Cli.Ui;

/// <summary>
/// One line of the live run's narrative pane (design 37 §4.2). <see cref="Markup"/> is the fully-rendered
/// Spectre markup for this entry AT ITS CURRENT <see cref="Count"/> — the emitter owns the wording, because
/// the singular form must stay byte-identical to the line that surface printed before design 37 and the
/// counted form reads differently per emitter (§4.5).
/// </summary>
/// <param name="Markup">The rendered line. Already escaped by the emitter; never re-escaped here.</param>
/// <param name="CoalesceKey">
/// The §4.5 coalescing key (<c>verifier-advisory</c> · <c>overwatch-no-verdict</c> · <c>model-mismatch</c>),
/// or null for an entry that never coalesces — a wave transition or a decision, each of which is a distinct
/// event and must not be folded into a count.
/// </param>
/// <param name="Count">How many occurrences this entry stands for. 1 for an uncoalesced entry.</param>
public readonly record struct NarrativeEntry(string Markup, string? CoalesceKey, int Count);

/// <summary>
/// The bounded, coalescing narrative buffer that sits ABOVE the live task table inside the Spectre Live
/// region (design 37 §4). Pure and static: the whole scrollback decision is testable as data, with no clock,
/// no timer and no terminal — the <c>GuardrailHeartbeat.FormatLine</c> / <c>LiveRunObserver.StatusMarkup</c>
/// pattern, and for the same reason (the Cli assembly ships no <c>InternalsVisibleTo</c>, so a pure function
/// IS the seam).
///
/// <para><b>Why bounded rather than unbounded scrollback (§4.2(a)).</b> Before design 37 these lines were
/// written with <c>AnsiConsole.MarkupLine</c> from INSIDE the Live region. That does not produce scrollback:
/// Spectre's <c>LiveRenderable</c> remembers the shape it drew and, on the next refresh, moves the cursor up
/// by that remembered height and repaints. A raw write advances the cursor without updating the bookkeeping,
/// so the next repaint lands one row low and stamps the table THROUGH the just-written line (#372). The trade
/// is therefore not "permanent history for recent-N" but "corrupted history for legible recent-N".</para>
///
/// <para><b>Where the full record lives (§4.2(b)).</b> <c>ObserverProjection</c> appends every
/// <c>IRunObserver</c> call verbatim and in order to <c>logs/&lt;runId&gt;/observer.jsonl</c>, and
/// <c>guardrails attach</c> replays that file into a real <c>LiveRunObserver</c>. Every elided entry is
/// durable there, and most are durable twice (<c>decisions[]</c> in <c>run.json</c>, the attempt records, the
/// wave page's settled breakdown panel). The elision line names that replay path rather than gesturing at
/// it.</para>
///
/// <para><b>The budget only works because of coalescing (§4.3).</b> Without it, one
/// <c>VerifierAdvisoryFound</c> per affected task means a 24-task advisory burst at run start evicts the whole
/// pane in one second. If coalescing is ever dropped, <see cref="DefaultBudget"/> is the wrong number and the
/// design does not hold.</para>
/// </summary>
public static class LiveNarrative
{
    /// <summary>
    /// Entries kept at a normal console width (§4.3). Eight, not a round number: the worst same-instant burst
    /// under the §4.4 routing is 6 (one JIT wave end-to-end), so 8 leaves 2–3 entries of headroom and NO burst
    /// is ever elided mid-burst — the operator never sees half a wave transition. On 80×24 the pane costs at
    /// most 8 rows, leaving 16 for the table: 4 border/header rows + 12 task rows.
    /// </summary>
    public const int DefaultBudget = 8;

    /// <summary>
    /// Entries kept below <see cref="NarrowConsoleWidth"/>. A narrow console wraps each entry to two rendered
    /// rows, so the ENTRY budget must halve to hold the ROW budget the table's share depends on.
    /// </summary>
    public const int NarrowBudget = 4;

    /// <summary>The console width at or above which <see cref="DefaultBudget"/> applies (§4.3).</summary>
    public const int NarrowConsoleWidth = 60;

    /// <summary>Coalescing key for the DoR §6.5 run-start verifier advisory (§4.4 #3).</summary>
    public const string VerifierAdvisoryKey = "verifier-advisory";

    /// <summary>Coalescing key for the #452 silent-overwatcher advisory (§4.4 #4).</summary>
    public const string OverwatchNoVerdictKey = "overwatch-no-verdict";

    /// <summary>Coalescing key for the #349 attempt model MISMATCH disclosure (§4.4 #2).</summary>
    public const string ModelMismatchKey = "model-mismatch";

    /// <summary>The entry budget for a console of <paramref name="consoleWidth"/> columns (§4.3).</summary>
    public static int BudgetFor(int consoleWidth) =>
        consoleWidth < NarrowConsoleWidth ? NarrowBudget : DefaultBudget;

    /// <summary>
    /// The index in <paramref name="current"/> of the entry <paramref name="coalesceKey"/> would replace, or
    /// -1 when the key is null (an entry that never coalesces) or not present. The ONE owner of the coalescing
    /// predicate: <see cref="Append"/> and its caller both consult it, so a caller computing the new occurrence
    /// count cannot disagree with the buffer about whether a fold happened.
    /// </summary>
    public static int CoalesceIndexOf(IReadOnlyList<NarrativeEntry> current, string? coalesceKey)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (coalesceKey is null)
        {
            return -1;
        }

        for (int i = 0; i < current.Count; i++)
        {
            if (string.Equals(current[i].CoalesceKey, coalesceKey, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Append <paramref name="incoming"/> to the buffer, honouring <paramref name="budget"/> (§4.5).
    ///
    /// <para>An entry whose <see cref="NarrativeEntry.CoalesceKey"/> is already present is <b>replaced IN
    /// PLACE</b> — it does NOT move to the bottom, because a line that jumps every time it recurs is more
    /// distracting than the information is worth. Anything else is appended at the bottom, and the OLDEST
    /// entries are dropped from the front until the buffer fits the budget (which also trims correctly when
    /// the budget SHRANK because the console was narrowed mid-run).</para>
    ///
    /// <para>Returns a new list; <paramref name="current"/> is never mutated, so a caller can compare the two
    /// counts to learn how many entries were evicted.</para>
    /// </summary>
    public static IReadOnlyList<NarrativeEntry> Append(
        IReadOnlyList<NarrativeEntry> current, NarrativeEntry incoming, int budget)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (budget <= 0)
        {
            return [];
        }

        var next = new List<NarrativeEntry>(current);
        int at = CoalesceIndexOf(current, incoming.CoalesceKey);
        if (at >= 0)
        {
            next[at] = incoming;
        }
        else
        {
            next.Add(incoming);
        }

        if (next.Count > budget)
        {
            next.RemoveRange(0, next.Count - budget);
        }

        return next;
    }

    /// <summary>
    /// The pane as rendered lines, top to bottom — the elision line first when
    /// <paramref name="elidedCount"/> is positive, then every entry's markup in buffer order (§5.4).
    ///
    /// <para>The elision line names how to replay what was dropped. With a plan directory that is
    /// <c>guardrails attach &lt;plan&gt;</c>, which replays the exact recorded call sequence into this same
    /// renderer and works after the run ends. Without one it degrades to naming the file
    /// (<c>logs/&lt;runId&gt;/observer.jsonl</c>), and without either it states the count alone rather than
    /// pointing somewhere that may not exist.</para>
    ///
    /// <para>The count is honest about the BUFFER, not the viewport: on a terminal too short for pane +
    /// table, Spectre elides further from the top (<c>Ellipsis</c>/<c>Top</c>, so the table survives), and it
    /// exposes <c>LiveRenderable.DidOverflow</c> but not a line count. Inferring one from
    /// <c>Profile.Height</c> would be a guess that goes wrong on resize, so no attempt is made.</para>
    /// </summary>
    public static IReadOnlyList<string> Render(
        IReadOnlyList<NarrativeEntry> entries, int elidedCount, string? planDirectory, string? runId)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var lines = new List<string>(entries.Count + 1);
        if (elidedCount > 0)
        {
            lines.Add(ElisionLine(elidedCount, planDirectory, runId));
        }

        foreach (NarrativeEntry entry in entries)
        {
            lines.Add(entry.Markup);
        }

        return lines;
    }

    private static string ElisionLine(int elidedCount, string? planDirectory, string? runId)
    {
        string count = $"… {elidedCount} earlier line{(elidedCount == 1 ? "" : "s")}";
        string pointer = planDirectory is not null
            ? $" — replay with: guardrails attach {Markup.Escape(planDirectory)}"
            : runId is not null
                ? $" — see logs/{Markup.Escape(runId)}/observer.jsonl"
                : "";

        return $"[grey]{count}{pointer}[/]";
    }
}
