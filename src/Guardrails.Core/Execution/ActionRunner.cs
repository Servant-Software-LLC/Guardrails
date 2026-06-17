using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Runs a task's ACTION and normalizes it into the disposition the attempt loop needs.
/// Script actions go through the interpreter map; prompt actions go through the prompt
/// pipeline (compose → runner → parse). The returned <see cref="ActionRun"/> collapses both
/// shapes into: success, exit code (for the journal), timeout, cost, a needsHuman question (if
/// any), and failure feedback/summary.
/// </summary>
internal sealed class ActionRunner
{
    private readonly PlanDefinition _plan;
    private readonly ScriptUnitRunner _scriptRunner;
    private readonly PromptExecutionSupport _promptSupport;
    private readonly DependencyContextBuilder _dependencyContext;
    private readonly Func<TaskNode, int?, TimeSpan> _resolveTimeout;

    public ActionRunner(
        PlanDefinition plan,
        ScriptUnitRunner scriptRunner,
        PromptExecutionSupport promptSupport,
        DependencyContextBuilder dependencyContext,
        Func<TaskNode, int?, TimeSpan> resolveTimeout)
    {
        _plan = plan;
        _scriptRunner = scriptRunner;
        _promptSupport = promptSupport;
        _dependencyContext = dependencyContext;
        _resolveTimeout = resolveTimeout;
    }

    /// <summary>
    /// Run a task's action. Script actions go through the interpreter map; prompt actions go
    /// through the prompt pipeline (compose → runner → parse). The returned <see cref="ActionRun"/>
    /// normalizes both into the disposition the attempt loop needs: success, exit code (for the
    /// journal), timeout, cost, a needsHuman question (if any), and failure feedback/summary.
    /// </summary>
    public async Task<ActionRun> RunAsync(
        TaskNode task,
        int attemptNumber,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string logDir,
        CancellationToken cancellationToken)
    {
        if (task.Action.Kind != ActionKind.Prompt)
        {
            ProcessResult script = await _scriptRunner.RunAsync(
                task.Action.Path, task.Action.Args, workspace, env,
                _resolveTimeout(task, task.Action.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return ActionRun.FromScript(script, NeedsHumanFrom(fragmentOutPath));
        }

        return await RunPromptActionAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionRun> RunPromptActionAsync(
        TaskNode task,
        int attemptNumber,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string logDir,
        CancellationToken cancellationToken)
    {
        PromptRunnerRegistry registry = _promptSupport.RequireRegistry();
        PromptFile promptFile = PromptExecutionSupport.LoadPromptFile(task.Action.Path);
        PromptRunnerConfig runnerConfig = registry.ResolveConfig(task.Action.Runner ?? promptFile.Frontmatter.Runner);

        IReadOnlyList<DependencyContextRef> dependencies = _dependencyContext.BuildDependencyContext(task);
        IReadOnlyList<PriorAttemptRef> priorAttempts = _dependencyContext.BuildPriorAttempts(task.Id, attemptNumber);
        string composed = PromptComposer.ComposeAction(
            promptFile.Body, snapshotPath, fragmentOutPath, previousFeedbackPath, dependencies, priorAttempts);
        AtomicFile.WriteAllText(Path.Combine(logDir, "composed-prompt.md"), composed);

        PromptRunnerSettings settings = PromptExecutionSupport.ApplyPromptOverrides(
            runnerConfig.EffectiveSettings(isGuardrail: false),
            task.Action.MaxTurns ?? promptFile.Frontmatter.MaxTurns);

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = env,
            Settings = settings,
            Timeout = _resolveTimeout(task, task.Action.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
            StreamLogPath = Path.Combine(logDir, "claude-stream.jsonl"),
            TranscriptLogPath = Path.Combine(logDir, "transcript.md")
        };

        PromptResult result = await registry.Resolve(task.Action.Runner ?? promptFile.Frontmatter.Runner)
            .RunAsync(invocation, cancellationToken).ConfigureAwait(false);

        // A prompt action's fragment may carry the needsHuman escape (SSOT §9).
        string? needsHuman = NeedsHumanFrom(fragmentOutPath);

        return ActionRun.FromPrompt(result, needsHuman);
    }

    /// <summary>
    /// Read the (already-written) action fragment and, if its root is an object with a string
    /// <c>needsHuman</c> key, return the question (SSOT §9). Anything else returns null.
    /// </summary>
    private static string? NeedsHumanFrom(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(fragmentOutPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("needsHuman", out JsonElement question) &&
                question.ValueKind == JsonValueKind.String)
            {
                return question.GetString();
            }
        }
        catch (JsonException)
        {
            // Not parseable JSON → not a needsHuman signal; the merge step will reject it later.
        }

        return null;
    }
}

/// <summary>
/// A normalized view of an action run — script OR prompt — carrying exactly what the
/// attempt loop needs. Scripts map their exit code and timeout directly; prompts map
/// <c>Completed &amp;&amp; !is_error</c> to success (SSOT §9), with cost and the needsHuman escape.
/// </summary>
internal sealed record ActionRun
{
    public required bool Succeeded { get; init; }
    public required int? ExitCode { get; init; }
    public required bool TimedOut { get; init; }
    public decimal? CostUsd { get; init; }
    public string? NeedsHumanQuestion { get; init; }
    public string? FailureFeedback { get; init; }
    public string FailureSummary { get; init; } = "action failed";

    // The action's captured streams. A SCRIPT action carries its real stdout/stderr so the harness
    // can write them to action-stdout.log / action-stderr.log (GUARDRAILS_ACTION_STDOUT/_STDERR,
    // issue #62) and surface stderr in action-failure feedback. A PROMPT action leaves these empty —
    // its "stdout" is the stream-json teed to claude-stream.jsonl, not a plain stream.
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;

    // For log artifacts (action-result.json + action-stdout/stderr.log) we reuse the ProcessResult
    // shape; prompt actions synthesize an exit code (0/1) reflecting success and carry no plain streams.
    public ProcessResult AsProcessResult() => new()
    {
        ExitCode = ExitCode ?? (Succeeded ? 0 : 1),
        StandardOutput = StandardOutput,
        StandardError = StandardError,
        TimedOut = TimedOut,
        Duration = TimeSpan.Zero
    };

    public static ActionRun FromScript(ProcessResult result, string? needsHuman) => new()
    {
        Succeeded = result.Succeeded,
        ExitCode = result.ExitCode,
        TimedOut = result.TimedOut,
        StandardOutput = result.StandardOutput,
        StandardError = result.StandardError,
        NeedsHumanQuestion = needsHuman,
        FailureSummary = result.TimedOut ? "action timed out" : $"action exited {result.ExitCode}"
    };

    public static ActionRun FromPrompt(PromptResult result, string? needsHuman)
    {
        bool succeeded = result.Completed && !result.IsError;
        string? feedback = succeeded ? null : BuildPromptFeedback(result);
        return new ActionRun
        {
            Succeeded = succeeded,
            // Synthesize an exit code for the journal: 0 on success, 1 otherwise.
            ExitCode = succeeded ? 0 : 1,
            TimedOut = result.Summary.Contains("timed out", StringComparison.Ordinal),
            CostUsd = result.CostUsd,
            NeedsHumanQuestion = needsHuman,
            FailureFeedback = feedback,
            FailureSummary = result.Summary
        };
    }

    private static string BuildPromptFeedback(PromptResult result)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# Prompt action did not succeed");
        text.AppendLine();
        text.AppendLine(result.Completed
            ? "The runner completed but reported an error (is_error = true)."
            : $"The runner did not complete cleanly: {result.Summary}.");
        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(result.ResultText))
        {
            text.AppendLine("## Runner result (tail)");
            text.AppendLine("```");
            string tail = result.ResultText!.Length > 2000 ? result.ResultText[^2000..] : result.ResultText;
            text.AppendLine(tail.TrimEnd());
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("Fix the specific problem above on retry; do not start over.");
        return text.ToString();
    }
}
