using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M2 walking-skeleton executor: runs script-only tasks serially, in folder-name
/// (ordinal) order, honoring <c>dependsOn</c> only insofar as a task whose dependency
/// did not succeed is reported <see cref="TaskOutcome.Blocked"/> and skipped. No retries,
/// no parallelism, no state/journal — those arrive in M3/M4.
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

        var interpreterMap = new InterpreterMap(_probe, plan.Config.Interpreters);
        var results = new List<TaskResult>(plan.Tasks.Count);
        var succeeded = new HashSet<string>(StringComparer.Ordinal);

        foreach (TaskNode task in plan.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskResult result = ShouldBlock(task, succeeded, out string blockReason)
                ? Blocked(task, blockReason)
                : await RunTaskAsync(plan, task, interpreterMap, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
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
        CancellationToken cancellationToken)
    {
        _observer.TaskStarting(task);

        IReadOnlyDictionary<string, string> env = BuildEnvironment(plan, task);
        string workspace = ResolveWorkingDirectory(plan, task);

        ProcessResult actionResult = await RunUnitAsync(
            interpreterMap,
            task.Action.Path,
            task.Action.Args,
            workspace,
            env,
            ResolveTimeout(plan, task, task.Action.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (!actionResult.Succeeded)
        {
            return ActionFailed(task, actionResult);
        }

        return await RunGuardrailsAsync(plan, task, interpreterMap, workspace, env, actionResult, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TaskResult> RunGuardrailsAsync(
        PlanDefinition plan,
        TaskNode task,
        InterpreterMap interpreterMap,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        ProcessResult actionResult,
        CancellationToken cancellationToken)
    {
        var guardrailResults = new List<GuardrailResult>(task.Guardrails.Count);
        bool anyFailed = false;

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

            GuardrailResult guardrailResult = ToGuardrailResult(guardrail, result);
            guardrailResults.Add(guardrailResult);
            _observer.GuardrailFinished(task, guardrailResult);

            if (!guardrailResult.Passed)
            {
                anyFailed = true;
                if (plan.Config.GuardrailMode == GuardrailMode.FailFast)
                {
                    break;
                }
            }
        }

        if (!anyFailed)
        {
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                ActionExitCode = actionResult.ExitCode,
                Guardrails = guardrailResults,
                Summary = $"action ok; {guardrailResults.Count} guardrail(s) passed"
            };
        }

        IEnumerable<string> failedNames = guardrailResults.Where(g => !g.Passed).Select(g => g.Name);
        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.GuardrailFailed,
            ActionExitCode = actionResult.ExitCode,
            Guardrails = guardrailResults,
            Summary = $"guardrail(s) failed: {string.Join(", ", failedNames)}"
        };
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

    private static TaskResult ActionFailed(TaskNode task, ProcessResult actionResult)
    {
        string reason = actionResult.TimedOut
            ? "action timed out"
            : $"action exited {actionResult.ExitCode}";

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.ActionFailed,
            ActionExitCode = actionResult.ExitCode,
            Summary = $"{reason}; guardrails skipped"
        };
    }

    private static TaskResult Blocked(TaskNode task, string reason) => new()
    {
        TaskId = task.Id,
        Outcome = TaskOutcome.Blocked,
        Summary = $"blocked: {reason}"
    };

    // --- env + cwd + timeout ----------------------------------------------------------

    /// <summary>The §5.1 env-var subset that exists in M2. State/log/feedback vars arrive in M3.</summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(PlanDefinition plan, TaskNode task)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GUARDRAILS_PLAN_DIR"] = plan.PlanDirectory,
            ["GUARDRAILS_TASK_ID"] = task.Id,
            ["GUARDRAILS_TASK_DIR"] = task.Directory,
            ["GUARDRAILS_ATTEMPT"] = "1"
        };

        foreach (KeyValuePair<string, string> extra in task.Action.Env)
        {
            env[extra.Key] = extra.Value;
        }

        return env;
    }

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
}
