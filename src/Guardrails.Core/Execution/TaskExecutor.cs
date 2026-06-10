using Guardrails.Core.Journal;
using Guardrails.Core.Model;
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

    public TaskExecutor(
        PlanDefinition plan,
        ProcessRunner processRunner,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        IRunObserver observer)
    {
        _plan = plan;
        _processRunner = processRunner;
        _interpreterMap = interpreterMap;
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer;
    }

    /// <inheritdoc />
    public async Task<TaskResult> ExecuteAsync(TaskNode task, CancellationToken cancellationToken)
    {
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

            if (attempt.Result.Outcome is TaskOutcome.Succeeded or TaskOutcome.Cancelled)
            {
                return attempt.Result;
            }

            feedbackPath = attempt.FeedbackPath;
        }

        return last with { Summary = $"{last.Summary} — needs human after {budget} attempt(s)" };
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

        // --- action ---------------------------------------------------------------------
        ProcessResult actionResult = await RunUnitAsync(
            task.Action.Path, task.Action.Args, workspace, env,
            ResolveTimeout(task, task.Action.TimeoutSeconds), cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteActionLogs(logDir, actionResult, ActionKindLabel(task));

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(task, attemptNumber, startedAt, relativeLogDir, actionResult);
        }

        if (!actionResult.Succeeded)
        {
            string feedback = RetryPolicy.ForActionFailure(task, attemptNumber, actionResult);
            return FailedAttempt(
                task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                actionResult.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.ActionFailed,
                new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.ActionFailed,
                    ActionExitCode = actionResult.ExitCode,
                    Summary = $"{(actionResult.TimedOut ? "action timed out" : $"action exited {actionResult.ExitCode}")}; guardrails skipped"
                });
        }

        // --- guardrails -----------------------------------------------------------------
        IReadOnlyDictionary<string, string> guardrailEnv = BuildGuardrailEnvironment(env, logDir, fragmentOutPath);
        GuardrailRunResult guardrails = await RunGuardrailsAsync(
            task, workspace, guardrailEnv, logDir, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(task, attemptNumber, startedAt, relativeLogDir, actionResult);
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
                    ActionExitCode = actionResult.ExitCode,
                    Guardrails = guardrails.Results,
                    Summary = $"guardrail(s) failed: {string.Join(", ", failed.Select(g => g.Name))}"
                },
                failed.Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "guardrail failed" }).ToList());
        }

        // --- merge fragment (only after every guardrail passed) --------------------------
        return CompleteSucceededOrInvalidFragment(
            task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, actionResult, guardrails, isFinal);
    }

    private AttemptResult CompleteSucceededOrInvalidFragment(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ProcessResult actionResult,
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
                        ActionExitCode = actionResult.ExitCode,
                        Guardrails = guardrails.Results,
                        Summary = reason
                    });
            }

            mergeSequence = reserved;
        }

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = AttemptOutcome.Succeeded,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = actionResult.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed"
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
        IReadOnlyList<FailedGuardrail>? failedGuardrails = null)
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
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, isFinal ? JournalTaskStatus.NeedsHuman : JournalTaskStatus.Running);

        return new AttemptResult(result, feedbackPath);
    }

    private AttemptResult Cancelled(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult)
    {
        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = AttemptOutcome.Cancelled,
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
        string logDir,
        CancellationToken cancellationToken)
    {
        var results = new List<GuardrailResult>(task.Guardrails.Count);
        bool anyFailed = false;
        bool timedOut = false;

        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            ProcessResult processResult = await RunUnitAsync(
                guardrail.Path, guardrail.Args, workspace, env,
                ResolveTimeout(task, guardrail.TimeoutSeconds), cancellationToken).ConfigureAwait(false);

            AttemptArtifacts.WriteGuardrailLogs(logDir, guardrail.Name, processResult);

            GuardrailResult result = ToGuardrailResult(guardrail, processResult);
            results.Add(result);
            _observer.GuardrailFinished(task, result);

            if (cancellationToken.IsCancellationRequested)
            {
                break; // the caller turns this into a cancelled attempt
            }

            if (!result.Passed)
            {
                anyFailed = true;
                timedOut |= processResult.TimedOut;
                if (_plan.Config.GuardrailMode == GuardrailMode.FailFast)
                {
                    break;
                }
            }
        }

        return new GuardrailRunResult { Results = results, AnyFailed = anyFailed, TimedOut = timedOut };
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

    /// <summary>One attempt's terminal result plus the feedback file it left for the next attempt.</summary>
    private sealed record AttemptResult(TaskResult Result, string? FeedbackPath);

    /// <summary>The outcome of a single attempt's guardrail pass.</summary>
    private sealed record GuardrailRunResult
    {
        public required IReadOnlyList<GuardrailResult> Results { get; init; }
        public required bool AnyFailed { get; init; }
        public required bool TimedOut { get; init; }
    }
}
