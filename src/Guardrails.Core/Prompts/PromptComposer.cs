using System.Text;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Builds the composed prompt (<c>composed-prompt.md</c>, SSOT §8/§9): the prompt body plus
/// appended harness sections. Agents read instructions, not env vars, so every path and
/// contract the prompt needs is embedded in the text. The same composer serves actions and
/// guardrails; the appended sections differ by role.
///
/// Sections:
/// <list type="bullet">
/// <item><c>## Shared state</c> — STATE_IN inlined when ≤ 16 KB, else "read the JSON at &lt;path&gt;".</item>
/// <item>(actions) <c>## Context from completed dependency tasks</c> — transcript/fragment pointers
///   for the transitive <c>dependsOn</c> closure (issue #26 Gap 4); present on every attempt.</item>
/// <item>(actions) <c>## Output contract</c> — write a JSON fragment to STATE_OUT; the needsHuman escape.</item>
/// <item>(actions, attempt ≥ 2) <c>## Previous attempt failed</c> — the latest feedback.md verbatim,
///   plus pointers to ALL prior attempts' transcript/feedback (issue #26 Gaps 2 &amp; 3).</item>
/// <item>(guardrails) <c>## Verdict contract</c> — verifier instructions + the verdict file path.</item>
/// </list>
/// </summary>
public static class PromptComposer
{
    /// <summary>STATE_IN inlining ceiling (SSOT §9): at or below this many bytes it is inlined.</summary>
    public const int StateInlineLimitBytes = 16 * 1024;

    /// <summary>Compose an ACTION prompt.</summary>
    public static string ComposeAction(
        string body,
        string stateInPath,
        string stateOutPath,
        string? feedbackPath,
        IReadOnlyList<DependencyContextRef>? dependencies = null,
        IReadOnlyList<PriorAttemptRef>? priorAttempts = null)
    {
        var text = new StringBuilder();
        AppendBody(text, body);
        AppendSharedState(text, stateInPath);
        AppendDependencyContext(text, dependencies);
        AppendOutputContract(text, stateOutPath);
        AppendPreviousAttempt(text, feedbackPath, priorAttempts);
        return text.ToString();
    }

    /// <summary>Compose a GUARDRAIL (verifier) prompt.</summary>
    public static string ComposeGuardrail(
        string body,
        string stateInPath,
        string verdictOutPath,
        string actionStdoutPath)
    {
        var text = new StringBuilder();
        AppendBody(text, body);
        AppendSharedState(text, stateInPath);
        AppendVerdictContract(text, verdictOutPath, actionStdoutPath);
        return text.ToString();
    }

    private static void AppendBody(StringBuilder text, string body)
    {
        text.Append(body.TrimEnd());
        text.Append('\n');
    }

    private static void AppendSharedState(StringBuilder text, string stateInPath)
    {
        text.Append("\n## Shared state\n\n");

        string content = File.Exists(stateInPath) ? File.ReadAllText(stateInPath) : "{}";
        int bytes = Encoding.UTF8.GetByteCount(content);

        if (bytes <= StateInlineLimitBytes)
        {
            text.Append("Your input state (a snapshot, read-only) is:\n\n```json\n");
            text.Append(content.TrimEnd());
            text.Append("\n```\n");
        }
        else
        {
            text.Append($"Your input state is large ({bytes} bytes). Read the JSON at the absolute path:\n\n");
            text.Append('`').Append(stateInPath).Append("`\n");
        }
    }

    private static void AppendOutputContract(StringBuilder text, string stateOutPath)
    {
        text.Append("\n## Output contract\n\n");
        text.Append("Write your new/changed state as a single JSON object fragment to this absolute path:\n\n");
        text.Append('`').Append(stateOutPath).Append("`\n\n");
        text.Append("Write ONLY your own keys (conventionally namespaced under your task id). Do NOT ");
        text.Append("modify state.json directly — the harness is the single writer and merges your ");
        text.Append("fragment after guardrails pass. If you have nothing to contribute, write nothing.\n\n");
        text.Append("If you cannot proceed without a human decision, write exactly ");
        text.Append("`{ \"needsHuman\": \"<your question>\" }` to that same path and stop — the harness will ");
        text.Append("escalate to a human without burning further retries.\n");
    }

    /// <summary>
    /// The dependency-context section (issue #26 Gap 4): for each transitive <c>dependsOn</c>
    /// ancestor, a pointer to its clean transcript (what it built) and the state fragment it
    /// contributed. Present on every attempt so the FIRST try already knows the project shape,
    /// rather than rediscovering it via Glob/Read. Reading is encouraged but not mandated —
    /// the section is bounded (paths, not inlined content), so it stays cheap even with many
    /// ancestors. Emitted only when there is at least one resolvable ancestor.
    /// </summary>
    private static void AppendDependencyContext(StringBuilder text, IReadOnlyList<DependencyContextRef>? dependencies)
    {
        if (dependencies is null || dependencies.Count == 0)
        {
            return;
        }

        text.Append("\n## Context from completed dependency tasks\n\n");
        text.Append("Your task depends on the tasks below (directly or transitively); they have already ");
        text.Append("completed. Read their transcripts to see exactly what they produced — files, classes, ");
        text.Append("and conventions — instead of rediscovering the project from scratch. These are ");
        text.Append("read-only context, not work to redo.\n\n");

        foreach (DependencyContextRef dependency in dependencies)
        {
            text.Append("- `").Append(dependency.TaskId).Append("` — ").Append(dependency.Description).Append('\n');
            if (dependency.TranscriptPath is { } transcript)
            {
                text.Append("  - What it did: `").Append(transcript).Append("`\n");
            }
            else
            {
                text.Append("  - Logs: `").Append(dependency.LogDir).Append("`\n");
            }

            if (dependency.FragmentPath is { } fragment)
            {
                text.Append("  - State it contributed: `").Append(fragment).Append("`\n");
            }
        }
    }

    /// <summary>
    /// The retry section: the latest <c>feedback.md</c> inlined verbatim (issue feedback loop),
    /// followed by pointers to ALL prior attempts' transcript/feedback so the agent sees the
    /// full arc of what was tried, not only the immediately preceding failure (issue #26 Gaps
    /// 2 &amp; 3). The agent is pointed at the clean <c>transcript.md</c> (what it did) and
    /// <c>feedback.md</c> (why it failed) — never the raw stream.
    /// </summary>
    private static void AppendPreviousAttempt(
        StringBuilder text,
        string? feedbackPath,
        IReadOnlyList<PriorAttemptRef>? priorAttempts)
    {
        bool hasFeedback = feedbackPath is not null && File.Exists(feedbackPath);
        bool hasPriors = priorAttempts is { Count: > 0 };
        if (!hasFeedback && !hasPriors)
        {
            return;
        }

        text.Append("\n## Previous attempt failed\n\n");

        if (hasFeedback)
        {
            string feedback = File.ReadAllText(feedbackPath!).TrimEnd();
            text.Append(feedback);
            text.Append("\n\nThis is a RETRY. Fix these specific problems; do not start over — keep what already ");
            text.Append("works and address only what failed above.\n");
        }

        if (hasPriors)
        {
            text.Append("\n### Prior attempt logs (read-only — inspect for full context)\n\n");
            text.Append("Earlier attempts and their logs, most recent first. Read the transcript to see what ");
            text.Append("each attempt did, and the feedback for why it failed:\n\n");

            foreach (PriorAttemptRef attempt in priorAttempts!)
            {
                text.Append("- Attempt ").Append(attempt.Attempt)
                    .Append(" (").Append(attempt.Outcome).Append("): `").Append(attempt.LogDir).Append("`\n");
                if (attempt.TranscriptPath is { } transcript)
                {
                    text.Append("  - What it did: `").Append(transcript).Append("`\n");
                }

                if (attempt.FeedbackPath is { } feedback)
                {
                    text.Append("  - Why it failed: `").Append(feedback).Append("`\n");
                }
            }
        }
    }

    private static void AppendVerdictContract(StringBuilder text, string verdictOutPath, string actionStdoutPath)
    {
        text.Append("\n## Verdict contract\n\n");
        text.Append("You are a VERIFIER. Do NOT fix, edit, or create anything beyond your verdict file — ");
        text.Append("only judge the criterion above.\n\n");
        text.Append("The action's captured stdout is at this absolute path (read it if your criterion needs it):\n\n");
        text.Append('`').Append(actionStdoutPath).Append("`\n\n");
        text.Append("You MUST end by writing your verdict as a JSON object to this absolute path:\n\n");
        text.Append('`').Append(verdictOutPath).Append("`\n\n");
        text.Append("The verdict shape is `{ \"pass\": <true|false>, \"reason\": \"<one line>\" }`. ");
        text.Append("The reason is shown to a human and (on failure) fed back to the author, so make it ");
        text.Append("specific and actionable. If you cannot determine a verdict, write `pass: false` with ");
        text.Append("a reason explaining why it is undeterminable.\n");
    }
}
