// Sample: the CORRECT shape 04-generic-brief-unchanged.ps1 must accept -> must exit 0.
//
// Kept COMPLETE rather than a fragment (usings, namespace, type, and both real constructs): an
// incomplete valid sample fails for a DIFFERENT reason and masks the one the check is about. Both
// method declarations are present because the clause SCOPES to the region between them.
//
// This models the state AFTER task 09 lands correctly: the shipped generic brief is byte-for-byte what
// it was, and the new missing-resource brief sits BESIDE it carrying the resource-supply vocabulary.
// That neighbour is the trap the valid half exists to expose - a clause scoped to the FILE instead of
// to BuildDiagnosePrompt's own body would false-RED here, on a correct implementation.
using System.Text;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

public sealed class Overwatch
{
    /// <summary>The NEW missing-resource brief (design 41 section 2.3) - offers resource-supply, and only here.</summary>
    private static string BuildResourceSupplyPrompt(TaskNode task, int attempt, string question) =>
        $"# Overwatch resource supply: task '{task.Id}' (attempt {attempt}, trigger: missing-resource)\n\n" +
        "## The agent's question (UNTRUSTED - the agent wrote this, the harness did not verify it)\n\n" +
        "<<<UNTRUSTED\n" + question + "\nUNTRUSTED\n\n" +
        "These facts were verified by the harness. Do not assert anything about files, tests, other " +
        "tasks or plan-level gates beyond them. Nothing you write reaches the task's next attempt.\n\n" +
        "Return ONLY this JSON object:\n" +
        """{"classification":"retryable|doomed","diagnosis":"<one paragraph>","fixes":[{"kind":"resource-supply","path":"<a path from the candidate table>"}]}""";

    /// <summary>The SHIPPED generic diagnose brief - unchanged.</summary>
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
        """{"kind":"guidance","guidance":"..."} | {"kind":"budget","field":"maxTurns|retries|timeoutSeconds","value":<int>} | {"kind":"file-edit","path":"..."} | {"kind":"task-field","field":"..."}""";

    /// <summary>The journal's per-attempt outcome table for the brief.</summary>
    private static string RenderAttemptHistory(TaskNode task, RunJournal journal)
    {
        var sb = new StringBuilder();
        sb.Append("| attempt | outcome | failed guardrails |\n|---|---|---|\n");
        return sb.ToString();
    }
}
