using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// The serial executor: runs script-only tasks one at a time in folder-name (ordinal)
/// order, honoring <c>dependsOn</c> only insofar as a task whose dependency did not
/// succeed is reported <see cref="TaskOutcome.Blocked"/> and skipped. As of M3 it is
/// resume-aware: it loads/creates the run journal, applies the SSOT §7 resume rules,
/// skips tasks the journal records as <c>succeeded</c>, threads per-attempt state
/// snapshots and the merge of action fragments, and writes the SSOT §8 per-attempt log
/// layout. No retries or parallelism yet — a failed task records ONE attempt and goes to
/// <c>failed</c> (M4 adds retry/needs-human).
///
/// LIMITATION (documented): ordering is a plain ordinal sort of folder names, NOT a
/// topological sort of the DAG. The <c>NN-</c> prefix convention makes these coincide for
/// well-formed plans; real DAG scheduling is M4.
/// </summary>
public sealed class SerialExecutor
{
    private readonly ProcessRunner _processRunner;
    private readonly IExecutableProbe _probe;
    private readonly IRunObserver _observer;

    public SerialExecutor(
        ProcessRunner processRunner,
        IExecutableProbe probe,
        IRunObserver? observer = null)
    {
        _processRunner = processRunner;
        _probe = probe;
        _observer = observer ?? IRunObserver.Null;
    }

    /// <summary>
    /// Run the plan. Throws <see cref="PromptNotSupportedException"/> up front if the plan
    /// contains any prompt action or guardrail (M5 feature).
    /// </summary>
    public async Task<RunReport> RunAsync(PlanDefinition plan, CancellationToken cancellationToken = default)
    {
        EnsureNoPrompts(plan);

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        if (journal.PlanHashMismatch)
        {
            _observer.PlanHashMismatch(journal.PreviousPlanHash ?? "(unknown)");
        }

        var interpreterMap = new InterpreterMap(_probe, plan.Config.Interpreters);
        var results = new List<TaskResult>(plan.Tasks.Count);
        var succeeded = new HashSet<string>(StringComparer.Ordinal);

        // Tasks already terminal-success in the journal count as succeeded for dependents.
        foreach (TaskNode task in plan.Tasks)
        {
            if (journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded)
            {
                succeeded.Add(task.Id);
            }
        }

        foreach (TaskNode task in plan.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskResult result;
            if (journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded)
            {
                // Resume: already done. Skip without re-running the action.
                result = Skipped(task);
            }
            else if (ShouldBlock(task, succeeded, out string blockReason))
            {
                result = Blocked(task, blockReason);
                journal.MarkBlocked(task.Id);
            }
            else
            {
                result = await RunTaskAsync(plan, task, interpreterMap, stateManager, journal, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A task is a satisfied dependency if it succeeded this run OR was skipped
            // because the journal already records it succeeded (resume).
            if (result.IsGreen)
            {
                succeeded.Add(task.Id);
            }

            results.Add(result);
            _observer.TaskFinished(result);
        }

        return new RunReport { Tasks = results };
    }

    private static void EnsureNoPrompts(PlanDefinition plan)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind == ActionKind.Prompt)
            {
                throw new PromptNotSupportedException(
                    $"Task '{task.Id}' has a prompt action; prompt actions are not supported until M5.");
            }

            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                if (guardrail.Kind == ActionKind.Prompt)
                {
                    throw new PromptNotSupportedException(
                        $"Task '{task.Id}' has a prompt guardrail '{guardrail.Name}'; prompt guardrails are not supported until M5.");
                }
            }
        }
    }

    private static bool ShouldBlock(TaskNode task, IReadOnlySet<string> succeeded, out string reason)
    {
        foreach (string dependency in task.DependsOn)
        {
            if (!succeeded.Contains(dependency))
            {
                reason = $"dependency '{dependency}' did not succeed";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private async Task<TaskResult> RunTaskAsync(
        PlanDefinition plan,
        TaskNode task,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        CancellationToken cancellationToken)
    {
        _observer.TaskStarting(task);
        journal.MarkRunning(task.Id);

        int attemptNumber = journal.NextAttemptNumber(task.Id);
        var startedAt = DateTimeOffset.UtcNow;

        string logDir = AttemptLogDir(plan, task.Id, attemptNumber);
        Directory.CreateDirectory(logDir);
        string relativeLogDir = RelativeLogDir(plan, task.Id, attemptNumber);

        // Snapshot the merged state for this attempt (immutable copy → state-in.json).
        string snapshotPath = stateManager.CreateSnapshot(logDir);
        string fragmentOutPath = Path.Combine(logDir, "action-out-fragment.json");

        IReadOnlyDictionary<string, string> env = BuildEnvironment(
            plan, task, attemptNumber, logDir, snapshotPath, fragmentOutPath);
        string workspace = ResolveWorkingDirectory(plan, task);

        // --- action -------------------------------------------------------------------
        ProcessResult actionResult = await RunUnitAsync(
            interpreterMap,
            task.Action.Path,
            task.Action.Args,
            workspace,
            env,
            ResolveTimeout(plan, task, task.Action.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteActionLogs(logDir, actionResult, ActionKindLabel(task));

        if (!actionResult.Succeeded)
        {
            return CompleteActionFailed(journal, task, attemptNumber, startedAt, relativeLogDir, actionResult);
        }

        // --- guardrails ---------------------------------------------------------------
        IReadOnlyDictionary<string, string> guardrailEnv = BuildGuardrailEnvironment(env, logDir, fragmentOutPath);

        GuardrailRunResult guardrails = await RunGuardrailsAsync(
            plan, task, interpreterMap, workspace, guardrailEnv, logDir, cancellationToken).ConfigureAwait(false);

        if (guardrails.AnyFailed)
        {
            return CompleteGuardrailFailed(journal, task, attemptNumber, startedAt, relativeLogDir, actionResult, guardrails);
        }

        // --- merge fragment (only after every guardrail passed) -----------------------
        return CompleteSucceeded(
            stateManager, journal, task, attemptNumber, startedAt, relativeLogDir,
            actionResult, guardrails, fragmentOutPath, logDir);
    }

    private TaskResult CompleteSucceeded(
        StateManager stateManager,
        RunJournal journal,
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult,
        GuardrailRunResult guardrails,
        string fragmentOutPath,
        string logDir)
    {
        long? mergeSequence = null;

        if (File.Exists(fragmentOutPath))
        {
            long reserved = journal.ReserveMergeSequence();
            MergeFragmentResult merge = stateManager.MergeFragment(task.Id, fragmentOutPath, reserved, logDir);

            if (!merge.Merged)
            {
                // Invalid fragment is its own attempt failure (SSOT §6.2). State unchanged.
                var rejected = new AttemptRecord
                {
                    Attempt = attemptNumber,
                    StartedAt = startedAt,
                    EndedAt = DateTimeOffset.UtcNow,
                    ActionExitCode = actionResult.ExitCode,
                    Outcome = AttemptOutcome.InvalidFragment,
                    LogDir = relativeLogDir
                };
                journal.RecordAttempt(task.Id, rejected, JournalTaskStatus.Failed);

                return new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.InvalidFragment,
                    ActionExitCode = actionResult.ExitCode,
                    Guardrails = guardrails.Results,
                    Summary = merge.Reason ?? "invalid state fragment"
                };
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
        journal.RecordAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = actionResult.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed"
                      + (mergeSequence is null ? "" : $"; merged (seq {mergeSequence})")
        };
    }

    private static TaskResult CompleteActionFailed(
        RunJournal journal,
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult)
    {
        string reason = actionResult.TimedOut
            ? "action timed out"
            : $"action exited {actionResult.ExitCode}";

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = actionResult.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.ActionFailed,
            LogDir = relativeLogDir
        };
        journal.RecordAttempt(task.Id, record, JournalTaskStatus.Failed);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.ActionFailed,
            ActionExitCode = actionResult.ExitCode,
            Summary = $"{reason}; guardrails skipped"
        };
    }

    private static TaskResult CompleteGuardrailFailed(
        RunJournal journal,
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult,
        GuardrailRunResult guardrails)
    {
        IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = guardrails.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.GuardrailFailed,
            FailedGuardrails = failed
                .Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "guardrail failed" })
                .ToList(),
            LogDir = relativeLogDir
        };
        journal.RecordAttempt(task.Id, record, JournalTaskStatus.Failed);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.GuardrailFailed,
            ActionExitCode = actionResult.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"guardrail(s) failed: {string.Join(", ", failed.Select(g => g.Name))}"
        };
    }

    private async Task<GuardrailRunResult> RunGuardrailsAsync(
        PlanDefinition plan,
        TaskNode task,
        InterpreterMap interpreterMap,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string logDir,
        CancellationToken cancellationToken)
    {
        var guardrailResults = new List<GuardrailResult>(task.Guardrails.Count);
        bool anyFailed = false;
        bool timedOut = false;

        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            ProcessResult result = await RunUnitAsync(
                interpreterMap,
                guardrail.Path,
                guardrail.Args,
                workspace,
                env,
                ResolveTimeout(plan, task, guardrail.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            AttemptArtifacts.WriteGuardrailLogs(logDir, guardrail.Name, result);

            GuardrailResult guardrailResult = ToGuardrailResult(guardrail, result);
            guardrailResults.Add(guardrailResult);
            _observer.GuardrailFinished(task, guardrailResult);

            if (!guardrailResult.Passed)
            {
                anyFailed = true;
                timedOut |= result.TimedOut;
                if (plan.Config.GuardrailMode == GuardrailMode.FailFast)
                {
                    break;
                }
            }
        }

        return new GuardrailRunResult { Results = guardrailResults, AnyFailed = anyFailed, TimedOut = timedOut };
    }

    private async Task<ProcessResult> RunUnitAsync(
        InterpreterMap interpreterMap,
        string scriptPath,
        IReadOnlyList<string> args,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, args);
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

    private static TaskResult Blocked(TaskNode task, string reason) => new()
    {
        TaskId = task.Id,
        Outcome = TaskOutcome.Blocked,
        Summary = $"blocked: {reason}"
    };

    private static TaskResult Skipped(TaskNode task) => new()
    {
        TaskId = task.Id,
        Outcome = TaskOutcome.Skipped,
        Summary = "already succeeded (resumed) — skipped"
    };

    // --- log paths ---------------------------------------------------------------------

    private static string AttemptLogDir(PlanDefinition plan, string taskId, int attempt) =>
        Path.Combine(plan.PlanDirectory, "state", "logs", taskId, $"attempt-{attempt}");

    private static string RelativeLogDir(PlanDefinition plan, string taskId, int attempt) =>
        Path.Combine("state", "logs", taskId, $"attempt-{attempt}").Replace('\\', '/');

    // --- env + cwd + timeout ----------------------------------------------------------

    /// <summary>
    /// The §5.1 env-var contract for an ACTION process: the four always-on vars plus the
    /// state/log vars an action needs (snapshot in, fragment out, log dir). Per-action
    /// extra env from task.json is overlaid last. <c>GUARDRAILS_FEEDBACK</c> is M4 and is
    /// not set here.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(
        PlanDefinition plan,
        TaskNode task,
        int attempt,
        string logDir,
        string snapshotPath,
        string fragmentOutPath)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GUARDRAILS_PLAN_DIR"] = plan.PlanDirectory,
            ["GUARDRAILS_TASK_ID"] = task.Id,
            ["GUARDRAILS_TASK_DIR"] = task.Directory,
            ["GUARDRAILS_ATTEMPT"] = attempt.ToString(),
            ["GUARDRAILS_STATE_IN"] = snapshotPath,
            ["GUARDRAILS_STATE_OUT"] = fragmentOutPath,
            ["GUARDRAILS_LOG_DIR"] = logDir
        };

        foreach (KeyValuePair<string, string> extra in task.Action.Env)
        {
            env[extra.Key] = extra.Value;
        }

        return env;
    }

    /// <summary>
    /// The §5.1 env-var contract for a GUARDRAIL process: the action env minus
    /// <c>GUARDRAILS_STATE_OUT</c> (guardrails do not publish state), plus the action-output
    /// pointers (<c>GUARDRAILS_STATE_FRAGMENT</c> if a fragment exists, plus the action's
    /// captured stdout/stderr/result files).
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

    private static string ResolveWorkingDirectory(PlanDefinition plan, TaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.Action.WorkingDirectory))
        {
            return plan.Workspace;
        }

        return Path.GetFullPath(Path.Combine(plan.PlanDirectory, task.Action.WorkingDirectory));
    }

    private static TimeSpan ResolveTimeout(PlanDefinition plan, TaskNode task, int? narrowest)
    {
        int seconds = narrowest
            ?? task.TimeoutSeconds
            ?? plan.Config.DefaultTimeoutSeconds;
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

    /// <summary>The action + guardrail outcome of a single attempt's guardrail pass.</summary>
    private sealed record GuardrailRunResult
    {
        public required IReadOnlyList<GuardrailResult> Results { get; init; }
        public required bool AnyFailed { get; init; }
        public required bool TimedOut { get; init; }
    }
}
