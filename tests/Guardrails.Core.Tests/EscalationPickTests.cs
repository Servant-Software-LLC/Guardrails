using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit coverage for the shared "pick one option" surface machinery (issue #387,
/// <see cref="EscalationPick"/>) — the ONE component both pick surfaces (v1 interactive SelectionPrompt, v2 web
/// POST) call, so the non-answerable floor and the never-forge invariant are enforced identically regardless of
/// surface. Escalation records are authored directly in the FileEscalationSink shape (nested <c>id</c> +
/// top-level fields + <c>options[]</c>); the round-trip test feeds the pick's OUTPUT to the REAL
/// <see cref="AnswerFileConsumer"/> to prove a pick writes the SAME reply channel a resume consumes.
/// </summary>
public sealed class EscalationPickTests : IDisposable
{
    private const string RunId = "2026-07-24T10-00-00Z-abcd";
    private const string TaskId = "08-author-tests-run-outcome-wiring";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string[] Options =
        ["a pre-authored unreviewed wave with review-gate:proceed-unreviewed", "the JIT BreakdownComplete path"];

    private readonly string _root;
    private readonly string _escDir;

    public EscalationPickTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-escpick-" + Guid.NewGuid().ToString("N"));
        _escDir = Path.Combine(_root, "logs", RunId, "escalations");
        Directory.CreateDirectory(_escDir);
    }

    // ── ReadOpen ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadOpen_ReturnsOpenOptionsCarryingNeedsHuman_AsAnswerable()
    {
        WriteEscalation(seq: 3, gate: "needs-human", subject: TaskId, options: Options, criticality: "moderate");

        PickableEscalation pick = Assert.Single(EscalationPick.ReadOpen(_escDir, proceedUnreviewed: false));

        Assert.Equal(3, pick.Seq);
        Assert.Equal("needs-human", pick.Gate);
        Assert.Equal(TaskId, pick.Subject);
        Assert.Equal(Options, pick.Options);
        Assert.True(pick.Answerable);
        Assert.Null(pick.NonAnswerableReason);
    }

    [Fact]
    public void ReadOpen_ExcludesFreeTextAndConsumedAndNonNeedsHuman()
    {
        WriteEscalation(seq: 1, gate: "needs-human", subject: TaskId, options: [], criticality: "moderate"); // free-text (no options)
        WriteEscalation(seq: 2, gate: "needs-human", subject: TaskId, options: Options, status: "consumed");   // already consumed
        WriteEscalation(seq: 3, gate: "wave-checkpoint", subject: "wave-02", options: Options);                // not needs-human

        Assert.Empty(EscalationPick.ReadOpen(_escDir, proceedUnreviewed: false));
    }

    [Fact]
    public void ReadOpen_ClampedHighUnderProceedUnreviewed_IsNonAnswerable_WithReason()
    {
        WriteEscalation(seq: 5, gate: "needs-human", subject: TaskId, options: Options, criticality: "critical");

        PickableEscalation pick = Assert.Single(EscalationPick.ReadOpen(_escDir, proceedUnreviewed: true));

        Assert.False(pick.Answerable);
        Assert.NotNull(pick.NonAnswerableReason);
        Assert.Contains("proceed-unreviewed", pick.NonAnswerableReason!);
    }

    // ── WriteChoice: the happy path is a REAL answer file the REAL consumer injects ───────────────

    [Fact]
    public void WriteChoice_ValidPick_WritesAnswerFile_ConsumedAndInjectedByAnswerFileConsumer()
    {
        WriteEscalation(seq: 7, gate: "needs-human", subject: TaskId, options: Options, criticality: "high");

        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 7, gate: "needs-human", chosenOption: Options[1], answeredBy: "interactive-pick",
            proceedUnreviewed: false);

        Assert.Equal(PickWriteResult.Written, outcome.Result);

        string answerPath = Path.Combine(_escDir, "7-needs-human.answer.json");
        Assert.True(File.Exists(answerPath));

        // The written file is a valid firstmate answer echoing the binding, with a needs-human (never a
        // review-resolving) payload whose text is the CHOSEN option.
        AnswerFile? answer = JsonSerializer.Deserialize<AnswerFile>(
            File.ReadAllText(answerPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(answer);
        Assert.Equal(RunId, answer!.RunId);
        Assert.Equal(7, answer.Seq);
        Assert.Equal("needs-human", answer.Gate);
        Assert.Equal(TaskId, answer.Subject);
        Assert.Equal(Hash, answer.DefinitionHash);
        Assert.Equal(AnswerKinds.NeedsHuman, answer.Answer.Kind);
        Assert.Equal(Options[1], answer.Answer.Text);
        Assert.Equal("interactive-pick", answer.AnsweredBy);

        // The pick writes the SAME reply channel a resume consumes: the REAL consumer injects the chosen option
        // as delimited UNTRUSTED data and flips the escalation to consumed.
        AnswerConsumptionResult consumed =
            new AnswerFileConsumer(_escDir).Consume(seq: 7, gate: "needs-human", Hash, proceedUnreviewed: false);
        Assert.Equal(AnswerOutcome.Injected, consumed.Outcome);
        Assert.Contains(Options[1], consumed.InjectedPromptSection!);
        Assert.Contains(PromptComposer.InjectedHumanAnswerBeginMarker, consumed.InjectedPromptSection!);
        Assert.Equal("consumed", ReadStatus(7, "needs-human"));
    }

    // ── The non-answerable floor + the never-forge invariant ──────────────────────────────────────

    [Fact]
    public void WriteChoice_ReviewGate_IsRefused_NoAnswerFile_NoReviewMarkerEverWritten()
    {
        // A review-gate escalation is NON-answerable (§7.5) — even carrying options, a pick MUST refuse it. This
        // is the never-forge invariant: the pick can never resolve the review gate nor write a review marker.
        WriteEscalation(seq: 9, gate: "review-gate", subject: "wave-03", options: Options, criticality: "high");

        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 9, gate: "review-gate", chosenOption: Options[0], answeredBy: "interactive-pick",
            proceedUnreviewed: false);

        Assert.Equal(PickWriteResult.RefusedNonAnswerable, outcome.Result);
        Assert.False(File.Exists(Path.Combine(_escDir, "9-review-gate.answer.json")));

        // No review marker was written ANYWHERE under the run root — a pick can never forge state/guardrails-review.json.
        Assert.Empty(Directory.EnumerateFiles(_root, "guardrails-review.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void WriteChoice_ClampedHardCall_UnderProceedUnreviewed_IsRefused_NoAnswerFile()
    {
        // A high/critical needs-human hard call under proceed-unreviewed is clamped NON-answerable (§5.2/§7.3
        // Blocker 1) — the SAME clamp the consumer enforces. A pick surface can never write past it.
        WriteEscalation(seq: 11, gate: "needs-human", subject: TaskId, options: Options, criticality: "critical");

        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 11, gate: "needs-human", chosenOption: Options[0], answeredBy: "interactive-pick",
            proceedUnreviewed: true);

        Assert.Equal(PickWriteResult.RefusedNonAnswerable, outcome.Result);
        Assert.False(File.Exists(Path.Combine(_escDir, "11-needs-human.answer.json")));
    }

    [Fact]
    public void WriteChoice_OffMenuChoice_IsRejected_BoundedPick()
    {
        WriteEscalation(seq: 13, gate: "needs-human", subject: TaskId, options: Options, criticality: "high");

        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 13, gate: "needs-human", chosenOption: "rm -rf / (not one of the agent's options)",
            answeredBy: "log-viewer-pick", proceedUnreviewed: false);

        Assert.Equal(PickWriteResult.OptionNotOffered, outcome.Result);
        Assert.False(File.Exists(Path.Combine(_escDir, "13-needs-human.answer.json")));
    }

    [Fact]
    public void WriteChoice_AlreadyConsumed_IsRefused()
    {
        WriteEscalation(seq: 15, gate: "needs-human", subject: TaskId, options: Options, status: "consumed");

        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 15, gate: "needs-human", chosenOption: Options[0], answeredBy: "interactive-pick",
            proceedUnreviewed: false);

        Assert.Equal(PickWriteResult.AlreadyConsumed, outcome.Result);
    }

    [Fact]
    public void WriteChoice_UnknownEscalation_IsNotFound()
    {
        PickWriteOutcome outcome = EscalationPick.WriteChoice(
            _escDir, seq: 99, gate: "needs-human", chosenOption: Options[0], answeredBy: "interactive-pick",
            proceedUnreviewed: false);

        Assert.Equal(PickWriteResult.NotFound, outcome.Result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Author an escalation record in the exact FileEscalationSink shape (nested <c>id</c> + top-level fields + <c>options</c>).</summary>
    private void WriteEscalation(
        int seq, string gate, string subject, IReadOnlyList<string> options,
        string? criticality = "moderate", string status = "open")
    {
        var record = new JsonObject
        {
            ["gate"] = gate,
            ["subject"] = subject,
            ["question"] = $"[{gate}] which option should '{subject}' take?",
            ["context"] = "logs pointer + failure detail + the best-guess considered",
            ["definitionHash"] = Hash,
            ["at"] = "2026-07-24T09:59:00Z",
            ["id"] = new JsonObject
            {
                ["runId"] = RunId,
                ["seq"] = seq,
                ["gate"] = gate,
                ["subject"] = subject
            },
            ["status"] = status,
            ["options"] = new JsonArray(options.Select(o => (JsonNode)o!).ToArray())
        };
        if (criticality is not null)
        {
            record["criticality"] = criticality;
        }

        File.WriteAllText(Path.Combine(_escDir, $"{seq}-{gate}.json"), record.ToJsonString());
    }

    private string? ReadStatus(int seq, string gate)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_escDir, $"{seq}-{gate}.json")));
        return doc.RootElement.GetProperty("status").GetString();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
