using System.Text;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

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
/// <remarks>
/// The per-attempt machinery is delegated to focused collaborators in this namespace:
/// <see cref="ActionRunner"/> (action dispatch + prompt action), <see cref="GuardrailRunner"/>
/// (guardrail pass), and <see cref="DependencyContextBuilder"/> (prompt-context provenance).
/// This type owns the attempt loop, journal transitions, and the env/cwd/timeout contract.
/// </remarks>
public sealed class TaskExecutor : ITaskExecutor
{
    private readonly PlanDefinition _plan;
    private readonly StateManager _stateManager;
    private readonly RunJournal _journal;
    private readonly IRunObserver _observer;
    private readonly ActionRunner _actionRunner;
    private readonly GuardrailRunner _guardrailRunner;
    private readonly AttemptJournaler _journaler;
    private readonly DependencyGraph _graph;
    private readonly IReadOnlyDictionary<string, TaskNode> _tasksById;
    private readonly NeedsHumanTriage? _triage;

    public TaskExecutor(
        PlanDefinition plan,
        ProcessRunner processRunner,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        IRunObserver observer,
        PromptRunnerRegistry? promptRunners = null,
        NeedsHumanTriage? triage = null)
    {
        _plan = plan;
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer;
        _triage = triage;

        _graph = new DependencyGraph(plan.Tasks);
        _tasksById = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var scriptRunner = new ScriptUnitRunner(processRunner, interpreterMap);
        var promptSupport = new PromptExecutionSupport(promptRunners);
        var dependencyContext = new DependencyContextBuilder(plan, journal, _graph, _tasksById);

        _actionRunner = new ActionRunner(plan, scriptRunner, promptSupport, dependencyContext, ResolveTimeout);
        _guardrailRunner = new GuardrailRunner(plan, observer, scriptRunner, promptSupport, ResolveTimeout);
        _journaler = new AttemptJournaler(stateManager, journal);
    }

    /// <inheritdoc />
    public async Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
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

            AttemptResult attempt = await RunAttemptAsync(task, worktree, attemptNumber, feedbackPath, isFinal, cancellationToken)
                .ConfigureAwait(false);
            last = attempt.Result;

            // On success, stamp the summary with how long the task took (including any retries)
            // and the wall-clock completion time, so an unattended/overnight run can be reviewed
            // in the morning. Display-only — the journal already records per-attempt start/end.
            if (attempt.Result.Outcome is TaskOutcome.Succeeded)
            {
                return attempt.Result with
                {
                    // taskStartedAt is UTC; the subtraction is drift-free elapsed wall time.
                    // DateTimeOffset.Now (local) is used only for the human-readable HH:mm:ss
                    // stamp — intentional so the display matches the developer's clock.
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

            // F2: in worktree mode, reset the segment to taskBase + clean before the next attempt
            // so attempt N+1 starts on a pristine tree and never inherits attempt N's WIP (the
            // wip.txt-survives defect). Guarded on a real git segment (non-empty path + a real
            // taskBase sha, not the all-zeros placeholder a fake provider supplies).
            if (!isFinal && IsRealGitSegment(worktree))
            {
                GitWorktreeProvider.ResetSegment(worktree.WorktreePath, worktree.TaskBase);
            }
        }

        // Budget exhausted → needs-human via exhaustion. Run advisory triage (plan 08 §9).
        string? triageFeedbackPath = null;
        if (_triage is not null)
        {
            try
            {
                string taskLogDir = TaskLevelLogDir(task.Id);
                triageFeedbackPath = await _triage.RunAsync(
                    task, taskLogDir, _plan.PlanDirectory, _plan.Workspace, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Triage is advisory — exceptions must never abort the run or change the verdict.
            }
        }

        string exhaustedSuffix = $" — needs human after {budget} attempt(s)";
        if (triageFeedbackPath is not null)
            exhaustedSuffix += $"; triage: {triageFeedbackPath}";

        return last with { Summary = $"{last.Summary}{exhaustedSuffix}" };
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
        WorktreeHandle worktree,
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
            task, attemptNumber, logDir, snapshotPath, fragmentOutPath, previousFeedbackPath,
            worktree.WorktreePath);
        string workspace = ResolveWorkingDirectory(task);

        // --- action (script or prompt) --------------------------------------------------
        ActionRun action = await _actionRunner.RunAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, cancellationToken).ConfigureAwait(false);

        AttemptArtifacts.WriteActionLogs(logDir, action.AsProcessResult(), ActionKindLabel(task));

        if (cancellationToken.IsCancellationRequested)
        {
            return _journaler.Cancelled(task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(), action.CostUsd);
        }

        // --- needsHuman short-circuit (SSOT §9): record + escalate IMMEDIATELY -----------
        if (action.NeedsHumanQuestion is { } question)
        {
            return _journaler.NeedsHuman(task, attemptNumber, startedAt, relativeLogDir, logDir, action, question);
        }

        if (!action.Succeeded)
        {
            string feedback = action.FailureFeedback
                ?? RetryPolicy.ForActionFailure(task, attemptNumber, action.AsProcessResult());
            return _journaler.FailedAttempt(
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

        // --- write-scope check (plan 08 §2/§3.4): after action, before guardrails -------
        // Only runs when the task declares a writeScope AND the worktree carries a real
        // git repo path (non-empty TaskBase). Skipped for FakeWorktreeProvider segments.
        if (task.WriteScope is { } scopeGlobs && IsRealGitSegment(worktree))
        {
            WriteScopeCheckResult scopeCheck = WriteScopeCheck.Check(
                worktree.WorktreePath, worktree.TaskBase, scopeGlobs);

            if (!scopeCheck.Passed)
            {
                // Scoped revert: restore only the out-of-scope paths to taskBase state.
                WriteScopeCheck.ScopedRevert(worktree.WorktreePath, worktree.TaskBase, scopeCheck.OffendingPaths);

                string offendingList = string.Join(", ", scopeCheck.OffendingPaths);
                string feedback = RetryPolicy.ForWriteScopeViolation(task, attemptNumber, scopeCheck.OffendingPaths);
                return _journaler.FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.GuardrailFailed,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.GuardrailFailed,
                        ActionExitCode = action.ExitCode,
                        Summary = $"write-scope violation: {offendingList}"
                    },
                    costUsd: action.CostUsd);
            }
        }

        // --- guardrails -----------------------------------------------------------------
        IReadOnlyDictionary<string, string> guardrailEnv = BuildGuardrailEnvironment(env, logDir, fragmentOutPath);
        GuardrailRunResult guardrails = await _guardrailRunner.RunAsync(
            task, workspace, guardrailEnv, snapshotPath, logDir, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return _journaler.Cancelled(task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(), action.CostUsd);
        }

        if (guardrails.AnyFailed)
        {
            IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();
            string feedback = RetryPolicy.ForGuardrailFailures(task, attemptNumber, guardrails.Results);
            return _journaler.FailedAttempt(
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

        // --- merge fragment or defer to Scheduler (worktree mode) -----------------------
        // Worktree mode: the segment is a real directory. Validate the fragment but defer the
        // actual merge + git commit to the Scheduler's B1 settle under the integration lock.
        // Serial mode (empty or non-existent WorktreePath): merge immediately as before.
        if (!string.IsNullOrEmpty(worktree.WorktreePath) && Directory.Exists(worktree.WorktreePath))
        {
            return _journaler.ValidateFragmentForSettle(
                task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal);
        }

        return _journaler.CompleteSucceededOrInvalidFragment(
            task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal);
    }

    /// <summary>
    /// True when <paramref name="worktree"/> is a real git segment (worktree mode) rather than a
    /// serial-mode or fake-provider placeholder: a non-empty path that exists on disk plus a real
    /// <c>TaskBase</c> sha (not the all-zeros placeholder a <see cref="FakeWorktreeProvider"/>
    /// supplies). Gates both the write-scope check and the F2 retry reset on a usable git tree.
    /// </summary>
    private static bool IsRealGitSegment(WorktreeHandle worktree) =>
        !string.IsNullOrEmpty(worktree.WorktreePath)
        && Directory.Exists(worktree.WorktreePath)
        && !string.IsNullOrEmpty(worktree.TaskBase)
        && !worktree.TaskBase.All(c => c == '0');

    // --- log paths -----------------------------------------------------------------------

    private string TaskLevelLogDir(string taskId) =>
        Path.Combine(_plan.PlanDirectory, "logs", _journal.Document.RunId, taskId);

    private string AttemptLogDir(string taskId, int attempt) =>
        Path.Combine(_plan.PlanDirectory, "logs", _journal.Document.RunId, taskId, $"attempt-{attempt}");

    private string RelativeLogDir(string taskId, int attempt) =>
        $"logs/{_journal.Document.RunId}/{taskId}/attempt-{attempt}";

    // --- env + cwd + timeout ---------------------------------------------------------------

    /// <summary>
    /// The §5.1 env-var contract for an ACTION process. <c>GUARDRAILS_FEEDBACK</c> is set
    /// from attempt 2 onward. <c>GUARDRAILS_WORKSPACE</c> is set in worktree mode (when
    /// <paramref name="worktreePath"/> is a real directory) so actions can write files into the
    /// isolated segment that <see cref="GitWorktreeProvider.Integrate"/> will commit.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildEnvironment(
        TaskNode task,
        int attempt,
        string logDir,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string worktreePath = "")
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

        if (!string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath))
        {
            env["GUARDRAILS_WORKSPACE"] = worktreePath;
        }

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
}
