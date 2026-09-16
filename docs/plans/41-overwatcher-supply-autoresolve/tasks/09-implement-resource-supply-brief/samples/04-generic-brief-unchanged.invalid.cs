// Sample: the ONE defect 04-generic-brief-unchanged.ps1 exists to catch -> must exit NON-ZERO.
//
// THE DEFECT: the resource-supply op was added to the SHIPPED generic brief's fix-op shapes line,
// instead of to a new brief of its own. It is the cheapest way to make the new vocabulary reach the
// model, and every test in the pair still passes: they assert what the NEW brief contains, and this
// change is to the OLD one. Nothing fails at run time either - OverwatchFixClassifier routes a
// resource-supply op to Default (propose-only), so it is recorded and never applied. The harness simply
// starts offering, and paying for, a fix vocabulary with no consumer on every eager, short-circuit and
// permission-wall diagnose in every run.
//
// Note the trap it carries, pointing the other way from the valid half: a COMMENT below states the
// generic brief is unchanged. The clauses read comment-stripped source, so the comment neither satisfies
// nor trips them - only the literal does.
using System.Text;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

public sealed class Overwatch
{
    /// <summary>
    /// The generic diagnose brief. Unchanged from the shipped version - the missing-resource vocabulary
    /// is offered in the new brief only (design 41 section 2.3).
    /// </summary>
    private static string BuildDiagnosePrompt(
        OverwatchTrigger trigger, TaskNode task, int attempt, string taskLogDir, RunJournal journal) =>
        $"# Overwatch diagnose: task '{task.Id}' (attempt {attempt}, trigger: {OverwatchTriggers.Token(trigger)})\n\n" +
        $"Task: {task.Description}\n\n" +
        "You are a read-only supervisor. Your ONLY tools are Read, Glob and Grep.\n\n" +
        "## Attempt history (recorded by the harness — authoritative)\n\n" +
        RenderAttemptHistory(task, journal) + "\n" +
        "## Your verdict\n\n" +
        "Decide whether more attempts can plausibly converge (retryable) or the task is structurally " +
        "doomed, and propose ONLY action-layer fixes.\n\n" +
        "Return ONLY this JSON object:\n" +
        """{"classification":"retryable|doomed","diagnosis":"<precise one-paragraph diagnosis>","fixes":[{"kind":"guidance","guidance":"<failure-specific guidance>"}]}""" + "\n\n" +
        "Fix op shapes: " +
        """{"kind":"guidance","guidance":"..."} | {"kind":"budget","field":"maxTurns|retries|timeoutSeconds","value":<int>} | {"kind":"file-edit","path":"..."} | {"kind":"task-field","field":"..."} | {"kind":"resource-supply","path":"..."}""";

    /// <summary>The journal's per-attempt outcome table for the brief.</summary>
    private static string RenderAttemptHistory(TaskNode task, RunJournal journal)
    {
        var sb = new StringBuilder();
        sb.Append("| attempt | outcome | failed guardrails |\n|---|---|---|\n");
        return sb.ToString();
    }
}
