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

        IReadOnlyList<string> passed = results.Where(r => r.Passed).Select(r => r.Name).ToList();
        if (passed.Count > 0)
        {
            text.AppendLine($"Guardrails that PASSED (do not break these): {string.Join(", ", passed)}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Compose feedback when the action succeeded but one or more declared <c>captureHashes</c>
    /// files did not exist afterward (issue #46). The harness records hashes in code, so the only
    /// failure mode is a missing file — name them so the retry creates them at the declared paths.
    /// </summary>
    public static string ForMissingCaptureFiles(TaskNode task, int attempt, IReadOnlyList<string> missingFiles)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Declared output file(s) missing");
        text.AppendLine("The action reported success, but these files declared in this task's");
        text.AppendLine("`captureHashes` do not exist at the expected workspace-relative paths:");
        text.AppendLine();
        foreach (string missing in missingFiles)
        {
            text.AppendLine($"- {missing}");
        }

        text.AppendLine();
        text.AppendLine("Create each file at exactly that path. The harness hashes them automatically");
        text.AppendLine("once they exist — you do NOT need to compute or write any hash yourself.");
        return text.ToString();
    }

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
