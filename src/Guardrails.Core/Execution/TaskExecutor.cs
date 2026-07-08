using System.Text;
using Guardrails.Core.Graph;
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
    private readonly IReVerifier _reVerifier;
    private readonly AttemptJournaler _journaler;
    private readonly DependencyGraph _graph;
    private readonly IReadOnlyDictionary<string, TaskNode> _tasksById;
    private readonly NeedsHumanTriage? _triage;
    private readonly Func<TimeSpan, CancellationToken, Task> _transientDelay;

    public TaskExecutor(
        PlanDefinition plan,
        ProcessRunner processRunner,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        IRunObserver observer,
        PromptRunnerRegistry? promptRunners = null,
        NeedsHumanTriage? triage = null,
        Func<TimeSpan, CancellationToken, Task>? transientDelay = null)
    {
        _plan = plan;
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer;
        _triage = triage;
        // Injected so concurrency tests gate the transient backoff deterministically (no real sleeps);
        // production waits with Task.Delay (issue #115).
        _transientDelay = transientDelay ?? Task.Delay;

        _graph = new DependencyGraph(plan.Tasks);
        _tasksById = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var scriptRunner = new ScriptUnitRunner(processRunner, interpreterMap);
        var promptSupport = new PromptExecutionSupport(promptRunners);
        var dependencyContext = new DependencyContextBuilder(plan, journal, _graph, _tasksById);

        _actionRunner = new ActionRunner(plan, scriptRunner, promptSupport, dependencyContext, ResolveTimeout);
        _guardrailRunner = new GuardrailRunner(plan, observer, scriptRunner, promptSupport, ResolveTimeout);
        // The task-preflight slot (design-of-record 09-preflight-first-class, deliverable 5) reuses the
        // same attempt-decoupled re-verify seam SchedulerFactory wires into the Scheduler for the
        // per-union re-verify — built here from the same processRunner/interpreterMap so it is wired
        // unconditionally in BOTH serial and worktree mode (TaskExecutor is constructed once per run).
        _reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);
        _journaler = new AttemptJournaler(stateManager, journal);
    }

    /// <inheritdoc />
    public async Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
    {
        var taskStartedAt = DateTimeOffset.UtcNow;
        _observer.TaskStarting(task);

        // Task-level preflight slot (design-of-record 09-preflight-first-class, deliverable 5): a JIT
        // dependency-delivery gate — tasks/<id>/preflights/, when present, is evaluated in the
        // CONSUMER's own effective workspace (the segment worktree at taskBase in worktree mode, the
        // plan workspace in serial mode) BEFORE the attempt loop AND before MarkRunning, so a RED
        // preflight settles straight from `pending` to `needs-human` without ever recording a
        // transient `running` status or burning a retry attempt (the no-burn property, both modes).
        // A GREEN preflight (or no preflights/ folder at all) falls through to the unchanged attempt
        // loop below.
        if (task.Preflights.Count > 0)
        {
            ReVerifyResult preflightResult = await _reVerifier
                .ReVerifyAsync(EffectiveWorkspace(worktree), task.Preflights, cancellationToken)
                .ConfigureAwait(false);

            if (!preflightResult.Passed)
            {
                // D6: journal a real AttemptRecord carrying Outcome = TaskPreflightFailed and the failed
                // preflight check names + reasons, so run.json shows WHAT gate failed and WHY (SSOT §7 —
                // "a per-attempt outcome inside tasks{}"). This does NOT burn a retry: the action never
                // runs and the retry budget is never consulted (we return BEFORE the attempt loop AND
                // before MarkRunning), so the no-burn property is preserved STRUCTURALLY — the recorded
                // attempt simply is not counted against a budget nothing reads here.
                int preflightAttempt = _journal.NextAttemptNumber(task.Id);
                IReadOnlyList<FailedGuardrail> failedChecks = preflightResult.FailedGuardrails
                    .Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "preflight check failed" })
                    .ToList();

                AttemptResult preflightSettle = _journaler.TaskPreflightFailed(
                    task,
                    preflightAttempt,
                    taskStartedAt,
                    RelativeLogDir(task.Id, preflightAttempt),
                    AttemptLogDir(task.Id, preflightAttempt),
                    failedChecks);

                // TaskFinished is fired by the Scheduler's OnSettledAsync for every settled result (as it
                // is for the other ExecuteAsync early-returns — needs-human / permission-wall), so it is
                // deliberately NOT called here to avoid a duplicate observer notification.
                return preflightSettle.Result;
            }
        }

        _journal.MarkRunning(task.Id);

        int budget = 1 + (task.Retries ?? _plan.Config.DefaultRetries);
        string? feedbackPath = null;
        TaskResult last = null!;

        // Tracks permission walls across attempts (issues #86 / #104): a write the runtime refuses
        // because the path is not granted. A .claude/ wall is structural (halts on the FIRST hit); any
        // other path refused across repeated attempts halts on the repeat — both settle needs-human
        // EARLY rather than burning the rest of the budget on the identical, un-retryable wall.
        var permissionWalls = new PermissionWallTracker();

        // One transient-pause budget per task (issue #115): a rate limit pauses+re-runs WITHOUT
        // consuming the retry budget, bounded by the cumulative wall-clock pause budget.
        var backoff = new TransientBackoff(
            TimeSpan.FromSeconds(_plan.Config.TransientPauseBudgetSeconds), _transientDelay);
        int timeoutRetries = 0;
        // One auto-escalation counter for turn-budget exhaustion (issue #129 / #94), mirroring the
        // timeout clock: after a max-turns termination the NEXT attempt's turn budget is raised so the
        // retry does not hit the identical wall. A same-budget retry just re-exhausts at the same cap.
        int maxTurnsRetries = 0;

        // #174 / #182 no-op-deadlock short-circuit: the previous guardrail-failed attempt's no-op flag,
        // failure fingerprint, and (serial mode) action-output fingerprint. When the CURRENT attempt is
        // ALSO a no-op with the IDENTICAL guardrail fingerprint — plus, in serial mode, an identical
        // action-output fingerprint — a further attempt provably cannot differ, so escalate to
        // needs-human immediately instead of exhausting the budget. Null until the first guardrail
        // failure. Whether the SERIAL gate applies is fixed for the whole task by the worktree handle:
        // a real git segment uses the worktree gate (taskBase file diff), else the serial gate.
        bool isRealGitSegment = IsRealGitSegment(worktree);
        bool previousAttemptWasNoOp = false;
        string? previousFailureFingerprint = null;
        string? previousActionOutputFingerprint = null;

        for (int attemptIndex = 1; attemptIndex <= budget; attemptIndex++)
        {
            bool isFinal = attemptIndex == budget;
            _observer.AttemptStarting(task, attemptIndex, budget);

            // Inner pause loop: re-run the SAME attempt across transient pauses without consuming the
            // retry budget. attemptNumber is re-read each time (NextAttemptNumber is pure until an
            // attempt is actually recorded), so a paused retry reuses the same attempt-N log dir.
            AttemptResult attempt;
            while (true)
            {
                int attemptNumber = _journal.NextAttemptNumber(task.Id);
                attempt = await RunAttemptAsync(
                    task, worktree, attemptNumber, feedbackPath, isFinal, timeoutRetries, maxTurnsRetries,
                    permissionWalls, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Result.Outcome != TaskOutcome.TransientPause)
                {
                    break;
                }

                // Cancellation during a pause: journal back to pending (resume re-runs), like any
                // mid-attempt cancellation — NOT a rate-limit halt.
                if (cancellationToken.IsCancellationRequested)
                {
                    int n = _journal.NextAttemptNumber(task.Id);
                    AttemptResult cancelled = _journaler.Cancelled(
                        task, n, DateTimeOffset.UtcNow, RelativeLogDir(task.Id, n),
                        new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "", TimedOut = false, Duration = TimeSpan.Zero },
                        costUsd: null);
                    return cancelled.Result;
                }

                // Transient: pause (bounded backoff) and re-run, unless the whole-task pause budget is
                // spent — then settle needs-human with a DISTINCT rate-limit reason ("re-run later"),
                // NOT a generic failure. This is the named bound on "a rate limit never needs-human".
                if (!backoff.CanPauseAgain())
                {
                    int n = _journal.NextAttemptNumber(task.Id);
                    AttemptResult exhausted = _journaler.RateLimitExhausted(
                        task, n, DateTimeOffset.UtcNow,
                        RelativeLogDir(task.Id, n), AttemptLogDir(task.Id, n),
                        attempt.TransientReason ?? "transient infrastructure error",
                        backoff.Elapsed);
                    return exhausted.Result;
                }

                string reason = attempt.TransientReason ?? "transient infrastructure error";
                TimeSpan delay = backoff.NextDelay();
                _observer.PromptPaused(task, reason, delay, backoff.PauseCount + 1);
                await backoff.PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            last = attempt.Result;

            // A timeout outcome means the task needed more clock; count it so the NEXT attempt's
            // timeout is extended (issue #119) — a same-clock retry just re-times-out.
            if (attempt.Outcome is AttemptOutcome.Timeout)
            {
                timeoutRetries++;
            }

            // A max-turns outcome means the task needed more TURNS; count it so the NEXT attempt's
            // turn budget is raised (issue #129 / #94) — a same-budget retry just re-exhausts.
            if (attempt.Outcome is AttemptOutcome.MaxTurns)
            {
                maxTurnsRetries++;
            }

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

            // Other terminal outcomes do not retry: cancellation, plus the needs-human escalations that
            // skip the remaining budget — the prompt-action needsHuman short-circuit (SSOT §9) and the
            // permission-wall early halt (issues #86 / #104), both of which surface as TaskOutcome.NeedsHuman.
            if (attempt.Result.Outcome is TaskOutcome.Cancelled or TaskOutcome.NeedsHuman)
            {
                return attempt.Result;
            }

            feedbackPath = attempt.FeedbackPath;

            // Two sibling "a further attempt provably cannot converge" short-circuits, settling
            // needs-human NOW (on the 2nd guardrail-failed attempt) instead of reproducing the identical
            // failure through the rest of the budget. Both REQUIRE a byte-identical guardrail failure
            // across the two attempts — the load-bearing "nothing converged" evidence — and differ only
            // in the SECOND piece of evidence that the retry is pointless:
            //
            //   * #174 / #182 (no-op deadlock): the action made NO observable change (a no-op cannot fix
            //     a guardrail failure it did not cause — e.g. a terminal integrationGate no-op against an
            //     AI-merge artifact). "No observable change" differs by mode:
            //       - WORKTREE (#174): exit 0, no fragment, no file diff vs taskBase — ActionWasNoOp
            //         already encodes all three, so the worktree gate needs nothing more.
            //       - SERIAL (#182): no taskBase to diff, so ActionWasNoOp encodes only exit 0 + no
            //         fragment; the serial gate ADDS a byte-identical action-output requirement (the
            //         proxy for "the action behaved identically").
            //   * #264 (deterministic-script reproduction): the action is a `script` whose recorded
            //     output reproduced BYTE-IDENTICALLY across the two attempts — positive evidence the
            //     script is DETERMINISTIC, so re-running the unchanged script is provably pointless (no
            //     agent self-corrects between attempts). A script that WROTE FILES is not a no-op (its
            //     segment diff is non-empty), so #174 never fires for it in worktree mode; #264 is its
            //     sibling for exactly that gap. Scoped to worktree mode — a serial deterministic script
            //     is already a no-op under #182's serial model — and the byte-identical action-output
            //     requirement IS the flaky/nondeterministic-script escape hatch (a script whose output
            //     differs across attempts keeps its full budget, because a retry genuinely might pass).
            //
            // RecordSettle flips the task to needs-human without a synthetic attempt — the same shape the
            // budget-exhaustion path settles to. The tracking below is carried only across guardrail
            // failures, so a non-guardrail failure (action failure / timeout / invalid fragment) never
            // participates.
            bool actionOutputReproduced =
                previousActionOutputFingerprint is not null
                && string.Equals(attempt.ActionOutputFingerprint, previousActionOutputFingerprint, StringComparison.Ordinal);

            bool guardrailFailureReproduced =
                attempt.GuardrailFailureFingerprint is { Length: > 0 }
                && string.Equals(attempt.GuardrailFailureFingerprint, previousFailureFingerprint, StringComparison.Ordinal);

            // #174 / #182: the worktree gate proves "no change" via the taskBase file diff (isRealGitSegment);
            // serial requires byte-identical action output too.
            bool noOpDeadlock =
                attempt.ActionWasNoOp
                && previousAttemptWasNoOp
                && (isRealGitSegment || actionOutputReproduced);

            // #264: a deterministic script (worktree mode) whose action output reproduced byte-identically.
            bool deterministicScriptReproduced =
                task.Action.Kind == ActionKind.Script
                && isRealGitSegment
                && actionOutputReproduced;

            if (!isFinal && guardrailFailureReproduced && (noOpDeadlock || deterministicScriptReproduced))
            {
                _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);

                // A no-op deadlock keeps its established wording (it did LITERALLY nothing); a script
                // that DID work but reproduced identically (#264) gets the deterministic-script wording.
                // When both hold (a no-op script), the more specific no-op message wins.
                string why = noOpDeadlock
                    ? "action is a no-op and the guardrail failure is unchanged; retrying will not " +
                      "help, escalating to needs-human"
                    : "the script action reproduced byte-identical output and the guardrail failure is " +
                      "unchanged; retrying will not help, escalating to needs-human";

                return last with
                {
                    Outcome = TaskOutcome.NeedsHuman,
                    Summary = $"{last.Summary} — {why} (after {attemptIndex} identical attempt(s))"
                };
            }

            // Carry this attempt's no-op + fingerprint signals forward for the next iteration's
            // comparison. A non-guardrail failure (null fingerprint) clears the tracking so a later
            // guardrail failure is only matched against another guardrail failure. The action-output
            // fingerprint feeds the serial gate; it is irrelevant to (and ignored by) the worktree gate.
            previousAttemptWasNoOp = attempt.ActionWasNoOp && attempt.GuardrailFailureFingerprint is not null;
            previousFailureFingerprint = attempt.GuardrailFailureFingerprint;
            previousActionOutputFingerprint = attempt.GuardrailFailureFingerprint is not null
                ? attempt.ActionOutputFingerprint
                : null;

            // §3.5: clear the per-task staging tree before the next attempt so a failed action
            // (whose move never ran) cannot leak attempt N's staged scaffolding into attempt N+1.
            // The StagingMover already deletes staging after a successful move; this covers the
            // action-failed path and does NOT depend on staging being git-untracked. Runs in BOTH
            // modes (serial has no F2 reset). Re-created empty at the top of the next attempt.
            if (!isFinal && task.StagingOutputs is { Count: > 0 })
            {
                ClearStagingTree(EffectiveWorkspace(worktree), task.Id);
            }

            // F2: in worktree mode, reset the segment to taskBase + clean before the next attempt
            // so attempt N+1 starts on a pristine tree and never inherits attempt N's WIP (the
            // wip.txt-survives defect). Failure-kind-agnostic: EVERY non-final worktree attempt resets,
            // which is exactly why the timeout / max-turns feedback above discloses the rollback via the
            // SAME WorktreeWillReset predicate — the claim and the reset are guaranteed to agree (#167).
            if (WorktreeWillReset(worktree, isFinal))
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
                    task, taskLogDir, _plan.PlanDirectory, _plan.Workspace, cancellationToken,
                    autoFile: _plan.Config.TriageAutoFile)
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
    /// Re-validate-only (issue #102): run JUST this task's guardrails against the CURRENT workspace
    /// state, spawning NO action/agent attempt. The intended caller is a human who hand-fixed a
    /// <c>needs-human</c> task's artifact and wants to confirm the gate now passes WITHOUT burning an
    /// agent attempt that might redo expensive work or overwrite the fix.
    /// <list type="bullet">
    ///   <item>Guardrails run with cwd = the plan <see cref="PlanDefinition.Workspace"/> (the user's
    ///     own checkout where the fix lives) — this path is serial/shared-workspace only (the CLI
    ///     refuses worktree mode, where a fresh segment would not contain the in-place fix).</item>
    ///   <item>The <c>GUARDRAILS_ACTION_*</c> pointers are deliberately ABSENT: no action ran, so a
    ///     verify-don't-replay guardrail (#62) that requires recorded action output fails honestly
    ///     rather than passing vacuously. <c>GUARDRAILS_STATE_IN</c> is a fresh snapshot of the
    ///     current <c>state.json</c>; no fragment is produced or merged (the human's artifact is the
    ///     deliverable, not new state).</item>
    ///   <item>All pass ⇒ a synthetic <see cref="AttemptOutcome.Succeeded"/> attempt is journaled and
    ///     the task settles <see cref="TaskOutcome.Succeeded"/> (state.json unchanged). Any fail ⇒
    ///     a <c>feedback.md</c> is written and the task settles <see cref="TaskOutcome.GuardrailFailed"/>;
    ///     the journal status stays non-green so the next normal <c>run</c> still re-attempts it.</item>
    /// </list>
    /// Prompt guardrails are fully supported (same <see cref="GuardrailRunner"/> as a normal attempt);
    /// they are NEVER silently skipped.
    /// </summary>
    public async Task<TaskResult> RevalidateAsync(TaskNode task, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _observer.TaskStarting(task);

        int attemptNumber = _journal.NextAttemptNumber(task.Id);
        string logDir = AttemptLogDir(task.Id, attemptNumber);
        Directory.CreateDirectory(logDir);
        string relativeLogDir = RelativeLogDir(task.Id, attemptNumber);

        string snapshotPath = _stateManager.CreateSnapshot(logDir);
        // Revalidate is serial-only (the CLI refuses worktree mode here), so cwd = the plan workspace
        // where the human's in-place fix lives — never a segment worktree (issue #134 / #102).
        string workspace = ResolveRevalidateWorkingDirectory(task);

        // The guardrail env WITHOUT GUARDRAILS_STATE_OUT (no action) and WITHOUT the
        // GUARDRAILS_ACTION_* pointers: there is no recorded action output to verify against, so a
        // guardrail that reads them sees them absent (a verify-don't-replay guardrail then fails
        // honestly — never a vacuous pass). GUARDRAILS_STATE_IN is the fresh snapshot.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GUARDRAILS_PLAN_DIR"] = _plan.PlanDirectory,
            ["GUARDRAILS_TASK_ID"] = task.Id,
            ["GUARDRAILS_TASK_DIR"] = task.Directory,
            ["GUARDRAILS_ATTEMPT"] = attemptNumber.ToString(),
            ["GUARDRAILS_STATE_IN"] = snapshotPath,
            ["GUARDRAILS_LOG_DIR"] = logDir
        };
        foreach (KeyValuePair<string, string> extra in task.Action.Env)
        {
            env[extra.Key] = extra.Value;
        }

        GuardrailRunResult guardrails = await _guardrailRunner.RunAsync(
            task, workspace, env, snapshotPath, logDir, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Cancelled,
                Guardrails = guardrails.Results,
                Summary = "revalidate cancelled"
            };
        }

        if (guardrails.AnyFailed)
        {
            IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();
            string feedback = RetryPolicy.ForGuardrailFailures(task, attemptNumber, guardrails.Results);
            AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

            var failedRecord = new AttemptRecord
            {
                Attempt = attemptNumber,
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.UtcNow,
                ActionExitCode = null,
                Outcome = AttemptOutcome.GuardrailFailed,
                FailedGuardrails = failed
                    .Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "guardrail failed" })
                    .ToList(),
                LogDir = relativeLogDir
            };
            // NeedsHuman, not pending: the gate still does not pass, so the task stays a non-green
            // halt the human must keep working on — exactly as a normal failed attempt would leave it.
            _journal.RecordAttempt(task.Id, failedRecord, JournalTaskStatus.NeedsHuman);

            var result = new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.GuardrailFailed,
                Guardrails = guardrails.Results,
                Summary = $"revalidate: guardrail(s) still failing: {string.Join(", ", failed.Select(g => g.Name))}"
            };
            _observer.TaskFinished(result);
            return result;
        }

        // All guardrails pass against the current workspace. Journal a synthetic succeeded attempt —
        // no fragment merge (state.json is untouched: the artifact is the deliverable, and any state
        // earlier attempts contributed is already merged).
        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = null,
            Outcome = AttemptOutcome.Succeeded,
            LogDir = relativeLogDir
        };
        // §7.2 (#274 Part A): a revalidate that flips the task to succeeded also stamps its definition
        // hash, so a subsequent resume detects a later definition edit rather than skipping stale.
        _journal.RecordAttempt(
            task.Id, record, JournalTaskStatus.Succeeded, definitionHash: TaskDefinitionHash.Compute(task));

        var ok = new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            Guardrails = guardrails.Results,
            Summary = $"revalidate ok: {guardrails.Results.Count} guardrail(s) passed against current workspace (no agent attempt)"
        };
        _observer.TaskFinished(ok);
        return ok;
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
        int timeoutRetries,
        int maxTurnsRetries,
        PermissionWallTracker permissionWalls,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        string logDir = AttemptLogDir(task.Id, attemptNumber);
        Directory.CreateDirectory(logDir);
        string relativeLogDir = RelativeLogDir(task.Id, attemptNumber);

        // #198: the provenance the harness knows BEFORE the attempt runs — model + segment worktree +
        // base commit. Written to the attempt log dir as a machine-readable header artifact regardless
        // of outcome, and carried onto the journal AttemptRecord on the success paths below.
        Journal.AttemptProvenance? provenance = BuildProvenance(task, worktree);
        AttemptArtifacts.WriteProvenance(logDir, provenance);

        string snapshotPath = _stateManager.CreateSnapshot(logDir);
        string fragmentOutPath = Path.Combine(logDir, "action-out-fragment.json");

        // Staging (SSOT §3.5, issue #130): when the task declares stagingOutputs, the action writes
        // its .claude/-destined deliverable to a pre-created staging dir under the EFFECTIVE
        // workspace (the segment worktree in worktree mode, the plan workspace in serial mode); the
        // harness moves it into the real .claude/ path after the action succeeds and before the
        // write-scope check and guardrails. Null when the task declares no staging.
        string effectiveWorkspace = EffectiveWorkspace(worktree);
        string? stagingDir = task.StagingOutputs is { Count: > 0 }
            ? StagingDirFor(effectiveWorkspace, task.Id)
            : null;
        if (stagingDir is not null)
        {
            // Pre-created (unlike STATE_OUT) so the action can Write into it without first creating
            // the tree, and a pre-created empty dir is the "stage here" signal.
            Directory.CreateDirectory(stagingDir);
        }

        IReadOnlyDictionary<string, string> env = BuildEnvironment(
            task, attemptNumber, logDir, snapshotPath, fragmentOutPath, previousFeedbackPath,
            worktree.WorktreePath, stagingDir);
        // cwd = the EFFECTIVE workspace (issue #134): the segment worktree in worktree mode, the plan
        // workspace in serial mode — matching GUARDRAILS_WORKSPACE and EffectiveWorkspace exactly, so
        // a write relative to cwd lands in the segment that Integrate commits (not the user's checkout).
        string workspace = ResolveWorkingDirectory(task, worktree);

        // --- action (script or prompt) --------------------------------------------------
        // Timeout extension (issue #119): after a timeout, each retry gets a longer clock — a
        // same-clock retry just re-times-out. The factor grows 1× → 1.5× → 2.25× …, capped, so a
        // genuinely heavy task is given the wall-clock it demonstrably needs without unbounded growth.
        double timeoutMultiplier = TimeoutMultiplierFor(timeoutRetries);
        // Turn-budget extension (issue #129 / #94): after a max-turns termination, each retry gets a
        // larger turn budget — a same-budget retry just re-exhausts at the same cap. Same growth shape
        // and cap as the timeout clock; applied only to prompt actions (scripts have no turn budget).
        double maxTurnsMultiplier = MaxTurnsMultiplierFor(maxTurnsRetries);
        // Worktree containment hook (issue #199/#192): non-null ONLY for a real segment worktree — a
        // prompt action/guardrail then gets a generated PreToolUse hook hard-enforcing the OUTER
        // containment boundary (WorktreeContainmentHook), on top of the write-scope CHECK's post-hoc
        // diff (the INNER boundary, unaffected). Null in serial mode: no isolated tree to contain to.
        string? worktreeRootForHook = IsRealGitSegment(worktree) ? worktree.WorktreePath : null;
        ActionRun action = await _actionRunner.RunAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, timeoutMultiplier, stagingDir, maxTurnsMultiplier, cancellationToken, worktreeRootForHook).ConfigureAwait(false);

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

        // --- permission wall early halt (issues #86 / #104) ------------------------------
        // Feed this attempt's refused write paths to the cross-attempt tracker, then check whether a
        // wall warrants settling needs-human NOW instead of burning the rest of the budget. A .claude/
        // path is structural (halts on the FIRST hit — the runtime blocks .claude/ writes even under
        // acceptEdits, so no retry clears it, #104); any other path refused across repeated attempts
        // halts on the repeat (#86). Checked here, before success/failure routing, because even a
        // prompt that "completed" while silently unable to write its .claude/ deliverable must escalate.
        permissionWalls.Observe(action.BlockedWritePaths);
        PermissionWallDecision wall = permissionWalls.ShouldHalt();
        if (wall.Halt)
        {
            return _journaler.PermissionWall(task, attemptNumber, startedAt, relativeLogDir, logDir, action, wall);
        }

        // --- transient pause (issue #115): a retryable infra condition (429/503/529, overloaded,
        // rate/session/usage limit). Do NOT journal a failed attempt and do NOT consume the retry
        // budget — return the in-memory TransientPause signal so the loop backs off and re-runs the
        // SAME attempt. A human cannot fix a rate limit, so this never marks needs-human (until the
        // whole-task pause budget is exhausted, which the loop handles).
        if (!action.Succeeded && action.FailureKind == PromptFailureKind.Transient)
        {
            string reason = action.ResetHint is { Length: > 0 } hint
                ? $"{action.FailureSummary} (resets {hint})"
                : action.FailureSummary;
            return new AttemptResult(
                new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.TransientPause,
                    ActionExitCode = action.ExitCode,
                    Summary = $"paused (transient): {reason}"
                },
                FeedbackPath: null,
                TransientReason: reason);
        }

        if (!action.Succeeded)
        {
            // Compose signal-specific feedback so a retry CHANGES BEHAVIOR rather than re-hitting the
            // same wall: output-cap (#114) → "write incrementally / split"; timeout (#119) / max-turns
            // (#129) → "go straight at the deliverable, don't re-explore". A genuine error keeps the
            // prompt/script failure feedback. The journal outcome distinguishes timeout / output-cap /
            // action-failed so a human (and §9 triage) sees a budget/tool issue, not a generic failure.
            //
            // #167: in worktree mode a non-final FAILED attempt has its segment reset to taskBase +
            // cleaned before the next attempt (the F2 reset below — failure-kind-agnostic), so the
            // attempt's FILE writes are reverted. The timeout / max-turns feedback must then NOT claim
            // the partial work is "preserved on disk"; it discloses the reset and instructs re-authoring.
            // Same signal #162 uses, computed here because this feedback is composed in BOTH modes
            // (unlike the state-rejection path, which only runs in worktree mode).
            bool fileWritesRolledBack = WorktreeWillReset(worktree, isFinal);

            // #195 retry salvage: for the two NON-LOGIC budget-exhaustion outcomes — max-turns and
            // output-cap — preserve this about-to-be-rolled-back attempt's full working-tree state to an
            // inspectable git ref BEFORE the F2 reset discards it, then tell the NEXT attempt's feedback
            // where to find it. Deliberately excludes timeout (a generic budget signal, not scoped by the
            // issue) and NEVER runs on the separate guardrail-failure branch below (its own feedback is
            // ForGuardrailFailures, which this method never calls) — the scope guard is structural: a
            // guardrail-class failure simply never reaches this call site. No-op (returns null) unless
            // ALL of: worktree mode, config opt-in, non-final, and a real rollback is about to happen.
            SalvageRef? salvageRef = fileWritesRolledBack
                ? TryPreserveForSalvage(task, worktree, attemptNumber, action.FailureKind)
                : null;

            string feedback = action.FailureKind switch
            {
                PromptFailureKind.OutputCap => RetryPolicy.ForOutputCapExceeded(task, attemptNumber, salvageRef),
                PromptFailureKind.MaxTurns => RetryPolicy.ForMaxTurnsExceeded(task, attemptNumber, fileWritesRolledBack, salvageRef),
                PromptFailureKind.Timeout => RetryPolicy.ForTimeout(task, attemptNumber, fileWritesRolledBack),
                _ => action.FailureFeedback ?? RetryPolicy.ForActionFailure(task, attemptNumber, action.AsProcessResult())
            };

            AttemptOutcome attemptOutcome = action.FailureKind switch
            {
                PromptFailureKind.Timeout => AttemptOutcome.Timeout,
                PromptFailureKind.OutputCap => AttemptOutcome.OutputCap,
                PromptFailureKind.MaxTurns => AttemptOutcome.MaxTurns,
                _ => action.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.ActionFailed
            };

            string summary = action.FailureKind switch
            {
                PromptFailureKind.OutputCap => "response truncated at the output-token cap — reduce/split the task; guardrails skipped",
                PromptFailureKind.MaxTurns => $"{action.FailureSummary} — ran out of turns mid-progress; turn budget auto-raised for retry; guardrails skipped",
                PromptFailureKind.Timeout => $"{action.FailureSummary} — likely under-sized/under-budgeted; guardrails skipped",
                _ => $"{action.FailureSummary}; guardrails skipped"
            };

            return _journaler.FailedAttempt(
                task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                attemptOutcome,
                new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.ActionFailed,
                    ActionExitCode = action.ExitCode,
                    Summary = summary
                },
                costUsd: action.CostUsd);
        }

        // --- staging move (SSOT §3.5, issue #130): after action success, BEFORE the write-scope
        // check and BEFORE guardrails. Move the action's staged .claude/-destined files into their
        // real .claude/ paths in the EFFECTIVE workspace, then delete the staging tree. Gated on
        // action success (a failed action never reaches here). An empty-source / IO failure is a
        // guardrail-class failed attempt with actionable feedback (RetryPolicy.ForStagingFailure).
        if (stagingDir is not null && task.StagingOutputs is { Count: > 0 } stagingEntries)
        {
            StagingMoveResult moveResult = StagingMover.Move(stagingDir, effectiveWorkspace, stagingEntries);
            if (!moveResult.Succeeded)
            {
                string feedback = RetryPolicy.ForStagingFailure(
                    task, attemptNumber, moveResult.FailureReason ?? "the staging move did not complete");
                return _journaler.FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.GuardrailFailed,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.GuardrailFailed,
                        ActionExitCode = action.ExitCode,
                        Summary = $"staging move failed: {moveResult.FailureReason}"
                    },
                    costUsd: action.CostUsd);
            }
        }

        // --- needsHarnessWrite escape hatch (issue #191, SSOT §9): after action success, BEFORE the
        // write-scope check and guardrails — the .NET harness process itself performs a write the
        // action's own subprocess could never make (a .claude/ path the Claude Code runtime refuses
        // unconditionally, broader than the new-subdirectory-only gap #101 fixed, and unaffected by
        // dangerouslyDisableSandbox). Prospective validation (workspace-escape ALWAYS; writeScope
        // membership when declared) runs BEFORE the write, reusing the SAME predicates the
        // retrospective write-scope check uses below — so the two enforcement points can never drift.
        // A rejected/failed write is treated as an ACTION FAILURE (skip guardrails, retry with
        // actionable feedback) — this escape hatch unblocks write MECHANICS only, never verification:
        // an in-scope write still falls through to the write-scope check (which will also see the
        // just-written file — expected, not redundant) and the task's own guardrails, exactly as any
        // other successful action does.
        if (action.HarnessWriteRequest is { } harnessWriteRequest)
        {
            HarnessWriteOutcome writeOutcome = HarnessWrite.Validate(
                harnessWriteRequest, effectiveWorkspace, task.WriteScope);

            // The control key is consumed either way — it must never reach the fragment-merge check
            // as a foreign/reserved key (mirrors needsHuman being fully consumed pre-merge).
            HarnessWrite.StripFromFragment(fragmentOutPath);

            if (!writeOutcome.Succeeded)
            {
                string feedback = writeOutcome.WasRejected
                    ? RetryPolicy.ForHarnessWriteOutOfScope(task, attemptNumber, harnessWriteRequest.Path, writeOutcome.FailureReason!)
                    : RetryPolicy.ForHarnessWriteFailed(task, attemptNumber, harnessWriteRequest.Path, writeOutcome.FailureReason!);
                return _journaler.FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.GuardrailFailed,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.GuardrailFailed,
                        ActionExitCode = action.ExitCode,
                        Summary = writeOutcome.WasRejected
                            ? $"needsHarnessWrite rejected: {writeOutcome.FailureReason}"
                            : $"needsHarnessWrite failed: {writeOutcome.FailureReason}"
                    },
                    costUsd: action.CostUsd);
            }
        }

        // --- write-scope check (plan 08 §2/§3.4): after action (and staging move / needsHarnessWrite),
        // before guardrails. Only runs when the task declares a writeScope AND the worktree carries a
        // real git repo path (non-empty TaskBase). Skipped for FakeWorktreeProvider segments.
        if (task.WriteScope is { } declaredScope && IsRealGitSegment(worktree))
        {
            // The stagingOutputs 'to' destinations are IMPLICITLY in-scope (SSOT §3.4/§3.5): a staging
            // task must NOT have to also list its .claude/ destinations in writeScope. The check sees
            // the post-move surface, so the real .claude/ paths the move produced must be authorized.
            IReadOnlyList<string> scopeGlobs = WithImplicitStagingScope(declaredScope, task.StagingOutputs);

            WriteScopeCheckResult scopeCheck = WriteScopeCheck.Check(
                worktree.WorktreePath, worktree.TaskBase, scopeGlobs);

            if (!scopeCheck.Passed)
            {
                // Scoped revert: restore only the out-of-scope paths to taskBase state.
                WriteScopeCheck.ScopedRevert(worktree.WorktreePath, worktree.TaskBase, scopeCheck.OffendingPaths);

                string offendingList = string.Join(", ", scopeCheck.OffendingPaths.Select(o => o.Path));
                string feedback = RetryPolicy.ForWriteScopeViolation(task, attemptNumber, scopeCheck.OffendingPaths);
                AttemptResult scopeFailure = _journaler.FailedAttempt(
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

                // #264: attach the reproduction signals so a DETERMINISTIC script that re-writes the same
                // out-of-scope paths every attempt short-circuits to needs-human instead of burning the
                // whole budget (the observed `10-gitignore` write-scope case). A write-scope violation
                // means the action wrote out-of-scope files — never a no-op — so #174 never applies here;
                // #264 (script + byte-identical action output) is the sibling that fires. The failure
                // fingerprint is the stable set of offending paths + git statuses; the action-output
                // fingerprint is the script's own stdout/stderr. Only ever compared against another
                // write-scope violation's fingerprint (write-scope runs BEFORE guardrails and returns
                // here on violation), so a re-run that instead fails a guardrail simply won't match.
                return scopeFailure with
                {
                    ActionWasNoOp = false,
                    GuardrailFailureFingerprint = FingerprintWriteScopeViolation(scopeCheck.OffendingPaths),
                    ActionOutputFingerprint = FingerprintActionOutput(action)
                };
            }
        }

        // --- guardrails -----------------------------------------------------------------
        IReadOnlyDictionary<string, string> guardrailEnv = BuildGuardrailEnvironment(env, logDir, fragmentOutPath);
        GuardrailRunResult guardrails = await _guardrailRunner.RunAsync(
            task, workspace, guardrailEnv, snapshotPath, logDir, cancellationToken, worktreeRootForHook).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return _journaler.Cancelled(task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(), action.CostUsd);
        }

        if (guardrails.AnyFailed)
        {
            IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();
            string feedback = RetryPolicy.ForGuardrailFailures(task, attemptNumber, guardrails.Results);
            AttemptResult failedResult = _journaler.FailedAttempt(
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

            // #174 / #182: attach the no-op + failure-fingerprint signals so the attempt loop can detect
            // a provable deadlock — an action that changed NOTHING this attempt and a guardrail failure
            // byte-identical to the previous attempt's. Two such attempts in a row cannot converge, so
            // the loop escalates to needs-human immediately rather than burning the rest of the budget.
            // The action-output fingerprint (stdout+stderr) is the serial-mode evidence the action
            // behaved identically across the two attempts — required by the serial gate, ignored by the
            // worktree gate (which proves "no change" via the taskBase file diff instead).
            return failedResult with
            {
                ActionWasNoOp = ActionMadeNoChanges(action, fragmentOutPath, worktree),
                GuardrailFailureFingerprint = FingerprintFailures(failed),
                ActionOutputFingerprint = FingerprintActionOutput(action)
            };
        }

        // --- merge fragment or defer to Scheduler (worktree mode) -----------------------
        // Worktree mode: the segment is a real directory. Validate the fragment but defer the
        // actual merge + git commit to the Scheduler's B1 settle under the integration lock.
        // Serial mode (empty or non-existent WorktreePath): merge immediately as before.
        if (!string.IsNullOrEmpty(worktree.WorktreePath) && Directory.Exists(worktree.WorktreePath))
        {
            return _journaler.ValidateFragmentForSettle(
                task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal, provenance);
        }

        return _journaler.CompleteSucceededOrInvalidFragment(
            task, attemptNumber, startedAt, relativeLogDir, logDir, fragmentOutPath, action, guardrails, isFinal, provenance);
    }

    /// <summary>
    /// True when the segment WILL be reset to <c>taskBase</c> + cleaned before the next attempt — i.e.
    /// this is a real git segment (worktree mode) AND not the final attempt. This is the single
    /// failure-kind-agnostic signal that the attempt's FILE writes are about to be reverted: it gates
    /// the F2 retry reset (below) and, identically, the <c>fileWritesRolledBack</c> disclosure threaded
    /// into the timeout / max-turns retry feedback (issue #167) — so the feedback's claim and the
    /// actual reset can never disagree. Serial mode and the final attempt return false (no reset).
    /// </summary>
    internal static bool WorktreeWillReset(WorktreeHandle worktree, bool isFinal) =>
        !isFinal && IsRealGitSegment(worktree);

    /// <summary>
    /// Retry salvage (issue #195): commit the about-to-be-rolled-back attempt's working tree to
    /// <c>refs/guardrails/&lt;taskId&gt;/attempt-&lt;N&gt;</c> and compute a <c>git diff --stat</c> summary
    /// against <c>taskBase</c>, so the NEXT attempt's feedback can name it. Scoped to the two NON-LOGIC
    /// budget-exhaustion outcomes (<see cref="PromptFailureKind.MaxTurns"/> / <see cref="PromptFailureKind.OutputCap"/>)
    /// — the issue's own scope guard: a <c>guardrail-failed</c> attempt's code may be genuinely WRONG, so
    /// it is never silently preserved by this path (guardrail failures compose their own feedback
    /// elsewhere and do not call this method at all). Returns null (no salvage) when
    /// <see cref="RunConfig.PreserveAttemptsForSalvage"/> is off, the failure kind is not one of the two
    /// above, or the git operations fail for any reason — salvage is a best-effort convenience, never a
    /// reason to fail the attempt or change the rollback that already happens unconditionally.
    /// </summary>
    private SalvageRef? TryPreserveForSalvage(
        TaskNode task, WorktreeHandle worktree, int attemptNumber, PromptFailureKind failureKind)
    {
        if (!_plan.Config.PreserveAttemptsForSalvage)
        {
            return null;
        }

        if (failureKind is not (PromptFailureKind.MaxTurns or PromptFailureKind.OutputCap))
        {
            return null;
        }

        string refName = $"refs/guardrails/{task.Id}/attempt-{attemptNumber}";
        try
        {
            GitWorktreeProvider.PreserveAttemptToRef(worktree.WorktreePath, refName);
            string diffStat = GitWorktreeProvider.DiffStatAgainstBase(worktree.WorktreePath, worktree.TaskBase, refName);
            return new SalvageRef(refName, diffStat, attemptNumber);
        }
        catch (InvalidOperationException)
        {
            // Best-effort: a preservation failure must never fail the attempt or block the existing
            // rollback — the retry proceeds exactly as it would have before #195.
            return null;
        }
    }

    /// <summary>
    /// True when this attempt's action made NO change the harness can observe (issues #174 / #182): it
    /// exited 0 (a successful, no-op-style action — only the success path reaches this), wrote no
    /// state fragment, AND — in a real git segment (worktree mode) — touched no file versus
    /// <c>taskBase</c>. Such an action cannot possibly fix a guardrail failure by being re-run, so when
    /// its guardrail failure also repeats byte-for-byte the loop short-circuits to needs-human.
    /// <para>
    /// SERIAL MODE (#182): there is no <c>taskBase</c> to prove "no file writes", so the file-diff half
    /// is unavailable. A serial attempt is therefore a no-op CANDIDATE on exit-0 + no-fragment alone;
    /// the loop pairs this with the stronger serial gate — the action's stdout/stderr fingerprint must
    /// be IDENTICAL across the two attempts AND the guardrail failure byte-identical — so a task that
    /// silently writes a file (no fragment, no stdout) but whose guardrail nonetheless fails the IDENTICAL
    /// way across two such attempts is still escalated (the unchanged guardrail output proves the write,
    /// if any, was irrelevant to convergence). See the short-circuit block in <see cref="ExecuteAsync"/>.
    /// </para>
    /// <para>
    /// CONSERVATIVE by construction: returns <c>false</c> (never short-circuit) when the action wrote
    /// a fragment, or — in a real git segment — when the git diff reports file changes (the
    /// <see cref="WriteScopeCheck.HasFileChanges"/> fail-open keeps a task that DID work from being
    /// mistaken for a no-op). The serial path never loosens the byte-identical-guardrail-failure
    /// requirement that is the core "cannot converge" evidence.
    /// </para>
    /// </summary>
    private static bool ActionMadeNoChanges(ActionRun action, string fragmentOutPath, WorktreeHandle worktree)
    {
        // A failed action never reaches the guardrail stage; defensively require success anyway.
        if (!action.Succeeded)
        {
            return false;
        }

        // A written state fragment is an observable effect: the action DID something.
        if (File.Exists(fragmentOutPath))
        {
            return false;
        }

        // Serial mode / fake provider: no taskBase to diff against, so "no file writes" is unprovable.
        // The action is a no-op CANDIDATE here; the loop's serial gate (identical action stdout/stderr
        // AND identical guardrail failure across two such attempts) supplies the confidence the file
        // diff would in worktree mode (#182).
        if (!IsRealGitSegment(worktree))
        {
            return true;
        }

        return !WriteScopeCheck.HasFileChanges(worktree.WorktreePath, worktree.TaskBase);
    }

    /// <summary>
    /// A canonical, attempt-stable signature of an attempt's failed guardrails (issue #174): each
    /// failed guardrail's name, one-line reason, and full output, joined with record separators. Two
    /// attempts whose fingerprints are EQUAL produced byte-identical guardrail failures — combined
    /// with both attempts being no-ops, that proves a further attempt cannot differ. Empty/whitespace
    /// inputs never collide with a real failure (a real failure always carries at least a name).
    /// </summary>
    private static string FingerprintFailures(IReadOnlyList<GuardrailResult> failed) =>
        string.Join("", failed.Select(g => $"{g.Name}{g.Reason}{g.Output}"));

    /// <summary>
    /// A canonical signature of an attempt's ACTION output — its stdout joined to its stderr with a
    /// record separator (issue #182). In serial mode, where there is no <c>taskBase</c> to diff files
    /// against, two attempts whose action-output fingerprints are EQUAL produced byte-identical stdout
    /// and stderr — the proxy for "the action behaved identically this attempt". Combined with both
    /// attempts being serial no-op candidates (exit 0, no fragment) AND a byte-identical guardrail
    /// failure, this is the conservative serial signal that a further attempt cannot differ. A prompt
    /// action carries empty plain streams (its transcript is the stream-json file, not stdout), so its
    /// fingerprint is the empty string — for a prompt action the guardrail-failure identity remains the
    /// decisive evidence. The two streams are joined with a record separator so a stdout/stderr
    /// boundary cannot collide (stdout "ab"+stderr "c" must not equal stdout "a"+stderr "bc").
    /// </summary>
    private static string FingerprintActionOutput(ActionRun action) =>
        string.Concat(action.StandardOutput, "", action.StandardError);

    /// <summary>
    /// A canonical signature of a WRITE-SCOPE violation (issue #264): each offending path's git
    /// change-status letter + path, joined with record/unit separators and prefixed so it can never
    /// collide with a guardrail-failure fingerprint. Two attempts whose write-scope violations reproduce
    /// byte-identically — a DETERMINISTIC script re-writing the same out-of-scope paths every attempt —
    /// carry EQUAL fingerprints; combined with byte-identical action output, the loop escalates to
    /// needs-human instead of re-running the unchanged script the rest of the budget. Status + path are
    /// attempt-stable; the forensic preview is intentionally excluded (irrelevant to convergence).
    /// </summary>
    private static string FingerprintWriteScopeViolation(IReadOnlyList<WriteScopeOffense> offenses) =>
        "write-scope" + string.Join("", offenses.Select(o => $"{o.Status}{o.Path}"));

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

    /// <summary>
    /// Build the #198 per-attempt provenance the harness knows at launch: the resolved model, the
    /// segment worktree (branch + path), and the base commit. Returns null in serial mode (no segment)
    /// UNLESS a model is resolvable — a serial prompt task still records its model so <c>run.json</c>
    /// discloses which model ran even without a worktree. In worktree mode the segment fields are
    /// always populated; the model is null for a script task (no model runs).
    /// </summary>
    private Journal.AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree)
    {
        string? model = ResolveModel(task);
        bool realSegment = IsRealGitSegment(worktree);

        if (model is null && !realSegment)
        {
            return null;
        }

        return new Journal.AttemptProvenance
        {
            Model = model,
            SegmentBranch = realSegment ? NullIfEmpty(worktree.SegmentBranchName) : null,
            WorktreePath = realSegment ? NullIfEmpty(worktree.WorktreePath) : null,
            BaseCommit = realSegment ? NullIfEmpty(worktree.TaskBase) : null
        };
    }

    /// <summary>
    /// The model an agent attempt of <paramref name="task"/> runs on (issue #198, fixed for the #200
    /// task.json <c>action.model</c> override): resolved via the SAME precedence
    /// <see cref="ActionRunner"/> applies at invocation time —
    /// <see cref="PromptExecutionSupport.ResolveModelForDisplay"/> — so provenance can never drift from
    /// what actually ran: task.json <c>action.model</c> (if set) &gt; the task's prompt-runner config
    /// <c>model</c> (if set) &gt; the sentinel <c>"(cli default)"</c> when neither is set (so the
    /// provenance is never a silent gap for a prompt task). Null for a script task — no model runs — and
    /// the sentinel when the task's runner cannot be resolved (a malformed plan that validation would
    /// already reject).
    /// </summary>
    private string? ResolveModel(TaskNode task)
    {
        if (task.Action.Kind != ActionKind.Prompt)
        {
            return null;
        }

        string? runnerName = task.Action.Runner ?? _plan.Config.DefaultPromptRunner;
        string? runnerModel = runnerName is not null
            && _plan.Config.PromptRunners.TryGetValue(runnerName, out PromptRunnerConfig? config)
                ? config.Settings.Model
                : null;

        // A prompt task whose runner is unresolvable (validation would reject this) still records that
        // a model ran — the task override if set, else the sentinel — rather than a misleading absence.
        return PromptExecutionSupport.ResolveModelForDisplay(task.Action.Model, runnerModel);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

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
    /// from attempt 2 onward. <c>GUARDRAILS_WORKSPACE</c> is the effective workspace in BOTH modes:
    /// the isolated segment worktree when <paramref name="worktreePath"/> is a real directory (worktree
    /// mode, the segment <see cref="GitWorktreeProvider.Integrate"/> commits), else the plan workspace
    /// (serial mode) — so actions/guardrails reference the workspace uniformly across modes.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildEnvironment(
        TaskNode task,
        int attempt,
        string logDir,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string worktreePath = "",
        string? stagingDir = null)
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

        // GUARDRAILS_WORKSPACE = the effective workspace in BOTH modes: the segment worktree when real
        // (worktree mode), else the plan workspace (serial mode). Set in serial too so a guardrail/action
        // references the workspace uniformly — and so a stagingOutputs move (which lands under the
        // effective workspace) is found by a guardrail checking $GUARDRAILS_WORKSPACE/<to> regardless of
        // mode (#130: the serial gap that failed Linux/macOS CI while Windows's Join-Path masked it).
        env["GUARDRAILS_WORKSPACE"] = !string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath)
            ? worktreePath
            : _plan.Workspace;

        // Staging dir (§3.5): action env only, only when the task declares stagingOutputs. The
        // guardrail env is derived from this action env, so BuildGuardrailEnvironment removes it
        // (guardrails verify the real .claude/ path, not the deleted pre-move scaffolding).
        if (stagingDir is not null)
        {
            env["GUARDRAILS_STAGING_DIR"] = stagingDir;
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

        // §3.5: GUARDRAILS_STAGING_DIR is absent for guardrails — by the time guardrails run, the move
        // has happened and the real .claude/ artifact is the thing to verify; a guardrail reading the
        // staging dir would inspect pre-move scaffolding (already deleted), an anti-pattern.
        env.Remove("GUARDRAILS_STAGING_DIR");

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

    /// <summary>
    /// The process <b>cwd</b> for an action/guardrail (SSOT §5.1) — the EFFECTIVE workspace, so the
    /// cwd matches <c>GUARDRAILS_WORKSPACE</c> and <see cref="EffectiveWorkspace"/> in BOTH modes
    /// (issue #134). In worktree mode (a real git segment) this is the task's isolated SEGMENT
    /// worktree, so files the action writes <i>relative to its cwd</i> — not only via
    /// <c>$GUARDRAILS_WORKSPACE</c> — land in the segment that <see cref="GitWorktreeProvider.Integrate"/>
    /// commits; in serial shared-workspace mode it is the plan <see cref="PlanDefinition.Workspace"/>
    /// (byte-identical to before).
    /// <para>
    /// A <c>WorkingDirectory</c> override is — per SSOT §5.1 — relative to the plan dir. In SERIAL
    /// mode the plan dir is the main checkout's plan dir (byte-identical to before). In WORKTREE mode
    /// the plan folder physically lives <i>inside</i> the segment (it is committed in the repo), so the
    /// override resolves relative to the SEGMENT's copy of the plan dir (issue #135) — otherwise an
    /// override-using task's cwd would escape into the user's main checkout, the same write-escape
    /// class as #134. <c>GUARDRAILS_PLAN_DIR</c> and the prompt-runner <c>--add-dir</c> grant remain
    /// anchored to the MAIN checkout (harness-owned state I/O lives there, #134) — this redirect is
    /// purely the process <b>cwd</b>.
    /// </para>
    /// </summary>
    private string ResolveWorkingDirectory(TaskNode task, WorktreeHandle worktree)
    {
        if (string.IsNullOrWhiteSpace(task.Action.WorkingDirectory))
        {
            return EffectiveWorkspace(worktree);
        }

        string mainCheckoutAnchor = Path.GetFullPath(
            Path.Combine(_plan.PlanDirectory, task.Action.WorkingDirectory));

        // Serial mode (no real git segment): anchor at the main-checkout plan dir, byte-identical to
        // before. Only worktree mode redirects the override into the segment.
        if (!IsRealGitSegment(worktree))
        {
            return mainCheckoutAnchor;
        }

        // Worktree mode: re-anchor the override under the segment's copy of the plan dir. The plan dir
        // lives at <workspace>/<rel> in the main checkout; its segment twin is at <segment>/<rel>.
        // Canonicalize both endpoints (#135 edge 1) so GetRelativePath compares like-for-like — without
        // it a symlinked TEMP root (macOS /var → /private/var, and the symlinked CI temp dirs) can make
        // a genuinely-nested plan dir look like it escapes the workspace and emit a spurious "..".
        string canonicalWorkspace = Canonicalize(_plan.Workspace);
        string canonicalPlanDir = Canonicalize(_plan.PlanDirectory);
        string relPlanFromWorkspace = Path.GetRelativePath(canonicalWorkspace, canonicalPlanDir);

        // Edge 2: the plan dir is NOT under the workspace (rel escapes — starts with ".." or is rooted).
        // Worktree isolation of the override cannot be expressed (there is no segment twin of a plan dir
        // that lives outside the checked-out tree), so fall back to the main-checkout anchor rather than
        // fabricate a broken segment path. Normal plans nest the plan folder inside the repo (under the
        // workspace), so this is the abnormal case.
        if (EscapesBase(relPlanFromWorkspace))
        {
            return mainCheckoutAnchor;
        }

        // Re-anchor: <segment>/<rel-plan-dir>/<override>. Path.GetFullPath normalizes any ".."/subdirs
        // in the override (edge 3) — an override like "../sibling" that resolves OUTSIDE the segment is
        // a misconfiguration we resolve rather than crash on; containment is not hard-enforced here.
        string segmentPlanDir = Path.Combine(worktree.WorktreePath, relPlanFromWorkspace);
        return Path.GetFullPath(Path.Combine(segmentPlanDir, task.Action.WorkingDirectory));
    }

    /// <summary>
    /// Canonicalize an existing directory path for a like-for-like <see cref="Path.GetRelativePath"/>
    /// comparison (#135 edge 1): <see cref="Path.GetFullPath"/> normalizes separators and collapses
    /// <c>..</c>, and — when the directory exists — <see cref="Directory.ResolveLinkTarget"/> resolves
    /// a final-segment symlink (the shape of a symlinked TEMP/CI root). Best-effort: a missing path or
    /// a resolve failure returns the <see cref="Path.GetFullPath"/> form, never throws.
    /// </summary>
    private static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            // returnFinalTarget: true follows a chain of links on the final segment to its real target.
            FileSystemInfo? target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            return target is not null ? Path.GetFullPath(target.FullName) : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>
    /// True when a relative path produced by <see cref="Path.GetRelativePath"/> does NOT stay within
    /// its base: it climbs out (a leading <c>..</c> segment) or came back rooted (the two paths share
    /// no common root, e.g. different drives on Windows). Such a path cannot be re-anchored under the
    /// segment (#135 edge 2).
    /// </summary>
    private static bool EscapesBase(string relativePath) =>
        Path.IsPathRooted(relativePath)
        || relativePath == ".."
        || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relativePath.StartsWith("../", StringComparison.Ordinal);

    /// <summary>
    /// Serial-only cwd resolution for <see cref="RevalidateAsync"/> (issue #102): there is no segment
    /// — the CLI refuses worktree mode for <c>--revalidate-task</c> (an in-place fix in the user's
    /// checkout is invisible to a fresh segment) — so the cwd is always the plan
    /// <see cref="PlanDefinition.Workspace"/> where the human's fix lives.
    /// </summary>
    private string ResolveRevalidateWorkingDirectory(TaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.Action.WorkingDirectory))
        {
            return _plan.Workspace;
        }

        return Path.GetFullPath(Path.Combine(_plan.PlanDirectory, task.Action.WorkingDirectory));
    }

    /// <summary>
    /// The EFFECTIVE workspace for staging (SSOT §3.5): the task's isolated SEGMENT worktree in
    /// worktree mode (a real git segment), else the plan <see cref="PlanDefinition.Workspace"/> in
    /// serial shared-workspace mode. This is the tree the action's writes land in and that
    /// <c>Integrate</c> commits — so staging and the move are both rooted here, never in the user's
    /// checkout in worktree mode.
    /// </summary>
    private string EffectiveWorkspace(WorktreeHandle worktree) =>
        IsRealGitSegment(worktree) ? worktree.WorktreePath : _plan.Workspace;

    /// <summary>The per-task staging root <c>&lt;workspace&gt;/.guardrails-staging/&lt;task-id&gt;/</c> (§3.5).</summary>
    private static string StagingDirFor(string effectiveWorkspace, string taskId) =>
        Path.Combine(effectiveWorkspace, ".guardrails-staging", taskId);

    /// <summary>
    /// Best-effort delete of the per-task staging tree (§3.5 rollback). Used on the retry path for a
    /// failed action whose move never ran; a delete failure is swallowed (the next attempt's
    /// pre-create + the segment reset/clean sweep any residue).
    /// </summary>
    private static void ClearStagingTree(string effectiveWorkspace, string taskId)
    {
        string stagingDir = StagingDirFor(effectiveWorkspace, taskId);
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swallowed: the next attempt re-creates the dir and the F2 reset cleans the segment.
        }
    }

    /// <summary>
    /// Combine the task's declared <c>writeScope</c> with the <c>stagingOutputs</c> <c>to</c>
    /// destinations, which are IMPLICITLY in-scope (SSOT §3.4/§3.5): a staging task must not have to
    /// also list its <c>.claude/</c> destinations in <c>writeScope</c>. Each <c>to</c> is added as a
    /// glob (a trailing-slash directory <c>to</c> becomes <c>&lt;to&gt;**</c> so the moved subtree is
    /// covered). The original <c>declaredScope</c> is returned unchanged when there are no staging
    /// outputs, so a non-staging task's check is byte-for-byte identical to before.
    /// </summary>
    private static IReadOnlyList<string> WithImplicitStagingScope(
        IReadOnlyList<string> declaredScope,
        IReadOnlyList<StagingOutput>? stagingOutputs)
    {
        if (stagingOutputs is not { Count: > 0 } outputs)
        {
            return declaredScope;
        }

        var combined = new List<string>(declaredScope);
        foreach (StagingOutput entry in outputs)
        {
            string to = entry.To.Replace('\\', '/');
            // A directory-shaped 'to' ("foo/") covers its whole moved subtree → "foo/**"; a file 'to'
            // is the literal path. Also implicitly cover the staging prefix itself (deleted before the
            // diff, so it nets to zero, but listing it is harmless and self-documenting).
            combined.Add(to.EndsWith('/') ? to + "**" : to);
        }

        combined.Add(".guardrails-staging/**");
        return combined;
    }

    private TimeSpan ResolveTimeout(TaskNode task, int? narrowest)
    {
        int seconds = narrowest
            ?? task.TimeoutSeconds
            ?? _plan.Config.DefaultTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The timeout-extension factor for the current attempt given how many prior attempts timed out
    /// (issue #119): 1× on the first attempt, growing 1.5× per prior timeout, capped at 4× so a
    /// genuinely heavy task gets the wall-clock it demonstrably needs without unbounded growth. A
    /// non-timeout failure does not extend the clock (it would not help).
    /// </summary>
    internal static double TimeoutMultiplierFor(int priorTimeouts) =>
        Math.Min(Math.Pow(1.5, Math.Max(priorTimeouts, 0)), 4.0);

    /// <summary>
    /// The turn-budget-extension factor for the current attempt given how many prior attempts hit the
    /// max-turns cap (issue #129 / #94): 1× on the first attempt, growing 1.5× per prior max-turns
    /// exhaustion, capped at 4× — the same shape and cap as <see cref="TimeoutMultiplierFor"/>. A
    /// genuinely turn-expensive task (an unfamiliar-SDK discovery task) is given the turn headroom it
    /// demonstrably needs without unbounded growth. A non-max-turns failure does not raise the budget.
    /// </summary>
    internal static double MaxTurnsMultiplierFor(int priorMaxTurns) =>
        Math.Min(Math.Pow(1.5, Math.Max(priorMaxTurns, 0)), 4.0);
}
