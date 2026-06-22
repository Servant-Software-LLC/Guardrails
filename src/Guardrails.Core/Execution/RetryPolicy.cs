using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Composes the <c>feedback.md</c> written after a failed attempt (SSOT §8). The text is
/// the retry's input: deterministic actions just re-run, but prompt actions receive it
/// verbatim via <c>GUARDRAILS_FEEDBACK</c> / the composed prompt, so it must be specific
/// and actionable — guardrail names, their reasons, and output tails, never just "failed".
/// </summary>
public static class RetryPolicy
{
    private const int TailLines = 60;
    private const int TailChars = 4000;

    /// <summary>Compose feedback for an attempt whose ACTION failed (guardrails were skipped).</summary>
    public static string ForActionFailure(TaskNode task, int attempt, ProcessResult action)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## What failed");
        text.AppendLine(action.TimedOut
            ? "The action timed out and was killed. Guardrails were skipped."
            : $"The action exited with code {action.ExitCode}. Guardrails were skipped.");
        AppendTail(text, "Action stderr (tail)", action.StandardError);
        AppendTail(text, "Action stdout (tail)", action.StandardOutput);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a prompt action that exceeded the runner's OUTPUT-TOKEN cap (issue #114).
    /// The retry must CHANGE BEHAVIOR — a re-run with the identical config just re-hits the same wall —
    /// so the feedback is actionable: split the work, write the file with small incremental edits, and
    /// keep reasoning terse. Distinct from a generic action failure so a human (and §9 triage) sees a
    /// tool/budget issue, not "the agent failed".
    /// </summary>
    public static string ForOutputCapExceeded(TaskNode task, int attempt)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Response truncated at the output-token cap");
        text.AppendLine();
        text.AppendLine("Your previous response exceeded the runner's output-token cap, so it was cut off and");
        text.AppendLine("NOTHING was written. Re-running the same way will hit the same wall. CHANGE your");
        text.AppendLine("approach on this attempt:");
        text.AppendLine();
        text.AppendLine("- Write each file with SMALL, INCREMENTAL edits (one tool call per file/section), not");
        text.AppendLine("  one giant response containing the whole file.");
        text.AppendLine("- Keep prose/reasoning terse — spend the output budget on the deliverable, not narration.");
        text.AppendLine("- If the task genuinely needs more than one response's worth of output, split it: produce");
        text.AppendLine("  the most important part first, then continue in subsequent turns.");
        text.AppendLine("- If the deliverable is inherently too large to produce within the cap, STOP and write");
        text.AppendLine("  {\"needsHuman\": \"<why this task is too large for the output cap>\"} to GUARDRAILS_STATE_OUT.");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a prompt/script action that TIMED OUT (issue #119). A timeout means the
    /// task needed more wall-clock, and the partial work is PRESERVED in the segment worktree — so the
    /// retry must continue from it, not re-explore from scratch (the wasteful "15 reads, 0 edits" retry
    /// the issue documents). The harness also extends the retry's clock (see <c>TaskExecutor</c>).
    /// </summary>
    public static string ForTimeout(TaskNode task, int attempt)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## The previous attempt timed out");
        text.AppendLine();
        text.AppendLine("The previous attempt ran out of time and was stopped. Its PARTIAL WORK is preserved in");
        text.AppendLine("your workspace — do NOT start over and do NOT re-read the whole codebase to re-orient.");
        text.AppendLine();
        text.AppendLine("- CONTINUE from the partial work already on disk; build on it.");
        text.AppendLine("- Prioritise getting the change to COMPILE and the guardrails to GO GREEN first; refine after.");
        text.AppendLine("- Make focused edits — minimise exploration, maximise progress, because the clock matters.");
        text.AppendLine("- If this task bundles several distinct sub-features and cannot finish in the time given,");
        text.AppendLine("  STOP and write {\"needsHuman\": \"<this task is under-sized for the timeout; suggest a split>\"}");
        text.AppendLine("  to GUARDRAILS_STATE_OUT rather than burning more attempts.");
        return text.ToString();
    }

    /// <summary>Compose feedback for an attempt where one or more GUARDRAILS failed.</summary>
    public static string ForGuardrailFailures(
        TaskNode task,
        int attempt,
        IReadOnlyList<GuardrailResult> results)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Failed guardrails");

        foreach (GuardrailResult failed in results.Where(r => !r.Passed))
        {
            GuardrailDefinition? definition = task.Guardrails.FirstOrDefault(g => g.Name == failed.Name);
            text.AppendLine($"### {failed.Name}");
            if (!string.IsNullOrWhiteSpace(definition?.Description))
            {
                text.AppendLine($"Checks: {definition.Description}");
            }

            text.AppendLine($"Reason: {failed.Reason ?? "guardrail failed (no reason printed)"}");

            // The one-line reason is the FIRST line only; include the full (tail-bounded) output
            // so a multi-error failure shows every error, not just the first (issue #26 Gap 1).
            // Skipped when the output is just the reason line again (no extra signal).
            if (HasMoreThanReason(failed.Output, failed.Reason))
            {
                AppendTail(text, "Full output (tail)", failed.Output!);
            }

            text.AppendLine();
        }

        // When a tests-untouched guardrail failed, the agent edited the authored test file (almost
        // always to force a tests-pass guardrail green). The harness has restored that file to its
        // authored baseline for the next attempt (issue #51), so steer the agent to fix the
        // IMPLEMENTATION and emit the "Do NOT edit the test file(s)" block, then RETURN.
        //
        // Returning here suppresses the WHOLE "Guardrails that PASSED (do not break these)" footer —
        // not just a tests-pass entry. That is deliberate: a tests-pass guardrail that went green by
        // editing the tests is exactly what must NOT be preserved, and after restore the passing set
        // is recomputed next attempt anyway, so listing "do not break these" here would be misleading.
        bool testsUntouchedFailed = results.Any(r => !r.Passed && IsTestsUntouched(r.Name));
        if (testsUntouchedFailed)
        {
            text.AppendLine("## Do NOT edit the test file(s)");
            text.AppendLine("A `tests-untouched` guardrail failed: the authored test file was modified. The harness");
            text.AppendLine("has restored each affected test file to its authored baseline for this attempt — it is");
            text.AppendLine("pristine again. Make the ORIGINAL tests pass by fixing the implementation; do not change");
            text.AppendLine("the tests. If the authored tests are genuinely wrong or incompatible with a reasonable");
            text.AppendLine("implementation, STOP and write {\"needsHuman\": \"<why>\"} to GUARDRAILS_STATE_OUT instead");
            text.AppendLine("of editing them.");
            return text.ToString();
        }

        IReadOnlyList<string> passed = results.Where(r => r.Passed).Select(r => r.Name).ToList();
        if (passed.Count > 0)
        {
            text.AppendLine($"Guardrails that PASSED (do not break these): {string.Join(", ", passed)}");
        }

        return text.ToString();
    }

    /// <summary>A guardrail whose name marks it as a tests-untouched check (doctrine: <c>NN-tests-untouched</c>).</summary>
    private static bool IsTestsUntouched(string name) =>
        name.Contains("untouched", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compose feedback for an attempt rejected because its state fragment was invalid (SSOT §6.2).</summary>
    public static string ForInvalidFragment(TaskNode task, int attempt, string reason)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Invalid state fragment");
        text.AppendLine(reason);
        text.AppendLine();
        text.AppendLine("The file written to GUARDRAILS_STATE_OUT must be a single JSON object, e.g.");
        text.AppendLine($"`{{ \"{task.Id}\": {{ \"someKey\": \"someValue\" }} }}`.");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for an attempt rejected because its state fragment carried a top-level key
    /// the task does not own — a foreign task id or an arbitrary shared key (SSOT §6.2,
    /// single-writer-per-key, issue #48). Names the exact offending key(s) so a confused (non-malicious)
    /// agent drops the stray key on retry and writes ONLY under its own id.
    /// </summary>
    public static string ForForeignKey(TaskNode task, int attempt, IReadOnlyList<string> foreignKeys)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## State fragment wrote a key this task does not own");
        text.AppendLine();
        foreach (string key in foreignKeys)
        {
            text.AppendLine($"- top-level key '{key}' is not owned by this task");
        }

        text.AppendLine();
        text.AppendLine($"A task may only write state under its OWN id, '{task.Id}'. The harness is the");
        text.AppendLine("single writer of every namespace, so writing under another task's id (or any");
        text.AppendLine("shared key) is rejected and NOTHING is merged. Remove the stray top-level key(s)");
        text.AppendLine("above and nest everything you publish under your own id, e.g.");
        text.AppendLine($"`{{ \"{task.Id}\": {{ \"someKey\": \"someValue\" }} }}`.");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for an attempt rejected by the write-scope check (plan 08 §2/§3.4).
    /// Names each offending path so the agent removes the out-of-scope change on retry. The
    /// harness has already performed a scoped revert of the offending paths before calling this.
    /// </summary>
    public static string ForWriteScopeViolation(TaskNode task, int attempt, IReadOnlyList<string> offendingPaths)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Write-scope violation");
        text.AppendLine();
        text.AppendLine("The following path(s) were modified but fall OUTSIDE this task's declared writeScope:");
        foreach (string path in offendingPaths)
        {
            text.AppendLine($"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("The harness has already reverted those files to their pre-attempt state. Your");
        text.AppendLine("in-scope changes are preserved. On retry, ensure you only write to paths covered");
        text.AppendLine("by this task's writeScope (SSOT §3.4, plan 08 §2).");
        return text.ToString();
    }

    /// <summary>
    /// True when <paramref name="output"/> carries more than the one-line <paramref name="reason"/>
    /// already shown — i.e. it is non-empty and not just the reason line repeated.
    /// </summary>
    private static bool HasMoreThanReason(string? output, string? reason)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        string trimmed = output.Trim();
        return !string.Equals(trimmed, reason?.Trim(), StringComparison.Ordinal);
    }

    private static void AppendHeader(StringBuilder text, TaskNode task, int attempt)
    {
        text.AppendLine($"# Attempt {attempt} of task '{task.Id}' failed");
        text.AppendLine();
        text.AppendLine($"Task: {task.Description}");
        text.AppendLine();
        text.AppendLine("Fix the specific problems below. Do NOT start over from scratch — keep what");
        text.AppendLine("already works and address only what failed.");
        text.AppendLine();
    }

    private static void AppendTail(StringBuilder text, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        string[] lines = content.TrimEnd().Split('\n');
        IEnumerable<string> tail = lines.Length > TailLines ? lines[^TailLines..] : lines;
        string joined = string.Join('\n', tail);
        if (joined.Length > TailChars)
        {
            joined = joined[^TailChars..];
        }

        text.AppendLine($"## {title}");
        text.AppendLine("```");
        text.AppendLine(joined.TrimEnd('\r', '\n'));
        text.AppendLine("```");
    }
}
