// The cheapest wrong implementation the clause exists to reject: the tests were written against
// the string literals, this file was never opened. No constants, and the marker still points at
// a code this plan has taken.
namespace Guardrails.Core.Loading;

public static class DiagnosticCodes
{
    public const string CrossTaskClauseCollision = "GR2076";

    // GR2077 — issue #587 check B, UnownedRequiredChange. RESERVED BY NAME, not allocated.

    // CURRENT next-free code: GR2078. GR2076 is the last taken code above.
}
