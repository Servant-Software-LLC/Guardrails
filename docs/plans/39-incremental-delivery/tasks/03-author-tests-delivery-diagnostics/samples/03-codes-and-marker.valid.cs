// A COMPLETE file of the shape the clause checks, not a fragment: both new codes DECLARED as
// constants, GR2077 left reserved-by-name in a comment only, and exactly one live next-free
// marker naming GR2080.
namespace Guardrails.Core.Loading;

public static class DiagnosticCodes
{
    public const string CrossTaskClauseCollision = "GR2076";

    // GR2077 — issue #587 check B, UnownedRequiredChange. RESERVED BY NAME, not allocated.

    public const string PostDeliveryWaveMissingEntryPreflight = "GR2078";
    public const string DeliveringWaveHasNoExitGate = "GR2079";

    // A PROSE mention of the marker phrase, quoted. The clause anchors on `//\s*CURRENT` so this
    // must NOT count as a second live marker — the real file carries one of these at line 567.
    // "CURRENT next-free code: GR2047". The design-of-record reserved GR2043-GR2054 earlier.

    // CURRENT next-free code: GR2080. GR2079 is the last taken code above.
}
