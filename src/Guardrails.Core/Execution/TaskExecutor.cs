using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// Runs ONE task through its retry lifecycle (SSOT §3/§6/§7/§8): for each attempt —
/// snapshot state, run the action (a failed action skips guardrails), run guardrails
/// (failFast per config), merge the fragment only after every guardrail passes. On
/// failure, compose <c>feedback.md</c> (the next attempt receives its path via
/// <c>GUARDRAILS_FEEDBACK</c>) and retry until the budget — <c>1 + retries</c> — is
/// exhausted, which journals the task <c>needs-human</c>. Cancellation journals the
/// task back to <c>pending</c> so a resumed run picks it up cleanly.
/// </summary>
public sealed class TaskExecutor : ITaskExecutor
{
    private readonly PlanDefinition _plan;
    private readonly ProcessRunner _processRunner;
    private readonly InterpreterMap _interpreterMap;
    private readonly StateManager _stateManager;
    private readonly RunJournal _journal;
    private readonly IRunObserver _observer;
    private readonly PromptRunnerRegistry? _promptRunners;

    public TaskExecutor(
        PlanDefinition plan,
        ProcessRunner processRunner,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        IRunObserver observer,
        PromptRunnerRegistry? promptRunners = null)
    {
        _plan = plan;
        _processRunner = processRunner;
        _interpreterMap = interpreterMap;
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer;
        _promptRunners = promptRunners;
    }

    /// <inheritdoc />
    public async Task<TaskResult> ExecuteAsync(TaskNode task, CancellationToken cancellationToken)
    {
        var taskStartedAt = DateTimeOffset.UtcNow;
        _observer.TaskStarting(task);
        _journal.MarkRunning(task.Id);

        int budget = 1 + (task.Retries ?? _plan.Config.DefaultRetries);
        string? feedbackPath = null;
        TaskResult last = null!;

        for (int attemptIndex = 1; attemptIndex <= budget; attemptIndex++)
        {
            bool isFinal = attemptIndex == budget;
            int attemptNumber = _journal.NextAttemptNumber(task.Id);
            _observer.AttemptStarting(task, attemptIndex, budget);

            AttemptResult attempt = await RunAttemptAsync(task, attemptNumber, feedbackPath, isFinal, cancellationToken)
                .ConfigureAwait(false);
            last = attempt.Result;

            // On success, stamp the summary with how long the task took (including any retries)
            // and the wall-clock completion time, so an unattended/overnight run can be reviewed
            // in the morning. Display-only — the journal already records per-attempt start/end.
            if (attempt.Result.Outcome is TaskOutcome.Succeeded)
            {
                return attempt.Result with
                {
                    Summary = $"{attempt.Result.Summary}; took {FormatDuration(DateTimeOffset.UtcNow - taskStartedAt)}, " +
                              $"done {DateTimeOffset.Now:HH:mm:ss}"
                };
            }

            // Other terminal outcomes do not retry: cancellation, plus the prompt-action needsHuman
            // short-circuit (SSOT §9) which escalates immediately with no retry burn.
            if (attempt.Result.Outcome is TaskOutcome.Cancelled or TaskOutcome.NeedsHuman)
            {
                return attempt.Result;
            }

            feedbackPath = attempt.FeedbackPath;
        }

        return last with { Summary = $"{last.Summary} — needs human after {budget} attempt(s)" };
    }

    /// <summary>
    /// Compact human-readable duration for the success summary: <c>43s</c>, <c>2m13s</c>,
    /// <c>1h04m</c>. Sub-minute keeps one decimal under 10s (<c>3.4s</c>) and whole seconds above.
    /// </summary>
    internal static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        }

        if (d.TotalMinutes >= 1)
        {
            return $"{(int)d.TotalMinutes}m{d.Seconds:D2}s";
        }

        return d.TotalSeconds < 10
            ? $"{d.TotalSeconds:0.#}s"
            : $"{(int)d.TotalSeconds}s";
    }

    private async Task<AttemptResult> RunAttemptAsync(
        TaskNode task,
        int attemptNumber,
        string? previousFeedbackPath,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        string logDir = AttemptLogDir(task.Id, attemptNumber);
        Directory.CreateDirectory(logDir);
        string relativeLogDir = RelativeLogDir(task.Id, attemptNumber);

        string snapshotPath = _stateManager.CreateSnapshot(logDir);
        string fragmentOutPath = Path.Combine(logDir, "action-out-fragment.json");

        IReadOnlyDictionary<string, string> env = BuildEnvironment(
            task, attemptNumber, logDir, snapshotPath, fragmentOutPath, previousFeedbackPath);
        string workspace = ResolveWorkingDirectory(task);

        // --- action (script or prompt) --------------------------------------------------
        ActionRun action = await RunActionAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteActionLogs(logDir, action.AsProcessResult(), ActionKindLabel(task));

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(), action.CostUsd);
        }

        // --- needsHuman short-circuit (SSOT §9): record + escalate IMMEDIATELY -----------
        if (action.NeedsHumanQuestion is { } question)
        {
            return NeedsHuman(task, attemptNumber, startedAt, relativeLogDir, logDir, action, question);
        }

        if (!action.Succeeded)
        {
            string feedback = action.FailureFeedback
                ?? RetryPolicy.ForActionFailure(task, attemptNumber, action.AsProcessResult());
            return FailedAttempt(
                task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                action.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.ActionFailed,
                new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.ActionFailed,
                    ActionExitCode = action.ExitCode,
                    Summary = $"{action.FailureSummary}; guardrails skipped"
                },
                costUsd: action.CostUsd);
        }

        // --- guardrails -----------------------------------------------------------------
        IReadOnlyDictionary<string, string> guardrailEnv = BuildGuardrailEnvironment(env, logDir, fragmentOutPath);
        GuardrailRunResult guardrails = await RunGuardrailsAsync(
            task, workspace, guardrailEnv, snapshotPath, logDir, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(), action.CostUsd);
        }

        if (guardrails.AnyFailed)
        {
            IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();
            string feedback = RetryPolicy.ForGuardrailFailures(task, attemptNumber, guardrails.Results);
            return FailedAttempt(
                task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                guardrails.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.GuardrailFailed,
                new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.GuardrailFailed,
                    ActionExitCode = action.ExitCode,
                    Guardrails = guardrails.Results,
                    Summary = $"guardrail(s) failed: {string.Join(", ", failed.Select(g => g.Name))}"
                },
                failed.Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "guardrail failed" }).ToList(),
                costUsd: action.CostUsd);
        }

        // --- merge fragment (only after every guardrail passed) --------------------------
        return CompleteSucceededOrInvalidFragment(
            task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal);
    }

    // --- action dispatch (script or prompt) ----------------------------------------------

    /// <summary>
    /// Run a task's action. Script actions go through the interpreter map; prompt actions go
    /// through the prompt pipeline (compose → runner → parse). The returned <see cref="ActionRun"/>
    /// normalizes both into the disposition the attempt loop needs: success, exit code (for the
    /// journal), timeout, cost, a needsHuman question (if any), and failure feedback/summary.
    /// </summary>
    private async Task<ActionRun> RunActionAsync(
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
            ProcessResult script = await RunUnitAsync(
                task.Action.Path, task.Action.Args, workspace, env,
                ResolveTimeout(task, task.Action.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
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
        PromptRunnerRegistry registry = RequireRegistry();
        PromptFile promptFile = LoadPromptFile(task.Action.Path);
        PromptRunnerConfig runnerConfig = registry.ResolveConfig(task.Action.Runner ?? promptFile.Frontmatter.Runner);

        string composed = PromptComposer.ComposeAction(promptFile.Body, snapshotPath, fragmentOutPath, previousFeedbackPath);
        AtomicFile.WriteAllText(Path.Combine(logDir, "composed-prompt.md"), composed);

        PromptRunnerSettings settings = ApplyPromptOverrides(
            runnerConfig.EffectiveSettings(isGuardrail: false),
            task.Action.MaxTurns ?? promptFile.Frontmatter.MaxTurns);

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = env,
            Settings = settings,
            Timeout = ResolveTimeout(task, task.Action.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
            StreamLogPath = Path.Combine(logDir, "claude-stream.jsonl")
        };

        PromptResult result = await registry.Resolve(task.Action.Runner ?? promptFile.Frontmatter.Runner)
            .RunAsync(invocation, cancellationToken).ConfigureAwait(false);

        // A prompt action's fragment may carry the needsHuman escape (SSOT §9).
        string? needsHuman = NeedsHumanFrom(fragmentOutPath);

        return ActionRun.FromPrompt(result, needsHuman);
    }

    private AttemptResult CompleteSucceededOrInvalidFragment(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal)
    {
        long? mergeSequence = null;

        if (File.Exists(fragmentOutPath))
        {
            long reserved = _journal.ReserveMergeSequence();
            MergeFragmentResult merge = _stateManager.MergeFragment(task.Id, fragmentOutPath, reserved, logDir);

            if (!merge.Merged)
            {
                string reason = merge.Reason ?? "invalid state fragment";
                string feedback = RetryPolicy.ForInvalidFragment(task, attemptNumber, reason);
                return FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.InvalidFragment,
                        ActionExitCode = action.ExitCode,
                        Guardrails = guardrails.Results,
                        Summary = reason
                    },
                    costUsd: action.CostUsd);
            }

            mergeSequence = reserved;
        }

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.Succeeded,
            CostUsd = action.CostUsd,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed"
                      + (action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "")
                      + (mergeSequence is null ? "" : $"; merged (seq {mergeSequence})")
        }, FeedbackPath: null);
    }

    /// <summary>
    /// Record a failed attempt: write <c>feedback.md</c> into the attempt's log dir, journal
    /// the attempt (non-final attempts keep status <c>running</c>; the final one goes
    /// <c>needs-human</c>), and hand the feedback path to the next attempt.
    /// </summary>
    private AttemptResult FailedAttempt(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string feedback,
        bool isFinal,
        AttemptOutcome outcome,
        TaskResult result,
        IReadOnlyList<FailedGuardrail>? failedGuardrails = null,
        decimal? costUsd = null)
    {
        string feedbackPath = Path.Combine(logDir, "feedback.md");
        AtomicFile.WriteAllText(feedbackPath, feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = result.ActionExitCode,
            Outcome = outcome,
            FailedGuardrails = failedGuardrails ?? [],
            CostUsd = costUsd,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, isFinal ? JournalTaskStatus.NeedsHuman : JournalTaskStatus.Running);

        return new AttemptResult(result, feedbackPath);
    }

    /// <summary>
    /// The needsHuman short-circuit (SSOT §9): a prompt action wrote a root <c>needsHuman</c>
    /// key to its fragment. Record the attempt with the <c>needs-human</c> outcome and journal
    /// the task <c>needs-human</c> immediately — no retry, no guardrails. Returns a non-green
    /// result so the scheduler blocks dependents.
    /// </summary>
    private AttemptResult NeedsHuman(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        string question)
    {
        string feedback =
            $"# Task '{task.Id}' needs a human\n\n" +
            $"Task: {task.Description}\n\n" +
            $"The prompt action signalled it cannot proceed without a human decision:\n\n> {question}\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.NeedsHuman,
            CostUsd = action.CostUsd,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            Summary = $"needs human: {question}"
        }, FeedbackPath: null);
    }

    private AttemptResult Cancelled(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult,
        decimal? costUsd)
    {
        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = AttemptOutcome.Cancelled,
            CostUsd = costUsd,
            LogDir = relativeLogDir
        };

        // Back to pending: a resumed run re-attempts this task (SSOT §7 resume rules).
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Pending);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Cancelled,
            ActionExitCode = actionResult.ExitCode,
            Summary = "cancelled mid-attempt; journaled back to pending"
        }, FeedbackPath: null);
    }

    private async Task<GuardrailRunResult> RunGuardrailsAsync(
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
        ProcessResult processResult = await RunUnitAsync(
            guardrail.Path, guardrail.Args, workspace, env,
            ResolveTimeout(task, guardrail.TimeoutSeconds), cancellationToken).ConfigureAwait(false);

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
        PromptRunnerRegistry registry = RequireRegistry();
        PromptFile promptFile = LoadPromptFile(guardrail.Path);
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

        PromptRunnerSettings settings = ApplyPromptOverrides(
            runnerConfig.EffectiveSettings(isGuardrail: true),
            promptFile.Frontmatter.MaxTurns);

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = guardrailEnv,
            Settings = settings,
            Timeout = ResolveTimeout(task, guardrail.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
            StreamLogPath = Path.Combine(logDir, $"guardrail-{Sanitize(guardrail.Name)}.stream.jsonl")
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

    private async Task<ProcessResult> RunUnitAsync(
        string scriptPath,
        IReadOnlyList<string> args,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        InterpreterMap.Resolution resolution = _interpreterMap.Resolve(scriptPath, args);
        if (resolution.Status != InterpreterMap.Status.Resolved || resolution.Command is null)
        {
            // Validation should have caught this; surface it as a failed run rather than crash.
            return new ProcessResult
            {
                ExitCode = ProcessRunner.TimeoutExitCode,
                StandardOutput = string.Empty,
                StandardError = $"no interpreter resolved for '{scriptPath}' ({resolution.Status})",
                TimedOut = false,
                Duration = TimeSpan.Zero
            };
        }

        return await _processRunner
            .RunAsync(resolution.Command, workspace, env, timeout, cancellationToken)
            .ConfigureAwait(false);
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

        return new GuardrailResult { Name = guardrail.Name, Passed = false, Reason = reason };
    }

    // --- log paths -----------------------------------------------------------------------

    private string AttemptLogDir(string taskId, int attempt) =>
        Path.Combine(_plan.PlanDirectory, "state", "logs", taskId, $"attempt-{attempt}");

    private static string RelativeLogDir(string taskId, int attempt) =>
        Path.Combine("state", "logs", taskId, $"attempt-{attempt}").Replace('\\', '/');

    // --- env + cwd + timeout ---------------------------------------------------------------

    /// <summary>
    /// The §5.1 env-var contract for an ACTION process. <c>GUARDRAILS_FEEDBACK</c> is set
    /// from attempt 2 onward, pointing at the previous attempt's <c>feedback.md</c>.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildEnvironment(
        TaskNode task,
        int attempt,
        string logDir,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GUARDRAILS_PLAN_DIR"] = _plan.PlanDirectory,
            ["GUARDRAILS_TASK_ID"] = task.Id,
            ["GUARDRAILS_TASK_DIR"] = task.Directory,
            ["GUARDRAILS_ATTEMPT"] = attempt.ToString(),
            ["GUARDRAILS_STATE_IN"] = snapshotPath,
            ["GUARDRAILS_STATE_OUT"] = fragmentOutPath,
            ["GUARDRAILS_LOG_DIR"] = logDir
        };

        if (previousFeedbackPath is not null)
        {
            env["GUARDRAILS_FEEDBACK"] = previousFeedbackPath;
        }

        foreach (KeyValuePair<string, string> extra in task.Action.Env)
        {
            env[extra.Key] = extra.Value;
        }

        return env;
    }

    /// <summary>
    /// The §5.1 env-var contract for a GUARDRAIL process: the action env minus
    /// <c>GUARDRAILS_STATE_OUT</c>, plus the action-output pointers.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildGuardrailEnvironment(
        IReadOnlyDictionary<string, string> actionEnv,
        string logDir,
        string fragmentOutPath)
    {
        var env = new Dictionary<string, string>(actionEnv, StringComparer.Ordinal);
        env.Remove("GUARDRAILS_STATE_OUT");

        env["GUARDRAILS_ACTION_STDOUT"] = Path.Combine(logDir, "action-stdout.log");
        env["GUARDRAILS_ACTION_STDERR"] = Path.Combine(logDir, "action-stderr.log");
        env["GUARDRAILS_ACTION_RESULT"] = Path.Combine(logDir, "action-result.json");

        if (File.Exists(fragmentOutPath))
        {
            env["GUARDRAILS_STATE_FRAGMENT"] = fragmentOutPath;
        }

        return env;
    }

    private static string ActionKindLabel(TaskNode task) =>
        task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";

    private string ResolveWorkingDirectory(TaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.Action.WorkingDirectory))
        {
            return _plan.Workspace;
        }

        return Path.GetFullPath(Path.Combine(_plan.PlanDirectory, task.Action.WorkingDirectory));
    }

    private TimeSpan ResolveTimeout(TaskNode task, int? narrowest)
    {
        int seconds = narrowest
            ?? task.TimeoutSeconds
            ?? _plan.Config.DefaultTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
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

    // --- prompt helpers -------------------------------------------------------------------

    private PromptRunnerRegistry RequireRegistry() =>
        _promptRunners ?? throw new InvalidOperationException(
            "This plan has prompt actions/guardrails but no prompt-runner registry was provided to the executor.");

    /// <summary>
    /// Load and parse a <c>*.prompt.md</c> file. Loading-time validation (GR10xx) should have
    /// caught malformed frontmatter, but if parsing fails here we fall back to the raw text as
    /// the body so the run surfaces a real prompt result rather than crashing.
    /// </summary>
    private static PromptFile LoadPromptFile(string path)
    {
        string content = File.ReadAllText(path);
        PromptParseResult parsed = PromptFileParser.Parse(content);
        return parsed.File ?? new PromptFile { Frontmatter = PromptFrontmatter.Empty, Body = content };
    }

    /// <summary>Apply a task/frontmatter <c>maxTurns</c> override over the runner-config settings.</summary>
    private static PromptRunnerSettings ApplyPromptOverrides(PromptRunnerSettings settings, int? maxTurns) =>
        maxTurns is { } turns ? settings with { MaxTurns = turns } : settings;

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

    /// <summary>One attempt's terminal result plus the feedback file it left for the next attempt.</summary>
    private sealed record AttemptResult(TaskResult Result, string? FeedbackPath);

    /// <summary>The outcome of a single attempt's guardrail pass.</summary>
    private sealed record GuardrailRunResult
    {
        public required IReadOnlyList<GuardrailResult> Results { get; init; }
        public required bool AnyFailed { get; init; }
        public required bool TimedOut { get; init; }
    }

    /// <summary>
    /// A normalized view of an action run — script OR prompt — carrying exactly what the
    /// attempt loop needs. Scripts map their exit code and timeout directly; prompts map
    /// <c>Completed &amp;&amp; !is_error</c> to success (SSOT §9), with cost and the needsHuman escape.
    /// </summary>
    private sealed record ActionRun
    {
        public required bool Succeeded { get; init; }
        public required int? ExitCode { get; init; }
        public required bool TimedOut { get; init; }
        public decimal? CostUsd { get; init; }
        public string? NeedsHumanQuestion { get; init; }
        public string? FailureFeedback { get; init; }
        public string FailureSummary { get; init; } = "action failed";

        // For log artifacts (action-result.json) we reuse the ProcessResult shape; prompt
        // actions synthesize one with a 0/1 exit code reflecting success.
        public ProcessResult AsProcessResult() => new()
        {
            ExitCode = ExitCode ?? (Succeeded ? 0 : 1),
            StandardOutput = string.Empty,
            StandardError = string.Empty,
            TimedOut = TimedOut,
            Duration = TimeSpan.Zero
        };

        public static ActionRun FromScript(ProcessResult result, string? needsHuman) => new()
        {
            Succeeded = result.Succeeded,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
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
}
