using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Coverage for the v1 interactive-pick surface's testable core (issue #387,
/// <see cref="EscalationPickPrompt.OfferPicks"/>). The production surface wraps this with a Spectre
/// <c>SelectionPrompt</c> (an attended TTY, not unit-testable), so here a FAKE chooser stands in for the
/// operator: it proves the core offers a pick ONLY for an answerable escalation, writes the choice to the reply
/// channel, and NEVER offers a pick for a non-answerable one (the §7.3 floor).
/// </summary>
public sealed class EscalationPickPromptTests : IDisposable
{
    private const string RunId = "2026-07-24T11-00-00Z-ef01";
    private const string TaskId = "08-author-tests-run-outcome-wiring";
    private static readonly string[] Options = ["a pre-authored unreviewed wave", "the JIT BreakdownComplete path"];

    private readonly string _root;
    private readonly string _escDir;

    public EscalationPickPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-escprompt-" + Guid.NewGuid().ToString("N"));
        _escDir = Path.Combine(_root, "logs", RunId, "escalations");
        Directory.CreateDirectory(_escDir);
    }

    [Fact]
    public void OfferPicks_AnswerableEscalation_WritesTheChosenAnswer()
    {
        WriteEscalation(seq: 3, gate: "needs-human", subject: TaskId, options: Options, criticality: "high");

        int written = EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: false, chooseOption: _ => Options[1], answeredBy: "interactive-pick",
            output: TextWriter.Null);

        Assert.Equal(1, written);
        string answerPath = Path.Combine(_escDir, "3-needs-human.answer.json");
        Assert.True(File.Exists(answerPath));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(answerPath));
        Assert.Equal(Options[1], doc.RootElement.GetProperty("answer").GetProperty("text").GetString());
    }

    [Fact]
    public void OfferPicks_NonAnswerableClampedCall_OffersNoPick_WritesNothing()
    {
        // A high/critical needs-human under proceed-unreviewed is clamped non-answerable (§7.3). The core must
        // NOT offer a pick — the fake chooser is never consulted, and no answer file is written.
        WriteEscalation(seq: 5, gate: "needs-human", subject: TaskId, options: Options, criticality: "critical");
        bool chooserCalled = false;

        int written = EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: true,
            chooseOption: _ => { chooserCalled = true; return Options[0]; },
            answeredBy: "interactive-pick", output: TextWriter.Null);

        Assert.Equal(0, written);
        Assert.False(chooserCalled);
        Assert.False(File.Exists(Path.Combine(_escDir, "5-needs-human.answer.json")));
    }

    [Fact]
    public void OfferPicks_NoOpenEscalations_ReturnsZero()
    {
        int written = EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: false, chooseOption: _ => Options[0], answeredBy: "interactive-pick",
            output: TextWriter.Null);

        Assert.Equal(0, written);
    }

    [Fact]
    public void OfferPicks_ChooserDeclines_LeavesEscalationOpen()
    {
        WriteEscalation(seq: 7, gate: "needs-human", subject: TaskId, options: Options, criticality: "high");

        int written = EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: false, chooseOption: _ => null, answeredBy: "interactive-pick",
            output: TextWriter.Null);

        Assert.Equal(0, written);
        Assert.False(File.Exists(Path.Combine(_escDir, "7-needs-human.answer.json")));
    }

    // ── #485: the rare options-carrying defective-guardrail claim ────────────────────────────────

    [Fact]
    public void OfferPicks_DefectiveGuardrailClaim_StillOffersThePick_ButWarnsFirst()
    {
        // The answerability floor is untouched (a pick IS offered and written), but answering a bounded
        // question does not repair a broken check — so the surface says so before the SelectionPrompt.
        WriteEscalation(seq: 3, gate: "needs-human", subject: TaskId, options: Options, criticality: "moderate",
            kind: NeedsHumanKinds.DefectiveGuardrail);
        using var output = new StringWriter();

        int written = EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: false, chooseOption: _ => Options[0], answeredBy: "interactive-pick",
            output: output);

        Assert.Equal(1, written);
        Assert.Contains(
            "Note: this task escalated as [defective-guardrail]. Answering does not fix a guardrail — if the "
            + "claim holds, fix the check in the plan folder instead of picking here.",
            output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OfferPicks_BlockedWorkOrUnclassified_PrintsNoAdvisory()
    {
        WriteEscalation(seq: 3, gate: "needs-human", subject: TaskId, options: Options, criticality: "moderate",
            kind: NeedsHumanKinds.BlockedWork);
        WriteEscalation(seq: 4, gate: "needs-human", subject: TaskId, options: Options, criticality: "moderate");
        using var output = new StringWriter();

        EscalationPickPrompt.OfferPicks(
            _escDir, proceedUnreviewed: false, chooseOption: _ => Options[0], answeredBy: "interactive-pick",
            output: output);

        Assert.DoesNotContain("Note: this task escalated as", output.ToString(), StringComparison.Ordinal);
    }

    private void WriteEscalation(
        int seq, string gate, string subject, IReadOnlyList<string> options, string? criticality,
        string? kind = null)
    {
        var record = new
        {
            gate,
            subject,
            question = $"how should '{subject}' proceed?",
            context = "logs pointer",
            criticality,
            definitionHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            at = "2026-07-24T10:59:00Z",
            id = new { runId = RunId, seq, gate, subject },
            status = "open",
            options,
            kind
        };
        File.WriteAllText(Path.Combine(_escDir, $"{seq}-{gate}.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
