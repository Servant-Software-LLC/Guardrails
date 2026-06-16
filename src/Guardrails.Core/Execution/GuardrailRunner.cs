using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Runs a task's guardrails for one attempt (SSOT §4/§7): each guardrail in order, script or
/// prompt, observing failFast per <see cref="RunConfig.GuardrailMode"/>. Script guardrails
/// pass/fail by exit code; prompt guardrails pass/fail SOLELY by their verdict file (never the
/// runner exit code). The aggregated <see cref="GuardrailRunResult"/> tells the attempt loop
/// whether any failed and whether a timeout was involved.
/// </summary>
internal sealed class GuardrailRunner
{
    private readonly PlanDefinition _plan;
    private readonly IRunObserver _observer;
    private readonly ScriptUnitRunner _scriptRunner;
    private readonly PromptExecutionSupport _promptSupport;
    private readonly Func<TaskNode, int?, TimeSpan> _resolveTimeout;

    public GuardrailRunner(
        PlanDefinition plan,
        IRunObserver observer,
        ScriptUnitRunner scriptRunner,
        PromptExecutionSupport promptSupport,
        Func<TaskNode, int?, TimeSpan> resolveTimeout)
    {
        _plan = plan;
        _observer = observer;
        _scriptRunner = scriptRunner;
        _promptSupport = promptSupport;
        _resolveTimeout = resolveTimeout;
    }

    public async Task<GuardrailRunResult> RunAsync(
        TaskNode task,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string logDir,
        CancellationToken cancellationToken)
    {
        var results = new List<GuardrailResult>(task.Guardrails.Count);
        bool anyFailed = false;
        bool timedOut = false;

        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            (GuardrailResult result, bool guardrailTimedOut) = guardrail.Kind == ActionKind.Prompt
                ? await RunPromptGuardrailAsync(task, guardrail, workspace, env, snapshotPath, logDir, cancellationToken).ConfigureAwait(false)
                : await RunScriptGuardrailAsync(task, guardrail, workspace, env, logDir, cancellationToken).ConfigureAwait(false);

            results.Add(result);
            _observer.GuardrailFinished(task, result);

            if (cancellationToken.IsCancellationRequested)
            {
                break; // the caller turns this into a cancelled attempt
            }

            if (!result.Passed)
            {
                anyFailed = true;
                timedOut |= guardrailTimedOut;
                if (_plan.Config.GuardrailMode == GuardrailMode.FailFast)
                {
                    break;
                }
            }
        }

        return new GuardrailRunResult { Results = results, AnyFailed = anyFailed, TimedOut = timedOut };
    }

    private async Task<(GuardrailResult Result, bool TimedOut)> RunScriptGuardrailAsync(
        TaskNode task,
        GuardrailDefinition guardrail,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string logDir,
        CancellationToken cancellationToken)
    {
        ProcessResult processResult = await _scriptRunner.RunAsync(
            guardrail.Path, guardrail.Args, workspace, env,
            _resolveTimeout(task, guardrail.TimeoutSeconds), cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteGuardrailLogs(logDir, guardrail.Name, processResult);
        return (ToGuardrailResult(guardrail, processResult), processResult.TimedOut);
    }

    /// <summary>
    /// Run a PROMPT guardrail (SSOT §4.2/§9): compose the verifier prompt, set
    /// <c>GUARDRAILS_VERDICT_OUT</c>, invoke the runner (guardrail-overrides profile), then
    /// judge pass/fail SOLELY by the verdict file — never the runner's exit code. Missing or
    /// invalid verdict ⇒ fail with the contractual reason.
    /// </summary>
    private async Task<(GuardrailResult Result, bool TimedOut)> RunPromptGuardrailAsync(
        TaskNode task,
        GuardrailDefinition guardrail,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string logDir,
        CancellationToken cancellationToken)
    {
        PromptRunnerRegistry registry = _promptSupport.RequireRegistry();
        PromptFile promptFile = PromptExecutionSupport.LoadPromptFile(guardrail.Path);
        PromptRunnerConfig runnerConfig = registry.ResolveConfig(promptFile.Frontmatter.Runner);

        string verdictPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.verdict.json");
        string actionStdoutPath = env.TryGetValue("GUARDRAILS_ACTION_STDOUT", out string? stdoutPath)
            ? stdoutPath
            : Path.Combine(logDir, "action-stdout.log");

        string composed = PromptComposer.ComposeGuardrail(promptFile.Body, snapshotPath, verdictPath, actionStdoutPath);
        AtomicFile.WriteAllText(Path.Combine(logDir, $"composed-prompt.{Sanitize(guardrail.Name)}.md"), composed);

        var guardrailEnv = new Dictionary<string, string>(env, StringComparer.Ordinal)
        {
            ["GUARDRAILS_VERDICT_OUT"] = verdictPath
        };

        PromptRunnerSettings settings = PromptExecutionSupport.ApplyPromptOverrides(
            runnerConfig.EffectiveSettings(isGuardrail: true),
            promptFile.Frontmatter.MaxTurns);

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = guardrailEnv,
            Settings = settings,
            Timeout = _resolveTimeout(task, guardrail.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
            StreamLogPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.stream.jsonl"),
            TranscriptLogPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.transcript.md")
        };

        PromptResult promptResult = await registry.Resolve(promptFile.Frontmatter.Runner)
            .RunAsync(invocation, cancellationToken).ConfigureAwait(false);

        // Pass/fail is the verdict file, full stop (NEVER the exit code).
        GuardrailVerdict verdict = GuardrailVerdictReader.Read(verdictPath);
        string reason = string.IsNullOrWhiteSpace(verdict.Reason)
            ? (verdict.Pass ? "passed" : GuardrailVerdictReader.NoValidVerdictReason)
            : verdict.Reason;

        var result = new GuardrailResult
        {
            Name = guardrail.Name,
            Passed = verdict.Pass,
            Reason = verdict.Pass ? null : reason
        };

        // The prompt guardrail's stdout/stderr are not the verdict, but tee them for audit
        // (the runner already teed its stream; capture nothing more here). Timeouts surface
        // as "did not complete" → no verdict → fail, which the reader already handled.
        return (result, !promptResult.Completed && promptResult.Summary.Contains("timed out", StringComparison.Ordinal));
    }

    private static GuardrailResult ToGuardrailResult(GuardrailDefinition guardrail, ProcessResult result)
    {
        if (result.Succeeded)
        {
            return new GuardrailResult { Name = guardrail.Name, Passed = true };
        }

        string reason = result.TimedOut
            ? "guardrail timed out"
            : FirstNonEmptyLine(result.StandardOutput)
              ?? FirstNonEmptyLine(result.StandardError)
              ?? $"exit code {result.ExitCode}";

        // The one-line reason feeds the UI/journal; the FULL output feeds retry feedback
        // (issue #26 Gap 1) so a multi-error failure surfaces every error, not just the first.
        string? output = FirstNonEmptyLine(result.StandardOutput) is not null
            ? result.StandardOutput
            : (FirstNonEmptyLine(result.StandardError) is not null ? result.StandardError : null);

        return new GuardrailResult { Name = guardrail.Name, Passed = false, Reason = reason, Output = output };
    }

    private static string? FirstNonEmptyLine(string text)
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

    private static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }
}

/// <summary>The outcome of a single attempt's guardrail pass.</summary>
internal sealed record GuardrailRunResult
{
    public required IReadOnlyList<GuardrailResult> Results { get; init; }
    public required bool AnyFailed { get; init; }
    public required bool TimedOut { get; init; }
}
