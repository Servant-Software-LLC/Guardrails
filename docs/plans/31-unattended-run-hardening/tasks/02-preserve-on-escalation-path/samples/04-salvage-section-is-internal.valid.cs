// Sample: a CORRECT shape for 04-salvage-section-is-internal.ps1 -> the guardrail must exit 0.
// Stage into a scratch tree at src/Guardrails.Core/Execution/RetryPolicy.cs.
// The trap it carries: a doc comment that NAMES the old private form. The clauses read
// comment-stripped source, so the comment neither satisfies nor trips them.
namespace Guardrails.Core.Execution;

internal static class RetryPolicy
{
    // Was `private static void AppendSalvageSection` before plan 31; widened to internal so
    // PromptComposer can route through the ONE owner of the salvage text (plan section 3.3).
    internal static void AppendSalvageSection(System.Text.StringBuilder text, SalvageRef? salvageRef,
                                              SalvageFraming framing = SalvageFraming.Retry) { }

    internal static void AppendHeader(System.Text.StringBuilder text, bool rolledBack) { }
}
