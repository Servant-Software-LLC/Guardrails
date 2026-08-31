// Sample: the ONE defect 04-codes-and-marker.ps1 exists to catch -> must exit NON-ZERO.
// Stage into a scratch tree at src/Guardrails.Core/Loading/DiagnosticCodes.cs.
// It is the tail of the real file, with this plan's allocation applied: both constants bound to their
// literals, the marker advanced to GR2070, the GR10xx ladder restated, and the three reserved-by-name
// gaps still unallocated.
namespace Guardrails.Core.Loading;

public static class DiagnosticCodes
{
    public const string OpenAiCompatWeakOrUnreachable = "GR2067";
    public const string HandoffPathUnreachable = "GR2068";
    public const string HandoffRowSplitAcrossTasks = "GR2069";
    public const string SomethingElse = "GR2060";

    // CURRENT next-free code: GR2068. GR2069 (HandoffRowSplitAcrossTasks) is the last taken code above.
    // THREE codes remain RESERVED BY NAME in design documents and must not be re-used:
    //   GR2060 - docs/plans/19-producer-coverage.md section 1
    //   GR2061 - docs/plans/18-integration-proof-proximity.md section 3.4
    //   GR2054 - docs/plans/17-model-tiering.md section 13.2, RoutingNumericNonPositive
    //
    // GR10xx: next-free is GR1011. The GR10xx and GR20xx ladders advance independently; a doc that
    // states only one of them is half a fact.
}
