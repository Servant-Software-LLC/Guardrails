// Sample: a CORRECT shape for 03-salvage-text-has-one-owner.ps1 -> the guardrail must exit 0.
//
// Stage it into a scratch tree at src/Guardrails.Core/Prompts/PromptComposer.cs and point
// GUARDRAILS_WORKSPACE at that tree.
//
// It is deliberately built from the REAL file's traps: it keeps the seven legitimate `git show`
// mentions PromptComposer already owns (the #382 read-route guidance), and it names the forbidden
// heading in a COMMENT. Both must pass. The ban reads comment-stripped source, so the comment is
// invisible to it; and `git show` is not banned at all, because measuring it at 7 in the real file is
// what killed that clause before it shipped.
using System.Text;

namespace Guardrails.Core.Prompts;

internal static class PromptComposer
{
    private static void AppendPreviousAttempt(StringBuilder text, PriorAttemptRef prior)
    {
        text.Append("\n## Previous attempt failed\n\n");
        text.Append("route the harness actually grants you: `git show` to READ, your own file-editing tool ");
        text.Append("2. Test the baseline: `git show \"HEAD:<repo-relative-path>\"` prints that file's ");
        text.Append("Run `git show` exactly as written. You are ALREADY inside the worktree, so a ");

        // Gated on the member being present, so a prior that left no patch gets NO recovery block
        // (pin C4). The heading "Prior attempt work is salvageable" is AppendSalvageSection's and is
        // named here only to explain the routing - the ban reads comment-stripped source.
        if (prior.SalvagePatchPath is not null || prior.SalvageRefName is not null)
        {
            Execution.RetryPolicy.AppendSalvageSection(text, ToSalvageRef(prior), Execution.SalvageFraming.PriorAttempt);
        }
    }
}
