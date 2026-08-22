// INVALID half of the pair for guardrails/02-codes-allocated.ps1 (#468/#302).
// The ONE defect it carries: the three codes were allocated, but the reservation block was never
// retired — it still advertises GR2051–GR2054 as reserved-and-free. This compiles, every constant is
// correct, and a build says nothing; the next allocator reads that line, believes GR2051 is free, and
// collides with a code that is already taken. That is precisely the rot DoR §13 records happening
// THREE times to this epic's own reservations.
// Running the guardrail with -SubjectPath <this file> must exit NON-ZERO.
namespace Guardrails.Core.Loading;

public static class DiagnosticCodes
{
    /// <summary>GR2051 — a non-routable block is the registry default pointer.</summary>
    public const string NonRoutableBlockIsDefault = "GR2051";

    /// <summary>GR2052 — a costly block also declares routing, which can never apply.</summary>
    public const string CostlyBlockRoutingInert = "GR2052";

    /// <summary>GR2053 — a full pin and action.tier coexist on one action.</summary>
    public const string PinAndTierCoexist = "GR2053";

    // CURRENT next-free code: GR2065.
    // GR2051–GR2054 also remain RESERVED by name in docs/plans/17-model-tiering.md §13.2
    // (NonRoutableBlockIsDefault / CostlyBlockRoutingInert / PinAndTierCoexist /
    // RoutingNumericNonPositive) and are the next codes the model-tiering epic will take.
}
