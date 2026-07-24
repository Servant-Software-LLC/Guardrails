using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console;

namespace Guardrails.Cli.Ui;

/// <summary>
/// The v1 interactive "pick one option" surface (issue #387): after an attended run halts with an OPEN,
/// options-carrying <c>needs-human</c> escalation, offer the operator an arrow-key/number
/// <c>Spectre SelectionPrompt</c> instead of making them hand-author a reply file (or edit the prompt and
/// re-run). The chosen option is written — through the SHARED <see cref="EscalationPick.WriteChoice"/> — to the
/// EXISTING firstmate reply channel (<c>escalations/&lt;seq&gt;-&lt;gate&gt;.answer.json</c>), and injected on
/// the next resume by the UNCHANGED <see cref="AnswerFileConsumer"/> path (halt/resume; no parallel path).
///
/// <para>Reuses the same interactive-capture idiom the drift confirm uses (a CLI prompt OUTSIDE the Spectre
/// live region — the live region is already disposed by run end). A NON-answerable escalation (a clamped hard
/// call under <c>proceed-unreviewed</c>, §7.3) is NEVER offered a pick — it prints the halt reason and moves on,
/// exactly as today; the <see cref="EscalationPick.WriteChoice"/> floor is the hard backstop regardless of
/// surface.</para>
/// </summary>
public static class EscalationPickPrompt
{
    /// <summary>The provenance recorded on a pick written from the interactive surface (audit only, never an authorization).</summary>
    private const string AnsweredBy = "interactive-pick";

    /// <summary>A menu sentinel for "decide later" — leaves the escalation open (no answer file written).</summary>
    private const string DecideLater = "(decide later — leave this open)";

    /// <summary>
    /// Offer interactive picks for the run's open, answerable <c>needs-human</c> escalations, when the process
    /// is attended (an interactive TTY with real stdin). A no-op when not interactive (CI / redirected input —
    /// the run already halted with the escalation records for an out-of-band answer) or when there is nothing
    /// pickable. Self-guarded so <see cref="Commands.RunCommand"/> can call it unconditionally at run end.
    /// </summary>
    public static void OfferPicksInteractive(PlanDefinition plan, string runId, IConsoleIo io)
    {
        // Attended-TTY guard (mirrors the drift-confirm idiom): a pick needs real interactive stdin. Never
        // prompt when input is redirected (CI / a pipe) — the escalation stays open for the answer channel.
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            return;
        }

        string escalationsDir = Path.Combine(plan.PlanDirectory, "logs", runId, "escalations");
        bool proceedUnreviewed =
            plan.Config.Autonomy?.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed;

        OfferPicks(escalationsDir, proceedUnreviewed, PromptForOption, AnsweredBy, io.Out);
    }

    /// <summary>
    /// The testable core: for each OPEN, options-carrying <c>needs-human</c> escalation in
    /// <paramref name="escalationsDir"/>, present it to <paramref name="chooseOption"/> and — on a returned
    /// choice — write it via <see cref="EscalationPick.WriteChoice"/>, printing the outcome. A NON-answerable
    /// escalation is NOT offered a pick (its halt reason is printed). Returns the number of picks written.
    /// <paramref name="chooseOption"/> returns the chosen option, or null to leave the escalation open.
    /// </summary>
    public static int OfferPicks(
        string escalationsDir,
        bool proceedUnreviewed,
        Func<PickableEscalation, string?> chooseOption,
        string answeredBy,
        TextWriter output)
    {
        IReadOnlyList<PickableEscalation> escalations = EscalationPick.ReadOpen(escalationsDir, proceedUnreviewed);
        if (escalations.Count == 0)
        {
            return 0;
        }

        output.WriteLine();
        output.WriteLine("Awaiting your decision — one or more tasks raised an enumerated needsHuman question:");

        int written = 0;
        foreach (PickableEscalation escalation in escalations)
        {
            output.WriteLine();
            output.WriteLine($"  [{escalation.Subject}] {escalation.Question}");

            if (!escalation.Answerable)
            {
                // The non-answerable floor: no pick offered — the operator must do real human work (§7.3).
                output.WriteLine($"    (no pick offered) {escalation.NonAnswerableReason}");
                continue;
            }

            string? chosen = chooseOption(escalation);
            if (chosen is null)
            {
                output.WriteLine("    Left open — resolve it later (a resume will re-pose it).");
                continue;
            }

            PickWriteOutcome outcome = EscalationPick.WriteChoice(
                escalationsDir, escalation.Seq, escalation.Gate, chosen, answeredBy, proceedUnreviewed);
            output.WriteLine($"    {outcome.Message}");
            if (outcome.Result == PickWriteResult.Written)
            {
                written++;
            }
        }

        if (written > 0)
        {
            output.WriteLine();
            output.WriteLine($"Recorded {written} choice(s). Resume with `guardrails run` to inject them and continue.");
        }

        return written;
    }

    /// <summary>The production chooser: an arrow-key/number <c>SelectionPrompt</c> over the escalation's own options (plus a "decide later" escape).</summary>
    private static string? PromptForOption(PickableEscalation escalation)
    {
        var prompt = new SelectionPrompt<string>()
            .Title($"    Pick one option for [yellow]{Markup.Escape(escalation.Subject)}[/]:")
            .AddChoices(escalation.Options)
            .AddChoices(DecideLater)
            .UseConverter(Markup.Escape);

        string chosen = AnsiConsole.Prompt(prompt);
        return chosen == DecideLater ? null : chosen;
    }
}
