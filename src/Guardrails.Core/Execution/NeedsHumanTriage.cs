using System.Text;
using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Advisory AI triage step that fires when a task exhausts its retry budget and transitions
/// to <c>needs-human</c> (plan 08 §9, PO Decision 8). Invokes the configured
/// <see cref="IPromptRunner"/> once to analyze the failure, classifies the diagnosis as a
/// Guardrails-tool problem or a local-repo problem, and writes <c>feedback.md</c> to the
/// task-level log directory. Triage is ADVISORY — a thrown runner exception or runner
/// error NEVER changes the task verdict or blocks other tasks.
/// </summary>
public sealed class NeedsHumanTriage
{
    private readonly IPromptRunner _runner;
    private readonly bool _autoFile;

    /// <param name="runner">The prompt runner used to invoke the AI triage analysis.</param>
    /// <param name="autoFile">
    /// When true, auto-file a GH issue for <c>guardrails-tool</c> diagnoses (future feature).
    /// Default false — draft the GH issue into <c>feedback.md</c> only.
    /// </param>
    public NeedsHumanTriage(IPromptRunner runner, bool autoFile = false)
    {
        _runner = runner;
        _autoFile = autoFile;
    }

    /// <summary>
    /// Run the triage analysis for a task that exhausted its retry budget. Returns the
    /// absolute path to the written <c>feedback.md</c>, or null when triage produces no
    /// output (runner error or no result text).
    /// Callers MUST catch all exceptions — this is advisory and must not abort the run.
    /// </summary>
    internal async Task<string?> RunAsync(
        TaskNode task,
        string taskLogDir,
        string planDirectory,
        string workspace,
        CancellationToken ct)
    {
        Directory.CreateDirectory(taskLogDir);

        string prompt = BuildTriagePrompt(task);
        string streamLogPath = Path.Combine(taskLogDir, "triage-stream.jsonl");

        var invocation = new PromptInvocation
        {
            ComposedPrompt = prompt,
            WorkingDirectory = workspace,
            PlanDirectory = planDirectory,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Settings = new PromptRunnerSettings { MaxTurns = 10 },
            Timeout = TimeSpan.FromMinutes(5),
            StreamLogPath = streamLogPath
        };

        PromptResult result = await _runner.RunAsync(invocation, ct).ConfigureAwait(false);

        if (!result.Completed || result.IsError || result.ResultText is null)
            return null;

        string feedbackContent = BuildFeedbackContent(task, result.ResultText);
        string feedbackPath = Path.Combine(taskLogDir, "feedback.md");
        File.WriteAllText(feedbackPath, feedbackContent, Encoding.UTF8);
        return feedbackPath;
    }

    private static string BuildTriagePrompt(TaskNode task) =>
        $"# AI Triage: Task '{task.Id}' Needs Human\n\n" +
        $"Task: {task.Description}\n\n" +
        "Analyze why this task failed and classify the root cause.\n\n" +
        "Return JSON with one of these shapes:\n" +
        """{"diagnosis":"guardrails-tool","ghIssueTitle":"...","ghIssueBody":"..."}""" + "\n" +
        """{"diagnosis":"local-repo","analysis":"..."}""";

    private static string BuildFeedbackContent(TaskNode task, string resultText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# AI Triage: Task '{task.Id}'");
        sb.AppendLine();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultText);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("diagnosis", out JsonElement diag))
                sb.AppendLine($"**Diagnosis**: {diag.GetString()}");

            if (root.TryGetProperty("ghIssueTitle", out JsonElement title))
                sb.AppendLine($"**GitHub Issue**: {title.GetString()}");

            if (root.TryGetProperty("ghIssueBody", out JsonElement body))
            {
                sb.AppendLine();
                sb.AppendLine(body.GetString());
            }

            if (root.TryGetProperty("analysis", out JsonElement analysis))
            {
                sb.AppendLine();
                sb.AppendLine(analysis.GetString());
            }
        }
        catch (JsonException)
        {
            sb.AppendLine(resultText);
        }

        return sb.ToString();
    }
}
