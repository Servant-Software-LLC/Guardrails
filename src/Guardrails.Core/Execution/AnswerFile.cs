using System.Text.Json.Serialization;

namespace Guardrails.Core.Execution;

/// <summary>
/// The firstmate reply-channel answer file (doc 12 §7.4, issue #361 Phase 3) — the payload a later
/// <c>resume</c> consumes to proceed past an ANSWERABLE escalation without a human editing the plan. It lives
/// beside the escalation record it answers, at
/// <c>logs/&lt;runId&gt;/escalations/&lt;seq&gt;-&lt;gate&gt;.answer.json</c>, and MUST echo the escalation's
/// binding identity — <see cref="RunId"/> / <see cref="Seq"/> / <see cref="Gate"/> / <see cref="Subject"/> —
/// VERBATIM, plus the <see cref="DefinitionHash"/> it was written against, so <see cref="AnswerFileConsumer"/>
/// can prove the answer is correctly bound, FRESH (not stale — the definition has not changed since the
/// escalation, §7.6), and UNCONSUMED before injecting it.
///
/// <para>Consuming an answer is TRUSTING whoever wrote it (§7.7): the binding echo, the dual definition-hash
/// staleness check, and the CAS-guarded once-only consumption are load-bearing security controls, not
/// conveniences. The trust is bounded — an injected answer never bypasses a deterministic gate, never reaches
/// the verdict surface (the <c>text</c> is injected as clearly-delimited UNTRUSTED DATA, §7.4 Finding 4), and
/// never resolves the review gate.</para>
/// </summary>
public sealed record AnswerFile
{
    /// <summary>Must equal the escalation's <c>runId</c> (the run that RAISED the escalation, §7.1).</summary>
    public required string RunId { get; init; }

    /// <summary>Must equal the escalation's <c>seq</c> — the durably-monotonic, never-reused per-run counter that makes the identity tuple unique for the run's life (§7.1).</summary>
    public required int Seq { get; init; }

    /// <summary>Must equal the escalation's <c>gate</c>; only the two ANSWERABLE gates bind — <c>needs-human</c> / <c>wave-checkpoint</c> (§7.4).</summary>
    public required string Gate { get; init; }

    /// <summary>Must equal the escalation's <c>subject</c> — the task id (<c>needs-human</c>) or wave dir (<c>wave-checkpoint</c>).</summary>
    public required string Subject { get; init; }

    /// <summary>The definition hash the answer was written against — must equal BOTH the escalation record's hash AND the unit's CURRENT <c>TaskDefinitionHash</c>/<c>WaveDefinitionHash</c> (the anti-stale binding, §7.6).</summary>
    public required string DefinitionHash { get; init; }

    /// <summary>Free-text provenance of the author (unauthenticated self-report, §7.7) — recorded on the <c>answer-injected</c> decision for audit, never used as an authorization.</summary>
    public required string AnsweredBy { get; init; }

    /// <summary>UTC time the answer was written (ISO-8601).</summary>
    public required DateTimeOffset AnsweredAt { get; init; }

    /// <summary>The gate-specific payload (below): a <c>needs-human</c> text decision or a <c>wave-proceed</c> proceed/hold decision.</summary>
    public required AnswerPayload Answer { get; init; }
}

/// <summary>
/// The gate-specific answer payload (doc 12 §7.4). There are EXACTLY two kinds — see <see cref="AnswerKinds"/>:
/// a <c>needs-human</c> text decision and a <c>wave-proceed</c> proceed/hold decision. There is deliberately
/// NO kind that resolves the review gate (Blocker 2 / #366, §7.5) — an answer file can never satisfy it.
/// </summary>
public sealed record AnswerPayload
{
    /// <summary>The payload kind — <see cref="AnswerKinds.NeedsHuman"/> or <see cref="AnswerKinds.WaveProceed"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>(<c>needs-human</c>) The human's decision text — injected into the next attempt's composed prompt as clearly-delimited UNTRUSTED DATA (§7.4 Finding 4). Null for a <c>wave-proceed</c> payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>(<c>wave-proceed</c>) The checkpoint decision — <see cref="WaveProceedDecisions.Proceed"/> or <see cref="WaveProceedDecisions.Hold"/>. Null for a <c>needs-human</c> payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decision { get; init; }
}

/// <summary>
/// The two — and only two — answer-payload kinds (doc 12 §7.4, Blocker 2 / #366). The single source of truth
/// for their kebab spelling so a consumer/test asserts against the constant, never a bare string literal. No
/// kind exists that resolves the review gate — that gate clears only by escalate or the explicit
/// <c>proceed-unreviewed</c> opt-in (§7.5).
/// </summary>
public static class AnswerKinds
{
    /// <summary>Answers a <c>needs-human</c> gate: <see cref="AnswerPayload.Text"/> carries the human's decision.</summary>
    public const string NeedsHuman = "needs-human";

    /// <summary>Answers a <c>wave-checkpoint</c> gate: <see cref="AnswerPayload.Decision"/> carries <c>proceed</c>/<c>hold</c>.</summary>
    public const string WaveProceed = "wave-proceed";
}

/// <summary>The two <c>wave-proceed</c> decisions (doc 12 §7.4): <c>proceed</c> clears the checkpoint; <c>hold</c> records the human chose to stay halted.</summary>
public static class WaveProceedDecisions
{
    /// <summary>Clear the wave checkpoint and run the wave (per the §5.1 rules).</summary>
    public const string Proceed = "proceed";

    /// <summary>Stay halted — the human chose to wait.</summary>
    public const string Hold = "hold";
}
