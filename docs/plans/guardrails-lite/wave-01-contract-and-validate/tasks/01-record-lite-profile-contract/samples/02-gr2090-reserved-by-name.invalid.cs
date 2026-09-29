namespace Guardrails.Core.Loading;

/// <summary>Diagnostic codes (sample: the ONE defect - GR2090 ALLOCATED as a constant instead of reserved by name).</summary>
public static class DiagnosticCodes
{
    // GR2083 is RESERVED BY NAME for issue #544 (native local-inference actions, the approved
    // docs/plans/544-local-inference-actions.charter.md) and is not yet allocated in master.

    /// <summary>
    /// GR2089 (WARNING) — a prompt runner block's <c>guardrailOverrides</c> carries <c>stallTimeoutSeconds</c> (#811).
    /// </summary>
    public const string StallTimeoutInGuardrailOverrides = "GR2089";

    /// <summary>GR2090 (ERROR) — a plan feature Guardrails Lite does not implement (#823).</summary>
    public const string LiteUnsupportedFeature = "GR2090";

    // CURRENT next-free code: GR2091. GR2089 (StallTimeoutInGuardrailOverrides) is the last taken code
    // above; GR2083 is RESERVED BY NAME above (#544) and GR2077 is RESERVED BY NAME below (issue #587
    // check B) — neither is free.
}
