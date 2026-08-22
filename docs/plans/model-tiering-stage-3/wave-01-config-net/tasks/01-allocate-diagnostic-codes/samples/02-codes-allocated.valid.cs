// Two-sided sample pair for guardrails/02-codes-allocated.ps1 (#468/#302).
// VALID half: a correct allocation. Running the guardrail with
//   -SubjectPath <this file>
// must exit 0. This half is the one authors skip and the one that pays: it is the only half that
// exposes a clause that can NEVER match, since under the invalid half everything is failing anyway.
namespace Guardrails.Core.Loading;

public static class DiagnosticCodes
{
    /// <summary>GR2051 — a non-routable block is the registry default pointer.</summary>
    public const string NonRoutableBlockIsDefault = "GR2051";

    /// <summary>GR2052 — a costly block also declares routing, which can never apply.</summary>
    public const string CostlyBlockRoutingInert = "GR2052";

    /// <summary>GR2053 — a full pin and action.tier coexist on one action.</summary>
    public const string PinAndTierCoexist = "GR2053";

    // CURRENT next-free code: GR2065. GR2051–GR2053 were gaps BELOW this counter, so taking them
    // does not advance it.
    // GR2054 remains RESERVED by name in docs/plans/17-model-tiering.md §13.2
    // (RoutingNumericNonPositive) and is the v2 #227 probes code.
}
