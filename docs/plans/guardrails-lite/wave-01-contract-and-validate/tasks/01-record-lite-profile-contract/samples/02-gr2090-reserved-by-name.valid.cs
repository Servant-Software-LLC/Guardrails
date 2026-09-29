namespace Guardrails.Core.Loading;

/// <summary>Diagnostic codes (sample: the tail of the real file after this task).</summary>
public static class DiagnosticCodes
{
    // GR2083 is RESERVED BY NAME for issue #544 (native local-inference actions, the approved
    // docs/plans/544-local-inference-actions.charter.md) and is not yet allocated in master.

    /// <summary>
    /// GR2089 (WARNING) — a prompt runner block's <c>guardrailOverrides</c> carries <c>stallTimeoutSeconds</c> (#811).
    /// </summary>
    public const string StallTimeoutInGuardrailOverrides = "GR2089";

    // GR2090 is RESERVED BY NAME for Guardrails Lite (#823): LiteUnsupportedFeature, emitted only by
    // scripts/lite/validate.ps1 for a plan feature Lite does not implement. The harness never emits it,
    // so it is deliberately NOT a constant. Do not allocate GR2090 for anything else.

    // CURRENT next-free code: GR2091. GR2089 (StallTimeoutInGuardrailOverrides) is the last taken code
    // above; GR2083 is RESERVED BY NAME above (#544), GR2090 is RESERVED BY NAME above for Guardrails
    // Lite (#823), and GR2077 is RESERVED BY NAME below (issue #587 check B) — none of them is free.
}
