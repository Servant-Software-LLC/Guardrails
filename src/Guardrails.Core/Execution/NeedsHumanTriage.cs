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

    // camelCase + omit-null so the sidecar reads { "diagnosis": ..., "summary": ..., "ghIssueTitle": ... }
    // and drops fields the triage did not supply (e.g. ghIssueTitle for a local-repo diagnosis).
    private static readonly JsonSerializerOptions SidecarOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
    /// <param name="autoFile">
    /// Effective auto-file flag for THIS run, flowed from <see cref="Model.RunConfig.TriageAutoFile"/>
    /// (SSOT §9, Decision 8). Null (the default) falls back to the constructor's value, so existing
    /// callers keep their behavior; <c>TaskExecutor</c> passes the per-run config value explicitly.
    /// </param>
    internal async Task<string?> RunAsync(
        TaskNode task,
        string taskLogDir,
        string planDirectory,
        string workspace,
        CancellationToken ct,
        bool? autoFile = null)
    {
        bool effectiveAutoFile = autoFile ?? _autoFile;
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

        string feedbackContent = BuildFeedbackContent(task, result.ResultText, effectiveAutoFile);
        string feedbackPath = Path.Combine(taskLogDir, "feedback.md");
        File.WriteAllText(feedbackPath, feedbackContent, Encoding.UTF8);

        // Structured sidecar for the console summary (issue #163): when the triage output parses as
        // the documented structured JSON, write a small machine-readable triage.json next to
        // feedback.md so the CLI run summary can surface the root-cause CATEGORY + one-line diagnosis
        // (and ghIssueTitle when present) WITHOUT the user opening each feedback.md. Absent when the
        // triage returned unstructured text — the summary then falls back to the feedback path only.
        WriteTriageSidecar(taskLogDir, result.ResultText);

        return feedbackPath;
    }

    /// <summary>
    /// Parse the triage result and, when it is the documented structured JSON
    /// (<c>{"diagnosis": ..., "ghIssueTitle"/"analysis": ...}</c>), write a compact
    /// <c>triage.json</c> sidecar — <c>{ "diagnosis", "summary", "ghIssueTitle" }</c> — next to
    /// <c>feedback.md</c> for the console summary (issue #163). The <c>summary</c> is a one-line
    /// diagnosis distilled from <c>ghIssueTitle</c> (tool problems) or <c>analysis</c> (local
    /// problems). No-op when the result is not structured (no <c>diagnosis</c> field) so the summary
    /// gracefully falls back. Best-effort: a malformed result or write hiccup is swallowed — the
    /// sidecar is purely advisory, exactly like the rest of triage.
    /// </summary>
    private static void WriteTriageSidecar(string taskLogDir, string resultText)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultText);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("diagnosis", out JsonElement diag)
                || diag.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string? diagnosis = diag.GetString();
            string? ghIssueTitle = root.TryGetProperty("ghIssueTitle", out JsonElement title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()
                : null;
            string? analysis = root.TryGetProperty("analysis", out JsonElement an) && an.ValueKind == JsonValueKind.String
                ? an.GetString()
                : null;

            // One-line diagnosis: the GH-issue title for a tool problem, else the analysis text for a
            // local problem. Either way, collapse to a single line so the summary stays scannable.
            string? oneLine = FirstLine(ghIssueTitle ?? analysis);

            var sidecar = new TriageSummaryDocument
            {
                Diagnosis = diagnosis,
                Summary = oneLine,
                GhIssueTitle = ghIssueTitle
            };
            File.WriteAllText(
                Path.Combine(taskLogDir, "triage.json"),
                JsonSerializer.Serialize(sidecar, SidecarOptions),
                Encoding.UTF8);
        }
        catch (JsonException)
        {
            // Unstructured triage text — no sidecar; the summary falls back to the feedback path.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Advisory sidecar — a logs-tree write hiccup must never affect the run.
        }
    }

    /// <summary>The first non-empty line of <paramref name="text"/>, trimmed; null when blank/null.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string BuildTriagePrompt(TaskNode task) =>
        $"# AI Triage: Task '{task.Id}' Needs Human\n\n" +
        $"Task: {task.Description}\n\n" +
        "Analyze why this task failed and classify the root cause.\n\n" +
        "Return JSON with one of these shapes:\n" +
        """{"diagnosis":"guardrails-tool","ghIssueTitle":"...","ghIssueBody":"..."}""" + "\n" +
        """{"diagnosis":"local-repo","analysis":"..."}""";

    private static string BuildFeedbackContent(TaskNode task, string resultText, bool autoFile)
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
            {
                sb.AppendLine($"**GitHub Issue**: {title.GetString()}");
                // SSOT §9 (Decision 8): auto-file is opt-in. The drafted issue records the mode so
                // the human (and tests) can see whether it was filed or left as a draft.
                sb.AppendLine(autoFile
                    ? "**Auto-file**: enabled — issue will be filed to the configured GH repo."
                    : "**Auto-file**: disabled — drafted only; nothing filed to a remote.");
            }

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

    /// <summary>The <c>triage.json</c> sidecar shape (issue #163): <c>{ diagnosis, summary, ghIssueTitle }</c>.</summary>
    private sealed record TriageSummaryDocument
    {
        public required string? Diagnosis { get; init; }
        public required string? Summary { get; init; }
        public string? GhIssueTitle { get; init; }
    }
}

/// <summary>
/// The structured triage diagnosis surfaced in the run summary (issue #163), read from the
/// <c>triage.json</c> sidecar <see cref="NeedsHumanTriage"/> writes next to <c>feedback.md</c>.
/// <see cref="Diagnosis"/> is the root-cause CATEGORY (e.g. <c>guardrails-tool</c>, <c>local-repo</c>);
/// <see cref="OneLine"/> is a one-line human diagnosis; <see cref="GhIssueTitle"/> is the drafted
/// GH-issue title when the triage produced one (tool diagnoses), else null.
/// </summary>
public sealed record TriageSummary(string Diagnosis, string? OneLine, string? GhIssueTitle);

/// <summary>
/// Reads the <c>triage.json</c> sidecar (issue #163) a needs-human task's triage left in its
/// task-level log dir, so the CLI run summary can surface the root-cause category + one-line
/// diagnosis without opening <c>feedback.md</c>. Read-only and best-effort: a missing/malformed
/// sidecar or an absent <c>diagnosis</c> returns null, and the summary falls back to the feedback
/// pointer alone — exactly the graceful path for an unstructured (or failed) triage.
/// </summary>
public static class TriageSummaryReader
{
    /// <summary>
    /// Try to read <c>&lt;taskLogDir&gt;/triage.json</c> into a <see cref="TriageSummary"/>. Returns
    /// null when the file is absent, unreadable, not valid JSON, or carries no string
    /// <c>diagnosis</c> — never throws.
    /// </summary>
    public static TriageSummary? TryRead(string taskLogDir)
    {
        string path = Path.Combine(taskLogDir, "triage.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("diagnosis", out JsonElement diag)
                || diag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string diagnosis = diag.GetString()!;
            string? oneLine = root.TryGetProperty("summary", out JsonElement s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            string? ghIssueTitle = root.TryGetProperty("ghIssueTitle", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            return new TriageSummary(diagnosis, oneLine, ghIssueTitle);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
