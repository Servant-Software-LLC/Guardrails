namespace Guardrails.Core.Execution;

/// <summary>
/// The ONE decision point for the optional <c>needsHuman.kind</c> classification (issue #485, SSOT §9):
/// the agent's own claim about WHICH follow-up a needs-human halt calls for —
/// <see cref="BlockedWork"/> ("I cannot complete this work" → look at the TASK) versus
/// <see cref="DefectiveGuardrail"/> ("this check is itself wrong" → look at the CHECK; the work may
/// already be correct). Two situations that read identically today and need opposite responses.
///
/// <para><b>Absent or unrecognised means UNCLASSIFIED, and the harness invents no default.</b> The
/// classification is the AGENT's assertion, never the harness's judgement — the harness cannot verify
/// which kind a halt is, so it records what was claimed and lets a human adjudicate (the same posture
/// as #481's evidence requirement). Every pre-#485 escalation, and any agent that ignores the
/// affordance, lands here and renders exactly as it always has.</para>
///
/// <para>Every surface that shows the kind — the live table, <c>--no-ui</c>, the run summary,
/// <c>guardrails status</c>, the log site — routes through <see cref="Parse"/> / <see cref="Terse"/>
/// rather than re-deriving the mapping, so "never invent a default" is enforced in ONE place instead of
/// being a convention five call sites can each drift from.</para>
/// </summary>
public static class NeedsHumanKinds
{
    /// <summary>The agent cannot complete the work — a missing decision, an unreachable symbol, a genuinely hard task. The follow-up is to help the agent or re-scope the task.</summary>
    public const string BlockedWork = "blocked-work";

    /// <summary>The agent asserts a GUARDRAIL is wrong — it reports something absent that is visibly present (#481). The follow-up is to fix the check in the plan folder; the work may already be complete.</summary>
    public const string DefectiveGuardrail = "defective-guardrail";

    /// <summary>
    /// Canonicalize a raw <c>kind</c> value: <see cref="BlockedWork"/> or <see cref="DefectiveGuardrail"/>
    /// for an exact (ordinal) match, and <c>null</c> for EVERYTHING else — absent, empty, whitespace, a
    /// different casing, or a value this harness version does not know. An unrecognised value is
    /// deliberately NOT an error and NOT a warning: it degrades to unclassified, which renders exactly as
    /// a pre-#485 escalation does.
    /// </summary>
    public static string? Parse(string? kind) => kind switch
    {
        BlockedWork => BlockedWork,
        DefectiveGuardrail => DefectiveGuardrail,
        _ => null
    };

    /// <summary>
    /// The width-scarce rendering of a kind — <c>work</c> / <c>guardrail</c>, or <c>null</c> when
    /// unclassified. Used where a column cannot afford the full token (the live-table Status cell, the
    /// log-site claim chip). It is the DISTINGUISHING half of the contract token rather than a separate
    /// vocabulary, so there is no translation layer that can drift from <see cref="Parse"/>.
    /// </summary>
    public static string? Terse(string? kind) => Parse(kind) switch
    {
        BlockedWork => "work",
        DefectiveGuardrail => "guardrail",
        _ => null
    };
}
