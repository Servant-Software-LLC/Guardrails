using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #485 — the five operator-facing surfaces that render the agent's <c>needsHuman.kind</c> claim,
/// pinned as PURE functions (no clock, no terminal, no IO): the live table
/// (<see cref="LiveRunObserver.StatusMarkup"/>), <c>--no-ui</c> (<see cref="ConsoleRunObserver"/> writing
/// to an injected <see cref="TextWriter"/>), the run summary
/// (<see cref="RunCommand.RenderNeedsHumanSections"/>), <c>guardrails status</c>
/// (<see cref="StatusCommand.LastFailureText"/>), and the log site
/// (<see cref="LogSiteRenderer.ClaimChip"/>).
///
/// <para><b>The load-bearing class is UNCLASSIFIED.</b> Every pre-#485 escalation lands there, as does
/// any agent that ignores the affordance — so it will dominate in the field. Its assertions are EQUALITY
/// against the literal strings the harness printed before #485, not <c>Contains</c>: an equality
/// assertion is the only kind that catches an ACCIDENTAL ADDITION, which is the whole risk this design
/// takes on.</para>
/// </summary>
public sealed class NeedsHumanKindRenderingTests
{
    private const string Work = NeedsHumanKinds.BlockedWork;
    private const string Guardrail = NeedsHumanKinds.DefectiveGuardrail;

    // ── Surface 1: the live table (width-scarce ⇒ the TERSE half of the token) ────────────────

    [Fact]
    public void LiveTable_Unclassified_IsByteIdenticalToTodaysMarkup()
    {
        // Not Contains: the unqualified form must be EXACTLY what every run has always printed, so it
        // cannot read as either kind, cannot look broken, and costs zero characters.
        Assert.Equal("[red]needs human[/]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, null));
        Assert.Equal("[red]needs human[/]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman));
        Assert.Equal("[red]needs human[/]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, "nonsense"));
        Assert.Equal("[red]needs human[/]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, "BLOCKED-WORK"));
    }

    [Theory]
    [InlineData(Work, "[red]needs human (work)[/]")]
    [InlineData(Guardrail, "[red]needs human (guardrail)[/]")]
    public void LiveTable_ClassifiedHalt_QualifiesTheStatusCell(string kind, string expected) =>
        Assert.Equal(expected, LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, kind));

    [Fact]
    public void LiveTable_ColourStaysRedForAllThree()
    {
        // #190 spent BLUE on "provider-side, re-run later". A defective guardrail is not re-run-later, so
        // a second colour would blur that signal for no gain the text does not already carry — and nothing
        // in this design may be colour-only.
        Assert.StartsWith("[red]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, null), StringComparison.Ordinal);
        Assert.StartsWith("[red]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, Work), StringComparison.Ordinal);
        Assert.StartsWith("[red]", LiveRunObserver.StatusMarkup(TaskOutcome.NeedsHuman, Guardrail), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskOutcome.Succeeded, "[green]succeeded[/]")]
    [InlineData(TaskOutcome.Skipped, "[green]skipped[/]")]
    [InlineData(TaskOutcome.Blocked, "[orange3]blocked[/]")]
    [InlineData(TaskOutcome.Cancelled, "[grey]cancelled[/]")]
    [InlineData(TaskOutcome.RateLimited, "[blue]rate limited[/]")]
    public void LiveTable_NonNeedsHumanOutcomes_IgnoreAKindEntirely(TaskOutcome outcome, string expected)
    {
        // A kind can only ever arrive on a needs-human settle, but pin it anyway: the qualifier must never
        // leak onto a green or a rate-limited row if a future caller passes one through.
        Assert.Equal(expected, LiveRunObserver.StatusMarkup(outcome, Guardrail));
        Assert.Equal(expected, LiveRunObserver.StatusMarkup(outcome, null));
    }

    // ── Surface 2: --no-ui (a tailed CI log IS the record) ───────────────────────────────────

    [Fact]
    public void NoUi_Unclassified_IsByteIdenticalToTodaysOutput()
    {
        Assert.Equal(
            "[NEEDS HUMAN] 07-implement — needs human: which engine?\n\n",
            RenderConsole(Result("07-implement", null)));

        Assert.Null(ConsoleRunObserver.ClaimLine(null));
        Assert.Null(ConsoleRunObserver.ClaimLine("nonsense"));
    }

    [Fact]
    public void NoUi_ClassifiedHalt_AddsOneIndentedClaimLine_LeadingLineVerbatim()
    {
        // The leading line is grep-anchored and CI-parsed — it must survive byte-for-byte. The claim rides
        // BELOW it in this file's own "  [tag]" idiom.
        Assert.Equal(
            "[NEEDS HUMAN] 07-implement — needs human: which engine?\n"
            + "  [claim] defective-guardrail — the agent disputes the check, not the work (unverified)\n"
            + "\n",
            RenderConsole(Result("07-implement", Guardrail)));

        Assert.Equal(
            "[NEEDS HUMAN] 07-implement — needs human: which engine?\n"
            + "  [claim] blocked-work — the agent could not complete the work (unverified)\n"
            + "\n",
            RenderConsole(Result("07-implement", Work)));
    }

    [Fact]
    public void NoUi_ClaimLine_SaysTheHarnessDidNotVerifyIt()
    {
        // The harness cannot adjudicate which kind a halt is; it records what was asserted. Both lines must
        // say so, or an operator reads a claim as a finding.
        Assert.Contains("(unverified)", ConsoleRunObserver.ClaimLine(Work)!, StringComparison.Ordinal);
        Assert.Contains("(unverified)", ConsoleRunObserver.ClaimLine(Guardrail)!, StringComparison.Ordinal);
    }

    // ── Surface 3: the run summary ───────────────────────────────────────────────────────────

    [Fact]
    public void RunSummary_Unclassified_IsByteIdenticalToTodaysSection()
    {
        Assert.Equal(
            "\n"
            + "NEEDS HUMAN: 07-implement — needs human: which engine?\n"
            + $"  Inspect {TaskLogDir("07-implement")}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),\n"
            + "  fix the action or guardrails, then re-run to resume.\n",
            RenderSummary(Result("07-implement", null)));
    }

    [Fact]
    public void RunSummary_UnrecognisedKind_DegradesToTheUnclassifiedSection()
    {
        Assert.Equal(RenderSummary(Result("07-implement", null)), RenderSummary(Result("07-implement", "not-a-kind")));
    }

    [Fact]
    public void RunSummary_DefectiveGuardrail_PointsAtTheCheck_AndReplacesTheClosingLine()
    {
        Assert.Equal(
            "\n"
            + "NEEDS HUMAN: 07-implement — needs human: which engine?\n"
            + "  Agent's claim [defective-guardrail] — look at the CHECK, not the task. The harness records "
            + "this claim; it does not verify it. Evidence: the latest attempt's action-out-fragment.json.\n"
            + $"  Inspect {TaskLogDir("07-implement")}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),\n"
            + "  and if the claim holds fix the guardrail (/guardrails-review) — the work may already be complete.\n",
            RenderSummary(Result("07-implement", Guardrail)));
    }

    [Fact]
    public void RunSummary_BlockedWork_PointsAtTheTask_AndReplacesTheClosingLine()
    {
        Assert.Equal(
            "\n"
            + "NEEDS HUMAN: 07-implement — needs human: which engine?\n"
            + "  Agent's claim [blocked-work] — look at the TASK. The harness records this claim; it does "
            + "not verify it.\n"
            + $"  Inspect {TaskLogDir("07-implement")}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),\n"
            + "  answer the question or re-scope the task (action, writeScope, dependencies), then re-run to resume.\n",
            RenderSummary(Result("07-implement", Work)));
    }

    [Fact]
    public void RunSummary_ClassifiedHalt_NeverKeepsTheMisdirectingClosingLine()
    {
        // "fix the action or guardrails" actively misdirects for BOTH kinds — a blocked-work halt needs a
        // decision or a re-scope, and a defective-guardrail halt claims the work is already right.
        Assert.DoesNotContain("fix the action or guardrails", RenderSummary(Result("07-implement", Work)), StringComparison.Ordinal);
        Assert.DoesNotContain("fix the action or guardrails", RenderSummary(Result("07-implement", Guardrail)), StringComparison.Ordinal);
    }

    [Fact]
    public void RunSummary_TriageBlock_IsUntouched_AndKindsAreNeverGrouped()
    {
        // Two tasks that both claim "guardrail" are two DIFFERENT guardrails — grouping them would tell the
        // operator nothing. The #163 same-root-cause grouping stays keyed on the triage DIAGNOSIS only.
        var triage = new TriageSummary("plan-authoring", "the check's filter names six clauses", "");
        string rendered = Render([Result("07-a", Guardrail), Result("08-b", Guardrail)], _ => triage);

        Assert.Contains("Root cause [plan-authoring]: the check's filter names six clauses\n", rendered, StringComparison.Ordinal);
        Assert.Contains("(same root cause as 07-a)", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("same claim as", rendered, StringComparison.Ordinal);

        // The claim sits ABOVE the harness's triage: the agent-asserted half first, the harness's second.
        int claim = rendered.IndexOf("Agent's claim [defective-guardrail]", StringComparison.Ordinal);
        int rootCause = rendered.IndexOf("Root cause [plan-authoring]", StringComparison.Ordinal);
        Assert.True(claim >= 0 && rootCause > claim, "the agent's claim must precede the harness's triage diagnosis");
    }

    // ── Surface 4: guardrails status (LAST FAILURE, a ,-40 budget) ───────────────────────────

    [Fact]
    public void Status_LastFailure_Unclassified_ReadsAgentEscalated_NotTheRawEnum()
    {
        // Pre-#485 this cell leaked the C# enum name ("NeedsHuman") — a defect this edit fixes in the same
        // place. Unclassified therefore renders the BARE phrase, with no bracketed kind.
        Assert.Equal("agent escalated", StatusCommand.LastFailureText(Attempt(AttemptOutcome.NeedsHuman, null)));
        Assert.Equal("agent escalated", StatusCommand.LastFailureText(Attempt(AttemptOutcome.NeedsHuman, "nonsense")));
    }

    [Theory]
    [InlineData(Work, "agent escalated [blocked-work]")]
    [InlineData(Guardrail, "agent escalated [defective-guardrail]")]
    public void Status_LastFailure_ClassifiedHalt_NamesTheKind_WithinTheColumnBudget(string kind, string expected)
    {
        string cell = StatusCommand.LastFailureText(Attempt(AttemptOutcome.NeedsHuman, kind));
        Assert.Equal(expected, cell);
        Assert.True(cell.Length <= 40, $"the LAST FAILURE column is ,-40; '{cell}' is {cell.Length} chars");
    }

    [Fact]
    public void Status_LastFailure_OtherOutcomes_AreUnchanged()
    {
        // A RETRY-EXHAUSTION halt records guardrail-failed / action-failed on its last attempt, never
        // needs-human — so it never reaches the new branch.
        Assert.Equal("-", StatusCommand.LastFailureText(null));
        Assert.Equal("-", StatusCommand.LastFailureText(Attempt(AttemptOutcome.Succeeded, null)));
        Assert.Equal("timed out", StatusCommand.LastFailureText(Attempt(AttemptOutcome.Timeout, null)));
        Assert.Equal("invalid state fragment", StatusCommand.LastFailureText(Attempt(AttemptOutcome.InvalidFragment, null)));
        Assert.Equal("cancelled", StatusCommand.LastFailureText(Attempt(AttemptOutcome.Cancelled, null)));
        Assert.Equal("GuardrailFailed", StatusCommand.LastFailureText(Attempt(AttemptOutcome.GuardrailFailed, null)));
        Assert.Equal("action exited 3", StatusCommand.LastFailureText(
            Attempt(AttemptOutcome.ActionFailed, null) with { ActionExitCode = 3 }));

        // A failed guardrail still wins the cell, kind or no kind.
        Assert.Equal("01-build: exit 1", StatusCommand.LastFailureText(
            Attempt(AttemptOutcome.NeedsHuman, Guardrail) with
            {
                FailedGuardrails = [new FailedGuardrail { Name = "01-build", Reason = "exit 1" }]
            }));
    }

    // ── Surface 5: the log site ──────────────────────────────────────────────────────────────

    [Fact]
    public void LogSite_ClaimChip_Unclassified_IsTheEmptyString()
    {
        // Empty, not a placeholder: an unclassified status cell must be byte-for-byte what it always was.
        Assert.Equal(string.Empty, LogSiteRenderer.ClaimChip(null));
        Assert.Equal(string.Empty, LogSiteRenderer.ClaimChip(""));
        Assert.Equal(string.Empty, LogSiteRenderer.ClaimChip("nonsense"));
        Assert.Equal(string.Empty, LogSiteRenderer.ClaimChip("DEFECTIVE-GUARDRAIL"));
    }

    [Theory]
    [InlineData(Work, " <span class=\"claim\">work</span>")]
    [InlineData(Guardrail, " <span class=\"claim\">guardrail</span>")]
    public void LogSite_ClaimChip_ClassifiedHalt_IsTheTerseSpan(string kind, string expected) =>
        Assert.Equal(expected, LogSiteRenderer.ClaimChip(kind));

    [Fact]
    public void LogSite_ClaimStyleReusesTheExistingMutedPaletteValue()
    {
        // One CSS rule, reusing the pending/blocked/unknown grey already in the sheet — the chip must read
        // as a qualifier of the red status word, never as a second status.
        Assert.Contains(".claim { font-weight: 400; color: #8aa0b3; }", LogSiteRenderer.SharedStyle, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private const string LogsRoot = "LOGS";

    /// <summary>The <c>Inspect</c> path the renderer composes — named here so the equality assertions stay OS-stable.</summary>
    private static string TaskLogDir(string taskId) => Path.Combine(LogsRoot, taskId);

    private static TaskResult Result(string taskId, string? kind) => new()
    {
        TaskId = taskId,
        Outcome = TaskOutcome.NeedsHuman,
        Summary = "needs human: which engine?",
        NeedsHumanKind = kind
    };

    private static AttemptRecord Attempt(AttemptOutcome outcome, string? kind) => new()
    {
        Attempt = 1,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        Outcome = outcome,
        LogDir = "logs/r/07-implement/attempt-1",
        NeedsHumanKind = kind
    };

    /// <summary>Render one task through <c>--no-ui</c>, with <c>\n</c> newlines so the assertion is OS-stable.</summary>
    private static string RenderConsole(TaskResult result)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        new ConsoleRunObserver(writer).TaskFinished(result);
        return writer.ToString();
    }

    private static string RenderSummary(TaskResult result) => Render([result], _ => null);

    private static string Render(IReadOnlyList<TaskResult> tasks, Func<string, TriageSummary?> triageFor)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        RunCommand.RenderNeedsHumanSections(tasks, LogsRoot, writer, triageFor);
        return writer.ToString();
    }
}
