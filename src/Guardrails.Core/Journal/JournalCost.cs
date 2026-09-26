namespace Guardrails.Core.Journal;

/// <summary>
/// Run-level cost aggregation (SSOT §7 <c>costUsd</c>): sums the per-attempt
/// <see cref="AttemptRecord.CostUsd"/> across every recorded attempt of every task. Used by
/// the <c>run</c> summary and <c>guardrails status</c> to print a single
/// <c>Total prompt cost</c> line.
///
/// The total is null when no attempt recorded a cost at all — deterministic-only plans
/// never record one, so the caller omits the line entirely and stays noise-free. A run with
/// any prompt attempt (even one costing $0) reports a concrete total.
/// </summary>
public static class JournalCost
{
    /// <summary>
    /// The summed cost across all attempts in <paramref name="document"/>, or null when no
    /// attempt recorded a cost. Attempts with a null <see cref="AttemptRecord.CostUsd"/> are
    /// ignored; a $0.00 recorded cost still makes the total non-null.
    /// </summary>
    public static decimal? Total(JournalDocument document)
    {
        decimal sum = 0m;
        bool any = false;

        foreach (TaskJournalEntry entry in document.Tasks.Values)
        {
            foreach (AttemptRecord attempt in entry.Attempts)
            {
                if (attempt.CostUsd is { } cost)
                {
                    sum += cost;
                    any = true;
                }
            }
        }

        // Overhead prompt spend that is not a task attempt (SSOT §9.2, #269/#314) — the overwatcher's
        // diagnose prompts, the AI-merge worker, and the terminal needs-human triage — is folded in here so
        // the reported total AND the maxCostUsd gate (via RunJournal.CurrentCostUsd) both see it. A recorded
        // overhead cost (even $0) makes the total non-null.
        if (document.OverheadCostUsd is { } overhead)
        {
            sum += overhead;
            any = true;
        }

        return any ? sum : null;
    }

    /// <summary>
    /// The token usage of every dispatch that went through a claude gateway (#782 §4) — task attempts whose
    /// provenance names a gateway, plus overhead gateway dispatches — or null when none reported any. This is the
    /// gateway half of a run's spend, which <see cref="Total"/> cannot carry because a gateway dispatch has no cost;
    /// rendered beside it by <see cref="SpendFormat.Total"/>. Judge spend stays out, exactly as it stays out of
    /// <see cref="Total"/>.
    /// </summary>
    public static long? GatewayTokens(JournalDocument document)
    {
        long sum = 0;
        bool any = false;

        foreach (TaskJournalEntry entry in document.Tasks.Values)
        {
            foreach (AttemptRecord attempt in entry.Attempts)
            {
                if (attempt.Provenance?.Gateway is not null && attempt.Usage is { } usage)
                {
                    sum += (long)usage.InputTokens + usage.OutputTokens;
                    any = true;
                }
            }
        }

        foreach (OverheadGatewayDispatch dispatch in document.OverheadGatewayDispatches ?? [])
        {
            if (dispatch.Usage is { } usage)
            {
                sum += (long)usage.InputTokens + usage.OutputTokens;
                any = true;
            }
        }

        return any ? sum : null;
    }

    /// <summary>
    /// The run's spend as ONE rendered figure (#782 §4): <see cref="Total"/> and <see cref="GatewayTokens"/> kept in
    /// their own units by <see cref="SpendFormat.Total"/>, or null when neither has anything to report.
    /// </summary>
    public static string? Render(JournalDocument document) =>
        SpendFormat.Total(Total(document), GatewayTokens(document));
}
