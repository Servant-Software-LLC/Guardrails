using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Guardrails.Core.Graph;
using Guardrails.Core.Io;
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
    private readonly Overwatch? _overwatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _transientDelay;

    /// <summary>
    /// The set of <c>(runner, model)</c> pairs already resolved this run (plan 30 §3.4) — keyed on the
    /// SAME recorded form <see cref="BuildProvenance"/> writes to <see cref="Journal.AttemptProvenance.Model"/>
    /// (<see cref="PromptExecutionSupport.ResolvedModelForDisplay"/>'s output), so two routes that both
    /// name no model collapse onto the one sentinel key rather than counting as different routes.
    ///
    /// <para>One <see cref="TaskExecutor"/> serves the whole run and parallel workers call into it
    /// concurrently, so a plain <see cref="HashSet{T}"/> with a check-then-add would let two simultaneous
    /// first attempts on one route both observe "not present" and both record cold. A
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>'s <c>TryAdd</c> is the atomic first-writer-wins
    /// primitive that makes exactly one attempt per route cold per run, even under a race — which of the
    /// two racers wins is unspecified and acceptable.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _invokedRoutes = new(StringComparer.Ordinal);

    public TaskExecutor(
        PlanDefinition plan,
        ProcessRunner processRunner,
        InterpreterMap interpreterMap,
        StateManager stateManager,
        RunJournal journal,
        IRunObserver observer,
        PromptRunnerRegistry? promptRunners = null,
        Overwatch? overwatch = null,
        Func<TimeSpan, CancellationToken, Task>? transientDelay = null)
    {
        _plan = plan;
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer;
        _overwatch = overwatch;
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
        // #269 WEAK-2: the cumulative extra attempts every overwatcher grant combined has added to `budget`,
        // hard-capped at MaxCumulativeGrantedRetries so repeated grants can never grow the budget without limit.
        int grantedRetriesTotal = 0;
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

            // #269 overwatcher: fires AT MOST ONCE per attempt (Decision C). A short-circuit consult
            // (a floor boundary) takes precedence over the eager consult so both never fire the same attempt.
            bool overwatchConsulted = false;

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
                //
                // #532: no provenance passed here, deliberately. This is the PRE-attempt cancel — we are
                // between attempts inside a transient backoff, no route has been resolved on this pass
                // and no model has run, which is why costUsd is null too. The route lives in
                // RunAttemptAsync, and reaching for it here would be a second derivation of a decision
                // that must have exactly one. The mid-attempt cancels, which CAN carry real spend, are
                // the two Cancelled call sites inside RunAttemptAsync and they now pass it.
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
                // A class-(b) transient that PAUSED at least once and then cleared within budget (issue #115)
                // is surfaced as a resolved-transient signal on the success (doc 12 §4.2): the executor already
                // re-ran the paused attempt to green, so the autonomous layer records `blocker-retried` from
                // this WITHOUT re-running any wait. Null when the task never paused (backoff untouched).
                ResolvedTransient? resolvedTransient = backoff.PauseCount > 0
                    ? new ResolvedTransient { Pauses = backoff.PauseCount, Waited = backoff.Elapsed }
                    : null;

                return attempt.Result with
                {
                    // taskStartedAt is UTC; the subtraction is drift-free elapsed wall time.
                    // DateTimeOffset.Now (local) is used only for the human-readable HH:mm:ss
                    // stamp — intentional so the display matches the developer's clock.
                    Summary = $"{attempt.Result.Summary}; took {FormatDuration(DateTimeOffset.UtcNow - taskStartedAt)}, " +
                              $"done {DateTimeOffset.Now:HH:mm:ss}",
                    ResolvedTransient = resolvedTransient
                };
            }

            // Other terminal outcomes do not retry: cancellation, plus the needs-human escalations that
            // skip the remaining budget — the prompt-action needsHuman short-circuit (SSOT §9), the
            // permission-wall halt (issues #86 / #104 / #325: an EAGER #86 repeated-path halt, or an
            // outcome-aware structural .claude/ halt on a non-converged attempt), and the §6.2 no-route
            // settle (#201) that never launched an attempt at all, all of which surface as
            // TaskOutcome.NeedsHuman.
            if (attempt.Result.Outcome is TaskOutcome.Cancelled or TaskOutcome.NeedsHuman)
            {
                // #269 overwatcher: a PERMISSION WALL (a floor boundary that may fire on attempt 1) gets a
                // diagnose-only consult that ENRICHES the halt (never grants — a wall needs a config/
                // permission change, not a guidance/budget lever). An AGENT-emitted needsHuman is left
                // untouched: the human is already being asked, exactly as the terminal triage skips it.
                if (attempt.Outcome == AttemptOutcome.PermissionDenied && _overwatch is not null)
                {
                    OverwatchDecision wallDecision = await _overwatch.EvaluateAsync(
                        OverwatchTrigger.PermissionWall, task, _plan, attemptIndex, TaskLevelLogDir(task.Id),
                        _journal, _observer, cancellationToken).ConfigureAwait(false);
                    if (wallDecision.RichHaltSummary is { } wallRich)
                    {
                        return attempt.Result with { Summary = $"{attempt.Result.Summary} — {wallRich}" };
                    }
                }

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
                // #269 overwatcher (a FLOOR boundary): the deterministic short-circuit is about to fire. The
                // overwatcher may UN-HALT it ONLY by applying a SANCTIONED change (guidance/budget) that makes
                // the next attempt materially different — so #174/#264's "no observable change + byte-identical
                // failure" no longer describes it. With NO sanctioned change (the default, and always
                // non-interactive/`halt`) the floor stands and the task halts, now with a richer diagnosis.
                OverwatchTrigger scTrigger = noOpDeadlock ? OverwatchTrigger.NoOpDeadlock : OverwatchTrigger.DeterministicScript;
                OverwatchDecision scDecision = _overwatch is not null
                    ? await _overwatch.EvaluateAsync(
                        scTrigger, task, _plan, attemptIndex, TaskLevelLogDir(task.Id), _journal, _observer, cancellationToken)
                        .ConfigureAwait(false)
                    : OverwatchDecision.NoAction;
                overwatchConsulted = true;

                if (scDecision.Kind == OverwatchDecisionKind.Grant)
                {
                    // Un-halt: apply the sanctioned change and FALL THROUGH to the normal carry-forward + F2
                    // reset + next attempt. The floor did not fire because its precondition (a byte-identical
                    // no-op) will no longer hold once the injected guidance/budget lands.
                    ApplyOverwatchGrant(scDecision, ref feedbackPath, ref budget, ref grantedRetriesTotal, task);
                }
                else
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
                    string richSuffix = scDecision.RichHaltSummary is { } r ? $"; {r}" : "";

                    return last with
                    {
                        Outcome = TaskOutcome.NeedsHuman,
                        Summary = $"{last.Summary} — {why}{richSuffix} (after {attemptIndex} identical attempt(s))"
                    };
                }
            }

            // #269 overwatcher EAGER trigger (Decision C): a NON-final failing attempt at attempt ≥ 2 that
            // did NOT hit a floor boundary this attempt. This is the advisory core — it NEVER gates a task the
            // deterministic policy would keep retrying (a non-grant outcome is advisory, the loop continues);
            // it may only ENRICH the next attempt with a sanctioned allowlist change (guidance/budget). Fires
            // at most once per attempt (skipped when the short-circuit already consulted).
            if (!overwatchConsulted && _overwatch is not null && !isFinal && attemptIndex >= 2)
            {
                OverwatchDecision eager = await _overwatch.EvaluateAsync(
                    OverwatchTrigger.EagerAttempt, task, _plan, attemptIndex, TaskLevelLogDir(task.Id),
                    _journal, _observer, cancellationToken).ConfigureAwait(false);
                overwatchConsulted = true;

                if (eager.Kind == OverwatchDecisionKind.Grant)
                {
                    ApplyOverwatchGrant(eager, ref feedbackPath, ref budget, ref grantedRetriesTotal, task);
                }
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

        // Budget exhausted → needs-human via exhaustion. This is the §9.2.1 TERMINAL case of the overwatcher
        // — it subsumes the shipped advisory triage (plan 08 §9): the overwatcher delegates to the composed
        // NeedsHumanTriage (unchanged feedback.md/triage.json) and records the halt to decisions[] +
        // overwatch.jsonl. Advisory: EvaluateTerminalAsync swallows a thrown/errored triage internally.
        string? triageFeedbackPath = null;
        if (_overwatch is not null)
        {
            try
            {
                triageFeedbackPath = await _overwatch.EvaluateTerminalAsync(
                    task, _plan, TaskLevelLogDir(task.Id), _plan.PlanDirectory, _plan.Workspace,
                    _journal, _observer, _plan.Config.TriageAutoFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Overwatch/triage is advisory — exceptions must never abort the run or change the verdict.
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

        // No action attempt here, so there is no ACTOR route to thread and none is invented (#201/§6.5).
        // The judge still RESOLVES — rule 1's frontmatter pin, §6.5.1's floor and the default pointer all
        // apply with no actor rung to key off — it simply resolves against nothing on the rules that need one.
        // BOTH attempt records below therefore carry that resolution as a judge-only provenance
        // (JudgeOnlyProvenance): a revalidate graded by a model must say WHICH model graded it.
        GuardrailRunResult guardrails = await _guardrailRunner.RunAsync(
            task, workspace, env, snapshotPath, logDir, route: null, cancellationToken).ConfigureAwait(false);

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
                // Plan 30 §3.4: a revalidate runs NO action — the human's in-place fix is the "attempt" —
                // so the ACTION half is absent and only the guardrail pass, which genuinely ran and was
                // measured inside GuardrailRunner, is recorded. Half-populated on purpose, and the same
                // shape the in-between attempt settles carry with the halves swapped. Null-guarded rather
                // than assumed: AttemptSegments must never be built out of two nulls.
                Segments = guardrails.GuardrailMs is { } failedMs
                    ? new AttemptSegments { GuardrailMs = failedMs }
                    : null,
                LogDir = relativeLogDir,
                Provenance = JudgeOnlyProvenance(guardrails.Judge)
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
            // Plan 30 §3.4: the guardrail half only, for the reason given at the failed sibling above.
            Segments = guardrails.GuardrailMs is { } okMs
                ? new AttemptSegments { GuardrailMs = okMs }
                : null,
            LogDir = relativeLogDir,
            Provenance = JudgeOnlyProvenance(guardrails.Judge)
        };
        // §7.2 (#274 Part A): a revalidate that flips the task to succeeded also stamps its definition
        // hash, so a subsequent resume detects a later definition edit rather than skipping stale.
        // Plan 32 §5.2: the pin captured at load, never a disk recompute — no fallback, ever.
        _journal.RecordAttempt(
            task.Id, record, JournalTaskStatus.Succeeded, definitionHash: task.DefinitionHashAtLoad);

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

        // #201 / DoR §6: THE attempt-launch resolution — which promptRunners block, model, effort and
        // rung this attempt runs on. Resolved HERE, once, immediately before THIS attempt launches
        // (retries included), and the ONE result feeds BOTH the provenance built below and the model the
        // invocation actually runs on (threaded into the ActionRunner call). One resolution, two
        // consumers — never two derivations that agree only by construction, which is the drift #198's
        // provenance and #200's argv override used to have.
        //
        // §6.2's no-route outcome (nothing serves the rung, at it or above it) is settled just below,
        // BEFORE anything launches. Quietly turning it into a legacy launch is the silent fallback D30
        // severed, so it gets its own branch rather than being papered over.
        TierResolution? route = ResolveRoute(task);

        // #198: the provenance the harness knows BEFORE the attempt runs — the resolved route, the
        // segment worktree + base commit, and (#382) the declared/injected tool grants. Written to the
        // attempt log dir as a machine-readable header artifact regardless of outcome, and carried onto
        // the journal AttemptRecord on the success paths below.
        Journal.AttemptProvenance? provenance = BuildProvenance(task, worktree, route);
        AttemptArtifacts.WriteProvenance(logDir, provenance);
        // #382: the same grant split, in prose, at the head of the attempt's own log dir — a human
        // reading logs sees what the harness ADDED to what the plan DECLARED without querying run.json.
        WriteToolGrantHeader(logDir, provenance);
        // #201 / DoR §6.2: the same ROUTE, in prose, beside it — plus the two lines §6.2 requires to be
        // LOUD (a climb, and a binding D28 costly ceiling once this task is on a re-attempt). Built from
        // the SAME `route` and the SAME provenance object resolved above: a disclosure that resolved
        // again would be the third derivation of a decision that must have exactly one.
        WriteRouteDisclosure(logDir, attemptNumber, route, provenance);

        // #201 / DoR §6.2: NO candidate block exists at the requested rung or at any stronger one.
        // Settle needs-human HERE — above the state snapshot, the environment and the runner call —
        // journalling the distinct AttemptOutcome.NoRoute (§12.4) so a human, and #9 triage, reads a
        // routing CONFIG gap rather than a generic action failure. A no-route DISCOVERED AFTER an
        // attempt ran on some fallback is not a no-route, it is a silent fallback wearing the name, and
        // there is nothing to fall back TO: D30 makes legacy the no-RUNG path and this the no-CANDIDATE
        // path, so the runner's own model is out, and a costly block a ceiling excluded is out too —
        // that floor constrains what the HARNESS may choose, and only a human's pin crosses it (D22).
        // No retry either: resolution is a pure function of the tag and the registry, so every further
        // attempt resolves identically. The provenance built above rides the record — `tierSource` says
        // WHERE the unservable rung was asked for, while `provenance.tier`, the rung SERVED, is absent
        // because none was.
        if (route is { NoRoute: true })
        {
            return _journaler.NoRoute(
                task, attemptNumber, startedAt, relativeLogDir, logDir, provenance, NoRouteReason(route));
        }

        // #524: the SAME route, announced to the observers, at LAUNCH. `AttemptModelResolved` below
        // cannot fire until the runner has reported what it ran on, so a surface fed only from it reads
        // a placeholder for the whole attempt (MEASURED at 14m02s and longer on plan 24's run.json), and
        // a §6.2 climb resolved above reaches no console surface at all — only attempt-route.log. Raised
        // here, after the no-route branch has settled and before the action launches, off the SAME
        // `route` and `provenance` the disclosure above was written from: nothing is re-derived and no
        // new plumbing exists. The guard is the precondition, not defensiveness — `RunnerName` is null
        // for no-route and `provenance.Model` is null for a script attempt (ResolveRoute returns null
        // outright there, so no second Kind test is needed), and with nothing to name there is no route
        // to disclose. `route.Climbed` is the ONLY owner of the climb predicate: `requestedTier` is
        // written only when the climb actually moved the rung, so its presence stays the signal.
        if (route is { RunnerName: { } runnerName } && provenance?.Model is { } routeModel)
        {
            _observer.AttemptRouteResolved(
                task, attemptNumber, runnerName, routeModel,
                route.Tier, route.Climbed ? route.RequestedTier : null);
        }

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
        // `route` is the SAME object the provenance above was built from (#201): the model recorded and
        // the model run are one resolution, read twice.
        //
        // Plan 30 §3.4: the ACTION half of the attempt's segmented duration is MEASURED here, around the
        // call, and never read back off the returned ProcessResult — ActionRun.AsProcessResult sets
        // `Duration = TimeSpan.Zero` for a prompt action deliberately (it synthesizes that result for the
        // log artifacts and has no child-process clock to report), so reading it would hand every prompt
        // attempt a confident `0`: a wrong number wearing a measurement's clothes. The guardrail half is
        // measured inside GuardrailRunner for the mirror-image reason — see the clock there.
        var actionClock = Stopwatch.StartNew();
        ActionRun action = await _actionRunner.RunAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, timeoutMultiplier, stagingDir, maxTurnsMultiplier, route, cancellationToken, worktreeRootForHook).ConfigureAwait(false);
        actionClock.Stop();

        // The local is REASSIGNED, exactly as `provenance` is below: ActionRun is immutable and a `with`
        // whose result is discarded changes nothing. Folded onto the run itself rather than kept as a
        // local because every settle below reads its facts off this object.
        action = action with { ActionMs = actionClock.ElapsedMilliseconds };

        AttemptArtifacts.WriteActionLogs(logDir, action.AsProcessResult(), ActionKindLabel(task));

        // --- #349: fold the OBSERVED model onto this attempt's provenance ----------------
        // The runner only reports what it actually ran on once it has run, so the observed model cannot
        // be part of the launch-time provenance built above; it is folded onto that SAME object the
        // moment the action returns, before any journal call below reads it. The local is REASSIGNED
        // because records are immutable — a `with` whose result is discarded changes nothing.
        //
        // Onto the PROVENANCE for the same mechanical reason the D32 judge fold below gives: it is the
        // one member that already rides PendingAttempt, so a value folded here reaches BOTH record
        // construction paths with no further edit — the serial AttemptJournaler AND
        // Scheduler.RecordSucceededSettle (`Provenance = pending.Provenance`), the DEFAULT worktree mode.
        //
        // `model` becomes BEST-KNOWN-ACTUAL — observed, else the resolved route, else the "(cli default)"
        // sentinel BuildProvenance already put there. It goes on answering the same question ("what did
        // this attempt run on") with a better answer wherever one exists, so every existing reader
        // improves with no change on its side.
        //
        // `requestedModel` records what the route ASKED for, and ONLY when the two disagree: its PRESENCE
        // is the mismatch signal, and there is no separate flag beside it. Written on every attempt it
        // would be a duplicate of `model` — which is exactly what the contract refuses a `resolvedModel`
        // key for (JournalModel.cs: two fields claiming the same fact is how they drift). A second field
        // earns its place by carrying the DISAGREEMENT.
        //
        // A runner that reported NOTHING changes nothing at all: silence is not a disagreement, and
        // assigning the observed value unconditionally would erase a real route model — or the sentinel,
        // which is the only thing per-attempt provenance has to say for the operator who configured no
        // model anywhere. A SCRIPT attempt ran no model and (in serial mode) has no provenance object to
        // fold onto, so it is skipped rather than given an object of nulls — the same discipline the
        // judge fold applies to a null provenance.
        // Plan 30 §3.3 (#548): the digest rides the SAME fold, extended rather than duplicated (a
        // second `with` against this local would discard the Model/RequestedModel fold above it —
        // records are immutable, so only the LAST assignment to `provenance` survives). The guard
        // widens to admit a runner that reported a digest with no observed model tag: gating this
        // block on ObservedModel alone would skip the fold entirely and lose that digest. When
        // ObservedModel is absent, `observedModel` below is null, so Model and RequestedModel fall
        // through to their launch-time values unchanged — silence about the model stays silence,
        // exactly as before this fold existed at all.
        string? observedModel = action.ObservedModel;
        if (provenance is { } launched && (observedModel is { } || action.ModelDigest is { }))
        {
            provenance = launched with
            {
                Model = observedModel ?? launched.Model,
                RequestedModel = observedModel is { } && launched.Model != observedModel
                    ? launched.Model
                    : launched.RequestedModel,
                ModelDigest = action.ModelDigest ?? launched.ModelDigest
            };

            // Re-mirror it, for the reason the judge fold re-mirrors below: on the guardrail-FAILED path
            // attempt-provenance.json is the ONLY surface that records this at all, because
            // AttemptJournaler.FailedAttempt takes no provenance parameter. An attempt that learned what
            // actually served it must not lose that the moment it goes red.
            AttemptArtifacts.WriteProvenance(logDir, provenance);

            // #349: and the PROSE twin, which has exactly the same problem. attempt-route.log was written
            // at launch — necessarily, since an attempt that dies before the runner returns must still
            // leave a route log — so it names the model the route ASKED for, stated with the confidence of
            // a fact. Re-written here from the FOLDED object, it names the model that actually ran and
            // gains its `requested model: ` line; the second write supersedes the first, exactly as the
            // re-mirror above does. Rewriting it is the cheap half of keeping the two surfaces from
            // disagreeing about one attempt, and the writer is best-effort, so a rewrite that fails is a
            // stale disclosure — never a failed attempt.
            WriteRouteDisclosure(logDir, attemptNumber, route, provenance);
        }

        // #349: and the LIVE surface. The disclosure above is a file an operator opens after the fact; this
        // is what reaches the task table and the --no-ui stream WHILE the run is going, which is when a
        // substituted model is still worth knowing. Raised HERE because this is the first point at which
        // best-known-actual is settled — the fold above is the ONE place that decides which model this
        // attempt ran on, so both strings go across VERBATIM off the provenance it produced. Recomputing
        // the comparison here would make this a second owner of a rule that must have exactly one, and it
        // would drift from the run.json this event is supposed to be showing.
        //
        // OUTSIDE the fold, not inside it: a runner that reported nothing still ran on the resolved route
        // (or the "(cli default)" sentinel), and that is an ordinary attempt whose model the operator is
        // owed. Raising only on a disagreement would make the model line appear exactly when something is
        // odd and vanish the rest of the time — a surface seen only in trouble teaches nothing about the
        // healthy case it is being compared against. An attempt with no model at all (a script attempt, or
        // serial mode's null provenance) announces nothing: `model` is non-nullable on this signature
        // because a null there has no meaning.
        if (provenance?.Model is { } attemptModel)
        {
            _observer.AttemptModelResolved(task, attemptNumber, attemptModel, provenance.RequestedModel);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Plan 30 §3.4: the EARLIER of the two mid-attempt cancels. The action ran and was timed;
            // no guardrail has, so the pair is ActionMs-only — a half-populated record, not a gap.
            return _journaler.Cancelled(
                task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(),
                action.CostUsd, action.Usage, provenance: provenance, turns: action.Turns,
                segments: AttemptJournaler.SegmentsFor(action));
        }

        // --- needsHuman short-circuit (SSOT §9): record + escalate IMMEDIATELY -----------
        // #554 / plan 31 §3: PRESERVE first. Until now this returned before any salvage call, so an
        // agent that wrote real work and then asked a human left nothing behind — and "the work is
        // discarded" understates it: the attempt loop returns terminally on NeedsHuman BEFORE the F2
        // reset, so the tree is not reset, it is ORPHANED (a resume mints a new runId and a fresh segment
        // at planHead; reuse/fork are intra-run; reclaim only deletes after the staleness threshold).
        // The ref and the patch are therefore the ONLY durable copies anyone — the resumed agent, the
        // triaging human, the firstmate — can be pointed at.
        if (action.NeedsHumanQuestion is { } question)
        {
            return _journaler.NeedsHuman(
                task, attemptNumber, startedAt, relativeLogDir, logDir, action, question,
                action.NeedsHumanOptions, action.NeedsHumanKind, provenance: provenance,
                salvage: TryStashEscalatingAttempt(task, worktree, attemptNumber),
                segments: AttemptJournaler.SegmentsFor(action));
        }

        // --- permission wall observation (issues #86 / #104 / #325) ----------------------
        // Feed this attempt's refused write paths to the cross-attempt tracker UNCONDITIONALLY (the #321
        // observe-filter that dropped .claude/ paths whenever a needsHarnessWrite was present is GONE —
        // subsumed by the outcome-aware halt below), then compute the wall verdict ONCE. WHERE that
        // verdict is consulted is now outcome-aware:
        //   • #86 REPEATED (below, right after the transient-pause check): a NON-.claude/ path refused
        //     across ≥2 attempts is a genuine un-clearable wall — halt EAGERLY, without waiting for the
        //     attempt outcome, because a retry just re-hits the same wall.
        //   • #104/#325 STRUCTURAL (a .claude/ path): consulted only on an attempt that did NOT converge
        //     — the action failed OR the guardrails failed (the two sites below). A CONVERGED attempt
        //     (guardrails pass) goes GREEN regardless of a .claude/ refusal the agent recovered from in
        //     the same attempt. That is the #325 fix: a task extending an EXISTING .claude/ file ran
        //     `cp ".claude/…" <staging>` (the .claude/ path a READ SOURCE), the Bash classifier phrased
        //     it as a WRITE and refused, the agent RECOVERED via the Read tool, the deliverable landed,
        //     and the guardrails passed — such an attempt must be green, not a structural halt. Deferring
        //     to the outcome also SUBSUMES the #321 probe-then-hatch escape-hatch yield: a converged
        //     hatch attempt is green by this same general rule, so no .claude/-specific filter is needed.
        //     #329 REFINES what a non-converged structural halt REPORTS (never WHEN it halts): the
        //     guardrail-failed site reports the true `guardrail-failed` outcome + failedGuardrails[] (with
        //     the .claude/ wall as secondary context), because a guardrail genuinely ran and failed; only
        //     the action-failed site (no guardrail reached — the pure #104 first-attempt wall) still
        //     reports `permission-denied`.
        permissionWalls.Observe(action.BlockedWritePaths);
        PermissionWallDecision wall = permissionWalls.ShouldHalt();

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

        // --- #86 EAGER permission-wall halt ---------------------------------------------
        // A NON-.claude/ path refused across ≥2 attempts (RepeatedPaths) is a strong un-clearable-wall
        // signal that need not wait for the attempt outcome — settle needs-human NOW instead of burning
        // the rest of the budget re-hitting the identical wall. Placed AFTER the transient-pause check
        // so a rate-limited attempt PAUSES and re-runs the SAME attempt rather than halting (a latent
        // ordering bug the #321 early-halt had, fixed here for free). A structural .claude/ wall is
        // deliberately NOT halted here — it defers to the two outcome sites below (only a NON-converged
        // attempt halts on it). Pass a REPEATED-ONLY decision so the feedback/summary wording stays
        // "repeated" (not "structural") even when a .claude/ read-source wall coexists this attempt.
        if (wall.RepeatedPaths.Count > 0)
        {
            return _journaler.PermissionWall(
                task, attemptNumber, startedAt, relativeLogDir, logDir, action,
                new PermissionWallDecision(true, [], wall.RepeatedPaths), provenance: provenance,
                segments: AttemptJournaler.SegmentsFor(action));
        }

        if (!action.Succeeded)
        {
            // #104/#325: an un-converged attempt (the action itself FAILED, so NO guardrail ran) plus a
            // structural .claude/ wall halts needs-human NOW — the .claude/ deliverable cannot have landed
            // and the agent may be stuck against a wall no retry clears (the #104 fast-halt is preserved
            // via this site). This is the PURE permission-wall case #329 deliberately LEAVES as
            // `permission-denied`: no guardrail failure is being hidden (none ran), and the reported
            // `.claude/` wall IS the honest primary cause — the classic #104 first-attempt wall. (#329
            // changes only the GUARDRAIL-failed site below, where a guardrail did run and fail.)
            // RepeatedPaths is provably empty here (any repeat halted eagerly above), so passing the full
            // wall yields structural-only feedback/summary wording.
            if (wall.HasStructural)
            {
                return _journaler.PermissionWall(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, action, wall,
                    provenance: provenance, segments: AttemptJournaler.SegmentsFor(action));
            }

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
            // #306 retry salvage: STASH this about-to-be-rolled-back attempt's full working tree to a git
            // ref + applyable patch BEFORE the F2 reset discards it, then tell the NEXT attempt's feedback
            // where to find it. #306 supersedes #195's scope guard (which restricted salvage to
            // max-turns/output-cap): salvage now fires for EVERY non-final worktree failure kind here —
            // timeout and generic action failures included — because the agent, not the harness, decides
            // how much to reuse. No-op (null) unless ALL of: worktree mode, config opt-in, non-final, and
            // the attempt actually changed something.
            (bool fileWritesRolledBack, SalvageRef? salvageRef) =
                StashIfRollingBack(task, worktree, attemptNumber, isFinal);

            string feedback = action.FailureKind switch
            {
                PromptFailureKind.OutputCap => RetryPolicy.ForOutputCapExceeded(task, attemptNumber, salvageRef, fileWritesRolledBack),
                PromptFailureKind.MaxTurns => RetryPolicy.ForMaxTurnsExceeded(task, attemptNumber, fileWritesRolledBack, salvageRef),
                PromptFailureKind.Timeout => RetryPolicy.ForTimeout(task, attemptNumber, fileWritesRolledBack, salvageRef),
                _ => action.FailureFeedback ?? RetryPolicy.ForActionFailure(task, attemptNumber, action.AsProcessResult(), fileWritesRolledBack, salvageRef)
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
                costUsd: action.CostUsd, usage: action.Usage, provenance: provenance,
                turns: action.Turns,
                // Plan 30 §3.4: the action ran (badly, or into a timeout / turn cap) and its clock is
                // real — that is precisely the cost §2 is missing. The summaries above all say
                // "guardrails skipped", so the guardrail half is honestly absent.
                segments: AttemptJournaler.SegmentsFor(action));
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
                (bool fileWritesRolledBack, SalvageRef? salvageRef) =
                    StashIfRollingBack(task, worktree, attemptNumber, isFinal);
                string feedback = RetryPolicy.ForStagingFailure(
                    task, attemptNumber, moveResult.FailureReason ?? "the staging move did not complete",
                    fileWritesRolledBack, salvageRef);
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
                    costUsd: action.CostUsd, usage: action.Usage, provenance: provenance,
                    turns: action.Turns,
                    // The staging move runs BETWEEN the two phases, so this is another action-only pair.
                    segments: AttemptJournaler.SegmentsFor(action));
            }
        }

        // --- needsHarnessWrite escape hatch (issue #191, SSOT §9): after action success, BEFORE the
        // write-scope check and guardrails — the .NET harness process itself performs a write the
        // action's own subprocess could never make (a .claude/ path the Claude Code runtime refuses
        // unconditionally, broader than the new-subdirectory-only gap #101 fixed, and unaffected by
        // dangerouslyDisableSandbox). #321: this handler is now actually REACHED for a prompt that hit a
        // .claude/ refusal — the permission-wall early halt above yields to the hatch (drops .claude/
        // walls from what it observes when a needsHarnessWrite is present), so the escape-hatch write is
        // no longer pre-empted by the halt. Prospective validation (workspace-escape ALWAYS; the #321
        // permission-file carve-out ALWAYS; writeScope membership when declared) runs BEFORE the write,
        // reusing the SAME predicates the retrospective write-scope check uses below — so the two
        // enforcement points can never drift. A rejected/denied/failed write is treated as an ACTION
        // FAILURE (skip guardrails, retry with actionable feedback) — this escape hatch unblocks write
        // MECHANICS only, never verification: an in-scope write still falls through to the write-scope
        // check (which will also see the just-written file — expected, not redundant) and the task's own
        // guardrails, exactly as any other successful action does. #437: an entry may carry EITHER
        // full `content` (create/replace) OR an anchored `edits` array (modify) — the harness resolves
        // every anchor in memory and writes once, so an unresolvable anchor leaves the target
        // byte-identical and fails the attempt with feedback instead of half-applying. #445: the request
        // may name SEVERAL files (an array of entries) and the whole batch is atomic — every entry of
        // every file resolves before the first byte is written, so a task whose deliverable spans two or
        // more .claude/ files converges in ONE attempt instead of never (a rollback between attempts
        // discards the previous attempt's write, so progress could not accumulate).
        if (action.HarnessWriteBatch is { } harnessWriteBatch)
        {
            HarnessWriteOutcome writeOutcome = HarnessWrite.ValidateAndApply(
                harnessWriteBatch, effectiveWorkspace, task.WriteScope);

            // The control key is consumed either way — it must never reach the fragment-merge check
            // as a foreign/reserved key (mirrors needsHuman being fully consumed pre-merge).
            HarnessWrite.StripFromFragment(fragmentOutPath);

            if (!writeOutcome.Succeeded)
            {
                (bool fileWritesRolledBack, SalvageRef? salvageRef) =
                    StashIfRollingBack(task, worktree, attemptNumber, isFinal);
                // For a multi-entry batch this names EVERY requested path — the failure applies to all of
                // them (nothing was written), and the reason itself identifies the offending entry.
                string requestedPath = harnessWriteBatch.PathForDisplay;
                // #321: a permission-file DENIAL (a .claude/settings*.json) gets its own actionable
                // feedback ("a human must author it") distinct from the generic out-of-scope rejection.
                // #437: a NOT-APPLIED request (bad/ambiguous anchor, wrong mode for the target, an
                // unusable payload) is neither — it is fixable by re-emitting, so its feedback restates
                // the accepted schema. Ordered most-specific first; IsNotApplied/IsPolicyDenied both
                // imply WasRejected, so the generic scope arm must come last.
                string feedback = writeOutcome switch
                {
                    { IsPolicyDenied: true } => RetryPolicy.ForHarnessWriteDenied(task, attemptNumber, requestedPath, writeOutcome.FailureReason!, fileWritesRolledBack, salvageRef),
                    { IsNotApplied: true } => RetryPolicy.ForHarnessWriteNotApplied(task, attemptNumber, requestedPath, writeOutcome.FailureReason!, fileWritesRolledBack, salvageRef),
                    { WasRejected: true } => RetryPolicy.ForHarnessWriteOutOfScope(task, attemptNumber, requestedPath, writeOutcome.FailureReason!, fileWritesRolledBack, salvageRef),
                    _ => RetryPolicy.ForHarnessWriteFailed(task, attemptNumber, requestedPath, writeOutcome.FailureReason!, fileWritesRolledBack, salvageRef)
                };
                return _journaler.FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.GuardrailFailed,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.GuardrailFailed,
                        ActionExitCode = action.ExitCode,
                        Summary = writeOutcome switch
                        {
                            { IsPolicyDenied: true } => $"needsHarnessWrite denied: {writeOutcome.FailureReason}",
                            { IsNotApplied: true } => $"needsHarnessWrite not applied: {writeOutcome.FailureReason}",
                            { WasRejected: true } => $"needsHarnessWrite rejected: {writeOutcome.FailureReason}",
                            _ => $"needsHarnessWrite failed: {writeOutcome.FailureReason}"
                        }
                    },
                    costUsd: action.CostUsd, usage: action.Usage, provenance: provenance,
                    turns: action.Turns,
                    // Same in-between position as the staging failure above: action measured, no
                    // guardrail reached.
                    segments: AttemptJournaler.SegmentsFor(action));
            }
        }

        // --- write-scope check (plan 08 §2/§3.4): after action (and staging move / needsHarnessWrite),
        // before guardrails. Runs whenever the worktree carries a real git repo path (non-empty
        // TaskBase); skipped for FakeWorktreeProvider segments. #389: it runs for a NULL scope too
        // (fail-closed) — writeScope is REQUIRED (GR2041), so a validated plan never reaches here with
        // null, but if one did, a null scope coalesces to [] (writes nothing) and any write is caught.
        if (IsRealGitSegment(worktree))
        {
            // #389: coalesce a null scope to [] here so WithImplicitStagingScope still folds in any
            // stagingOutputs destinations; WriteScopeCheck.Check performs the same fail-closed coalesce.
            IReadOnlyList<string> declaredScope = task.WriteScope ?? [];

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

                // #306: STASH the (now out-of-scope-reverted) attempt so the retry can recover the good
                // IN-SCOPE work instead of re-authoring — and so the feedback stops falsely claiming the
                // in-scope changes "are preserved" when the F2 reset is about to discard them too.
                (bool fileWritesRolledBack, SalvageRef? salvageRef) =
                    StashIfRollingBack(task, worktree, attemptNumber, isFinal);

                string offendingList = string.Join(", ", scopeCheck.OffendingPaths.Select(o => o.Path));
                string feedback = RetryPolicy.ForWriteScopeViolation(
                    task, attemptNumber, scopeCheck.OffendingPaths, fileWritesRolledBack, salvageRef);
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
                    costUsd: action.CostUsd, usage: action.Usage, provenance: provenance,
                    turns: action.Turns,
                    // The write-scope check runs BEFORE the task's own guardrails, so this is the last
                    // of the four action-only pairs.
                    segments: AttemptJournaler.SegmentsFor(action));

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
        // The SAME `route` local the action path was handed above (#201 / DoR §6.5 rule 2): a prompt
        // JUDGE is graded at the rung the actor actually ran at, and it reads that rung off the one
        // resolution this attempt made rather than resolving a second time.
        GuardrailRunResult guardrails = await _guardrailRunner.RunAsync(
            task, workspace, guardrailEnv, snapshotPath, logDir, route, cancellationToken, worktreeRootForHook).ConfigureAwait(false);

        // --- §12.4 / D32: fold the VERIFIER route onto this attempt's provenance ---------
        // A judge resolves DURING the guardrail pass, so it cannot be part of the launch-time provenance
        // built above; it is folded onto that SAME object the moment the pass returns, before any journal
        // call below reads it. The local is REASSIGNED because records are immutable — a `with` whose
        // result is discarded changes nothing.
        //
        // Onto the PROVENANCE specifically, and that is mechanical rather than cosmetic: AttemptProvenance
        // is the one member that already rides PendingAttempt, so a value folded here reaches BOTH record
        // construction paths with no further edit — the serial AttemptJournaler AND
        // Scheduler.RecordSucceededSettle (`Provenance = pending.Provenance`), the DEFAULT worktree mode.
        // A datum hung on the attempt record itself lands in serial mode and silently vanishes in the mode
        // almost every run actually uses.
        //
        // ABSENT, never null: a script attempt and a task whose guardrails are all deterministic resolve no
        // judge at all (§6.5 Invariant 7 — no model ran, so there is no verifier to name), so the whole fold
        // is skipped for them. A judge object built out of nulls is worse than no object — it reads as "a
        // judge resolved and every field was empty".
        if (guardrails.Judge is { } judge)
        {
            // A SCRIPT action in SERIAL mode builds NO launch-time provenance (no model to record, no
            // segment) and can still be graded by a prompt judge. Construct the judge-only object for it —
            // the same shape RevalidateAsync uses — rather than dropping a datum that genuinely resolved,
            // which is precisely how #475's `usage` became a member nothing ever populated.
            provenance = provenance is null
                ? JudgeOnlyProvenance(judge)
                : provenance with { Judge = judge };

            // Re-mirror it (SSOT §8: ONE provenance object recorded in TWO places). Not tidiness: on the
            // guardrail-FAILED path this artifact is the ONLY surface that records the judge at all,
            // because AttemptJournaler.FailedAttempt takes no provenance parameter. An attempt a model
            // graded RED must still say WHICH model graded it.
            AttemptArtifacts.WriteProvenance(logDir, provenance);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Plan 30 §3.4: the LATER mid-attempt cancel — downstream of the guardrail pass, so unlike
            // its sibling above it carries BOTH halves. Same method, a different answer, decided here.
            return _journaler.Cancelled(
                task, attemptNumber, startedAt, relativeLogDir, action.AsProcessResult(),
                action.CostUsd, action.Usage, provenance: provenance, turns: action.Turns,
                segments: AttemptJournaler.SegmentsFor(action, guardrails));
        }

        if (guardrails.AnyFailed)
        {
            IReadOnlyList<GuardrailResult> failed = guardrails.Results.Where(g => !g.Passed).ToList();

            // #325/#329: the guardrails failed AND a structural .claude/ wall is present. #326 halts this
            // needs-human on ONE attempt (bounded 1-attempt cost — the #104 fast-halt: a .claude/ wall no
            // retry clears means an unrecoverable .claude/ deliverable never lands, so the remaining budget
            // is never burned). PRESERVE that halt DECISION exactly — but #326 REPORTED the halt as
            // `permission-denied` with an EMPTY failedGuardrails[], hiding that a guardrail genuinely ran
            // and failed and misdirecting triage (#329: the human reasonably assumes the #325 fix didn't
            // ship). Report the TRUE primary cause instead: outcome `guardrail-failed` with
            // failedGuardrails[] populated, the .claude/ wall disclosed as SECONDARY context (it explains
            // the staging/recovery detour and, when the failure is a MISSING .claude/ deliverable, is the
            // likely reason). RepeatedPaths is provably empty here (any repeat halted eagerly above), so
            // wall.StructuralPaths carries the whole wall.
            if (wall.HasStructural)
            {
                IReadOnlyList<FailedGuardrail> failedList = failed
                    .Select(g => new FailedGuardrail { Name = g.Name, Reason = g.Reason ?? "guardrail failed" })
                    .ToList();
                string failedNames = string.Join(", ", failed.Select(g => g.Name));
                string wallPaths = string.Join(", ", wall.StructuralPaths);
                string summary =
                    $"guardrail(s) failed: {failedNames} — needs human; a .claude/ write was blocked this " +
                    $"attempt ({wallPaths}), which may be why (see feedback)";
                string primaryBody = string.Join(
                    "\n", failedList.Select(g => $"- **{g.Name}** — {g.Reason}"));
                string wallFeedback = RetryPolicy.ForStructuralWallHalt(
                    task, "A guardrail failed", primaryBody, wall.StructuralPaths);
                // #339 N1: mirror the canonical guardrail-failed sibling below — a guardrail that TIMED OUT
                // must record `timeout`, not `guardrail-failed`, even when a .claude/ wall coincided this
                // attempt. Hard-coding GuardrailFailed dropped the timeout/guardrail-failed distinction the
                // sibling keeps.
                // #339 N2 (intended, documented — do NOT re-file): reporting this recovered-wall-then-
                // guardrail-failed halt as guardrail-failed/timeout (rather than #326's permission-denied)
                // means the overwatcher's PERMISSION-WALL diagnose-consult (the sole PermissionDenied
                // consumer, ~line 263) no longer fires for this case. That is correct: this is a genuine
                // guardrail failure whose wall was RECOVERED (a detour), not a wall failure — the consult is
                // scoped to real wall halts. The wall is already disclosed as secondary context in the
                // summary and the feedback, so no diagnosis is lost.
                // Plan 30 §3.4: BOTH halves. The method itself receives only the ActionRun, but the
                // guardrails ran and failed right here — that is what this halt reports — so the
                // GuardrailRunResult in scope supplies the second half rather than leaving it null.
                return _journaler.StructuralWallHalt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, action,
                    guardrails.TimedOut ? AttemptOutcome.Timeout : AttemptOutcome.GuardrailFailed,
                    summary, wallFeedback, guardrails.Results, failedList, provenance: provenance,
                    segments: AttemptJournaler.SegmentsFor(action, guardrails));
            }

            // #306: STASH the guardrail-failed attempt (superseding #195's exclusion of the guardrail
            // path) so the retry gets the artifact back + per-guardrail verdicts, not just a summary. The
            // clean reset is still the default base; the agent chooses how much to reuse.
            //
            // #306 review WEAK-1: EXCEPT when a protected-artifact (tests-untouched-class) guardrail
            // failed — the attempt gamed a check by editing a protected upstream file, so its work must be
            // genuinely UNRECOVERABLE via salvage (not merely un-advertised): suppress the stash AT
            // CREATION so no ref/patch carrying the gamed edit is ever written. This is defense-in-depth;
            // the deterministic per-attempt re-check on the FINAL state is the real backstop that keeps a
            // re-introduced gamed edit from ever reaching green (GuardrailArchetypes remarks). Under
            // failFast a cheaper guardrail may fail first so the protected check never runs — then the
            // stash IS created, and the re-check remains the guarantee if the edit is later re-introduced.
            bool fileWritesRolledBack = WorktreeWillReset(worktree, isFinal);
            bool protectedArtifactGamed = failed.Any(r => GuardrailArchetypes.IsProtectedArtifactCheck(r.Name));
            SalvageRef? salvageRef = fileWritesRolledBack && !protectedArtifactGamed
                ? TryStashFailedAttempt(task, worktree, attemptNumber)
                : null;
            string feedback = RetryPolicy.ForGuardrailFailures(
                task, attemptNumber, guardrails.Results, fileWritesRolledBack, salvageRef);
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
                // Plan 30 §2: the guardrail-failed path is the one the survivorship finding is ABOUT —
                // ten of plan 27's twenty-three attempts settled here carrying nothing attributable.
                costUsd: action.CostUsd, usage: action.Usage, provenance: provenance,
                turns: action.Turns,
                // §3.4, and the same sentence one column over: an attempt that burned twenty minutes
                // before going red is the cost a per-model comparison cannot see today. Both phases ran
                // here, so both are recorded.
                segments: AttemptJournaler.SegmentsFor(action, guardrails));

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

        // --- phase-2 scope-clean (SSOT §3.4, issue #280): the guardrails PASSED. A passing guardrail
        // may legitimately run `npm ci` / a build as a side effect, leaving out-of-scope artifacts in
        // the segment AFTER the phase-1 action check already ran. Re-compute and STRIP them (reusing the
        // same Check + ScopedRevert) so the segment commit carries exactly the in-scope diff. Unlike the
        // phase-1 action check this NEVER fails the attempt — a verifier's side effects are expected; we
        // clean, we don't punish. The reconstructable dep/build set is invisible to Check's staging
        // (SegmentStaging §5.3(D)), so it is never stripped here — those dirs stay on disk (warm-cache
        // #255) and the SegmentStaging exclusion at the Integrate site keeps them out of the commit.
        // Guarded exactly like the phase-1 check: only for a declared writeScope on a real git segment.
        if (task.WriteScope is { } postGuardrailScope && IsRealGitSegment(worktree))
        {
            IReadOnlyList<string> scopeGlobs = WithImplicitStagingScope(postGuardrailScope, task.StagingOutputs);
            IReadOnlyList<WriteScopeOffense> stripped = WriteScopeCheck.StripOutOfScope(
                worktree.WorktreePath, worktree.TaskBase, scopeGlobs);
            if (stripped.Count > 0)
            {
                AttemptArtifacts.WriteScopeCleanNote(logDir, stripped);
                _observer.OutOfScopeStripped(task, stripped);
            }
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
    /// (bool RolledBack, SalvageRef?) for a failed attempt about to be handed to a feedback composer
    /// (issue #306). <c>RolledBack</c> is <see cref="WorktreeWillReset"/> — the single failure-kind-agnostic
    /// signal that this non-final worktree attempt's file writes are about to be discarded by the F2 reset.
    /// When true, the attempt's work is STASHED to a salvage ref + patch (best-effort) so the retry can
    /// recover it; when false (serial mode / the final attempt), there is no reset and nothing to stash.
    /// Called at EVERY failure return site so the composed feedback's rollback/salvage disposition always
    /// matches what actually happens to the tree.
    /// </summary>
    private (bool RolledBack, SalvageRef? Salvage) StashIfRollingBack(
        TaskNode task, WorktreeHandle worktree, int attemptNumber, bool isFinal)
    {
        bool rollingBack = WorktreeWillReset(worktree, isFinal);
        return (rollingBack, rollingBack ? TryStashFailedAttempt(task, worktree, attemptNumber) : null);
    }

    /// <summary>
    /// Retry salvage (issues #195 / #306): STASH the about-to-be-rolled-back attempt's full working tree
    /// to <c>refs/guardrails/&lt;taskId&gt;/attempt-&lt;N&gt;</c> (via <see cref="GitWorktreeProvider.PreserveAttemptToRef"/>,
    /// a throwaway-index side-channel snapshot — never a real commit on the segment branch), then compute a
    /// <c>git diff --stat</c> summary and write a directly-applyable full patch into the attempt's log dir,
    /// so the NEXT attempt's feedback can offer the agent all/some/none of the work.
    /// <para>
    /// Issue #306 makes this <b>failure-kind-agnostic</b>: it fires for EVERY non-final worktree failure
    /// (guardrail-fail, action-fail, timeout, max-turns, output-cap, write-scope, …), superseding #195's
    /// scope guard that restricted preservation to <c>max-turns</c>/<c>output-cap</c>. The clean-slate reset
    /// to <c>taskBase</c> remains the DEFAULT starting point (this does NOT resurrect the work on disk); the
    /// stash is opt-in for the agent, and the per-guardrail verdicts tell it how much already passes. A
    /// guardrail-failed attempt's code may be partly wrong, but the agent — not the harness — decides how
    /// much to reuse, exactly the issue's intent.
    /// </para>
    /// Returns null (no salvage exposed) when <see cref="RunConfig.PreserveAttemptsForSalvage"/> is off, the
    /// attempt was a genuine no-op (empty diff vs <c>taskBase</c> — nothing to salvage), or any git/IO step
    /// fails — salvage is a best-effort convenience, never a reason to fail the attempt or change the F2
    /// reset that happens unconditionally regardless.
    /// </summary>
    private SalvageRef? TryStashFailedAttempt(TaskNode task, WorktreeHandle worktree, int attemptNumber) =>
        TryStash(task, worktree, attemptNumber, restrictToScope: null, dropRefWhenNothingToSalvage: false);

    /// <summary>
    /// Escalation salvage (issue #554, plan 31 §3.4): the twin of <see cref="TryStashFailedAttempt"/> for
    /// the <c>needsHuman</c> short-circuit, differing in exactly the three ways §3.4 names — and each
    /// difference is load-bearing, not incidental:
    /// <list type="number">
    ///   <item><b>The guard is <see cref="IsRealGitSegment"/>, not <see cref="WorktreeWillReset"/>.</b>
    ///     <c>StashIfRollingBack</c> asks "will this attempt be reset?", and on this path that question is
    ///     wrong: no reset follows, and on a FINAL attempt <c>WorktreeWillReset</c> is false — yet a final
    ///     escalating attempt is precisely the one whose work a human is about to build on, because there
    ///     is no next attempt to hand it to, only a person. So it preserves regardless of <c>isFinal</c>.</item>
    ///   <item><b>The staged set is filtered to <c>writeScope</c>.</b> The retry path reaches its stash
    ///     only after the write-scope check and <c>ScopedRevert</c>, so its tree is already scope-clean;
    ///     this site is ~250 lines upstream of both. And the retry path's protected-artifact suppression
    ///     cannot stand in: it keys off the FAILED guardrail list, which is empty here because no
    ///     guardrail ran. The residual is identical to the retry path's post-<c>ScopedRevert</c> state —
    ///     a protected artifact INSIDE the task's own scope is still stashed, and the deterministic
    ///     per-attempt re-check on the next attempt's FINAL state remains the backstop.</item>
    ///   <item><b>An empty filtered diff leaves NO ref.</b> The retry helper writes the ref before it can
    ///     test the diff, and leaves the (harmless) empty ref behind for the settle/--fresh sweep. Here
    ///     there is no settle to sweep it — an escalating task by definition never succeeds — and an
    ///     empty ref would advertise recoverable work that does not exist, so it is deleted again. Note
    ///     the scope filter itself can CREATE this case: an attempt whose every write was out of scope
    ///     produces an empty filtered patch and is correctly offered nothing.</item>
    /// </list>
    /// </summary>
    private SalvageRef? TryStashEscalatingAttempt(TaskNode task, WorktreeHandle worktree, int attemptNumber) =>
        IsRealGitSegment(worktree)
            ? TryStash(task, worktree, attemptNumber, task.WriteScope ?? [], dropRefWhenNothingToSalvage: true)
            : null;

    /// <summary>
    /// The one implementation behind <see cref="TryStashFailedAttempt"/> and
    /// <see cref="TryStashEscalatingAttempt"/>. <paramref name="restrictToScope"/> is null on the retry
    /// path, which keeps its snapshot byte-identical to pre-#554; <paramref name="dropRefWhenNothingToSalvage"/>
    /// is the ORDER difference the escalation path cannot avoid (see that method's remarks).
    /// </summary>
    private SalvageRef? TryStash(
        TaskNode task,
        WorktreeHandle worktree,
        int attemptNumber,
        IReadOnlyList<string>? restrictToScope,
        bool dropRefWhenNothingToSalvage)
    {
        if (!_plan.Config.PreserveAttemptsForSalvage)
        {
            return null;
        }

        string refName = $"refs/guardrails/{task.Id}/attempt-{attemptNumber}";
        try
        {
            GitWorktreeProvider.PreserveAttemptToRef(worktree.WorktreePath, refName, restrictToScope);
            string diffStat = GitWorktreeProvider.DiffStatAgainstBase(worktree.WorktreePath, worktree.TaskBase, refName);
            string patch = GitWorktreeProvider.DiffAgainstBase(worktree.WorktreePath, worktree.TaskBase, refName);

            // A genuine no-op attempt (nothing changed vs taskBase) has nothing to salvage — do not offer
            // a misleading "recover your work" section for an empty diff. On the retry path the (empty)
            // ref is harmless and pruned on settle/--fresh like any other; on the escalation path there is
            // no settle to prune it and an empty ref would advertise work that does not exist, so it goes.
            if (string.IsNullOrWhiteSpace(diffStat) && string.IsNullOrEmpty(patch))
            {
                if (dropRefWhenNothingToSalvage)
                {
                    GitWorktreeProvider.DeleteRef(worktree.WorktreePath, refName);
                }

                return null;
            }

            string? patchPath = AttemptArtifacts.WriteSalvagePatch(AttemptLogDir(task.Id, attemptNumber), patch);
            return new SalvageRef(refName, diffStat, attemptNumber, patchPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Best-effort: a preservation failure must NEVER fail the attempt or block the existing
            // rollback — the retry proceeds exactly as it would have before salvage existed (it just falls
            // back to the honest "rolled-back-and-lost" feedback). #306 review WEAK-2: the catch matches
            // GitWorktreeProvider's sibling fault-capture sites — a git-spawn failure (git off PATH, a bad
            // working dir, ENOMEM) surfaces as Win32Exception, not InvalidOperationException, so catching
            // only the latter would let it escape and crash the attempt, contradicting this docstring.
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
    /// Build the #198 per-attempt provenance the harness knows at launch: the RESOLVED ROUTE (block
    /// name, kind, model, effort and the rung it served — DoR §9.3/§12.4), the segment worktree
    /// (branch + path), the base commit, and (#382) the tool grants split into what the plan DECLARED
    /// and what the harness INJECTED. Returns null in serial mode (no segment) UNLESS a model is
    /// resolvable — a serial prompt task still records its model so <c>run.json</c> discloses which
    /// model ran even without a worktree. In worktree mode the segment fields are always populated; the
    /// route fields and the grants are absent for a script task (no model, no route, no grants).
    ///
    /// <para><paramref name="route"/> is READ, never recomputed: it is the one resolution this attempt
    /// launched under, so what is RECORDED here and what the invocation RUNS on cannot disagree.</para>
    /// </summary>
    private Journal.AttemptProvenance? BuildProvenance(TaskNode task, WorktreeHandle worktree, TierResolution? route)
    {
        // The route's model, in its display form: the resolved string, else the sentinel that says
        // "nothing named a model, so the runner CLI picked". Null only for a script attempt.
        string? model = route is null ? null : PromptExecutionSupport.ResolvedModelForDisplay(route.Model);
        bool realSegment = IsRealGitSegment(worktree);

        if (model is null && !realSegment)
        {
            return null;
        }

        ToolGrantResolution? grants = ResolveToolGrants(task);

        // Warmth (plan 30 §3.4): absent for a script attempt (no route resolved, so "cold" would be a
        // false first-invocation penalty on work that invoked no model), else true on every attempt
        // after the first this run resolves against this exact (runner, model) pair. Keyed on `model`
        // (the RECORDED form, already computed above) rather than the raw `route.Model`, so two routes
        // that both name no model collapse onto the one sentinel key instead of counting as different
        // first invocations.
        bool? routeWarm = null;
        if (route is not null)
        {
            string routeKey = $"{route.RunnerName}|{model}";
            bool cold = _invokedRoutes.TryAdd(routeKey, 0);
            routeWarm = !cold;
        }

        return new Journal.AttemptProvenance
        {
            Model = model,
            RouteWarm = routeWarm,
            // The registry KEY the route selected, and that block's `kind` as its WIRE TOKEN (§12.4 —
            // the journal is read by tooling that never links against this assembly, so the token is the
            // contract, not the enum name). Kind is absent when the name resolved to no block, which is
            // the defensive residual validation already rejects.
            Runner = route?.RunnerName,
            Kind = route?.Runner is { } block ? PromptRunnerKinds.Token(block.Kind) : null,
            // The rung actually SERVED — equal to the one requested unless §6.2's climb moved it, and
            // absent whenever no rung resolved (a pin, the legacy path, a no-route). Recording the
            // REQUESTED rung here instead would make a climb invisible in the record.
            Tier = route?.Tier,
            TierSource = TierSourceFor(task.Action, route),
            // The route's effort is RECORDED, not passed to the CLI: the Claude runner exposes no
            // effort/thinking flag today and PromptRunnerSettings carries no field for one, so spelling
            // an argv flag here would invent a vendor knob that does not exist. When a runner CLASS
            // gains one it reads this same resolved value; until then the record is still honest about
            // what was asked for.
            Effort = route?.Effort,
            SegmentBranch = realSegment ? NullIfEmpty(worktree.SegmentBranchName) : null,
            WorktreePath = realSegment ? NullIfEmpty(worktree.WorktreePath) : null,
            BaseCommit = realSegment ? NullIfEmpty(worktree.TaskBase) : null,
            DeclaredToolGrants = grants is null ? null : DeclaredFrom(grants),
            InjectedToolGrants = grants?.Injected
        };
    }

    /// <summary>
    /// A provenance object carrying NOTHING BUT the verifier route (§12.4 / D32) — what
    /// <see cref="RevalidateAsync"/> records, and what a serial SCRIPT attempt graded by a prompt judge
    /// falls back to, because neither has a launch-time provenance to fold into.
    ///
    /// <para>The route-derived fields are legitimately absent rather than missing: a revalidate runs NO
    /// action, so there is no actor model, no segment and no tool grants to name — but it runs the same
    /// prompt guardrails and resolves a judge exactly as an attempt does, and the one path a human is
    /// actively working through must not be the one path with no record of who graded their fix.</para>
    ///
    /// <para>Null in, null out: a deterministic-only guardrail set resolved no judge, and an object of
    /// nulls would assert the opposite — that a judge resolved and every field about it was empty. The
    /// caller then records no provenance at all, which is what <c>WhenWritingNull</c> already means for
    /// every other attempt.</para>
    /// </summary>
    private static Journal.AttemptProvenance? JudgeOnlyProvenance(Journal.AttemptJudge? judge) =>
        judge is null ? null : new Journal.AttemptProvenance { Judge = judge };

    /// <summary>
    /// WHICH SITE supplied this attempt's rung (DoR §12.4, D31) — READ from the resolution's §6.1 branch
    /// and from the origin <c>PlanLoader</c> recorded when it collapsed the tier at load:
    /// <list type="bullet">
    ///   <item>a full pin ⇒ <see cref="Journal.TierSource.Override"/>. "Bypasses tier resolution
    ///     entirely" governs what is SELECTED, not what is LOGGED: §12.4 gives each v1 value exactly one
    ///     producer, and a pin is override's. <c>provenance.tier</c> stays absent beside it, because no
    ///     rung resolved.</item>
    ///   <item><see cref="TierOrigin.Task"/> ⇒ <c>task</c>, <see cref="TierOrigin.PlanDefault"/> ⇒
    ///     <c>plan-default</c> — with the rung that was served recorded beside it.</item>
    ///   <item>the LEGACY path (no rung anywhere) ⇒ ABSENT. Nothing resolved and nothing was overridden,
    ///     and §12.4 deliberately has no enum value for it — "absent" and "override" are different facts
    ///     about how the attempt got its model, and a reader must be able to tell them apart.</item>
    /// </list>
    ///
    /// <para><b>The origin is READ, never reconstructed.</b> Deriving it by comparing the action's own
    /// tier against the plan-wide default is <c>PlanValidator</c>'s shipped workaround, and it is wrong
    /// in the most ordinary case there is: a task that explicitly writes the same token the plan already
    /// defaults to would be attributed to the plan. <see cref="ActionDefinition.TierOrigin"/> exists
    /// precisely so this mapping is a lookup.</para>
    /// </summary>
    private static Journal.TierSource? TierSourceFor(ActionDefinition action, TierResolution? route) =>
        route switch
        {
            // A script attempt: no route, no rung, nothing to source.
            null => null,
            { Pinned: true } => Journal.TierSource.Override,
            { Legacy: true } => null,
            _ => action.TierOrigin switch
            {
                TierOrigin.Task => Journal.TierSource.Task,
                TierOrigin.PlanDefault => Journal.TierSource.PlanDefault,
                // TierOrigin.None means no tier was written anywhere, which cannot co-exist with a
                // tier-resolved route in a loaded plan. Defensive, and absent is the honest answer.
                _ => null
            }
        };

    /// <summary>
    /// The tool grants an agent attempt of <paramref name="task"/> runs under, resolved through the
    /// SAME <see cref="ClaudePromptRunner.ResolveToolGrants"/> the runner calls when it spells
    /// <c>--allowedTools</c> — so the recorded split can never drift from the set actually granted.
    /// Null for a script task (no grants apply) or a prompt task whose runner cannot be resolved
    /// (a malformed plan validation would already reject): recording a fabricated empty split there
    /// would assert "the plan declared nothing", which is not what the harness knows.
    /// </summary>
    private ToolGrantResolution? ResolveToolGrants(TaskNode task)
    {
        if (task.Action.Kind != ActionKind.Prompt)
        {
            return null;
        }

        string? runnerName = task.Action.Runner ?? _plan.Config.DefaultPromptRunner;
        if (runnerName is null || !_plan.Config.PromptRunners.TryGetValue(runnerName, out PromptRunnerConfig? config))
        {
            return null;
        }

        // The ACTION profile, not the guardrail one: this provenance describes the action attempt.
        return ClaudePromptRunner.ResolveToolGrants(config.EffectiveSettings(isGuardrail: false).AllowedTools);
    }

    /// <summary>
    /// The declared half of a <see cref="ToolGrantResolution"/>: the effective set minus what the
    /// harness injected. Derived by subtraction rather than re-read from config so the two halves are
    /// guaranteed to reconstitute the effective set exactly.
    /// </summary>
    private static IReadOnlyList<string> DeclaredFrom(ToolGrantResolution grants) =>
        grants.Effective.Where(t => !grants.Injected.Contains(t, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// Write <c>attempt-tool-grants.log</c> — the human-readable head of the attempt's log dir naming,
    /// on separate labelled lines, the grants the PLAN DECLARED and the ones the HARNESS INJECTED
    /// (issue #382). The machine-readable copy already rides in <c>attempt-provenance.json</c>; this is
    /// the copy someone scanning a failed attempt's logs actually reads. No-op when the attempt has no
    /// grants to report (a script attempt, or an unresolvable runner). Best-effort: an IO hiccup must
    /// never fail an attempt over a disclosure artifact.
    /// </summary>
    private static void WriteToolGrantHeader(string logDir, Journal.AttemptProvenance? provenance)
    {
        if (provenance?.InjectedToolGrants is not { } injected || provenance.DeclaredToolGrants is not { } declared)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# effective tool grants for this attempt (issue #382)");
        sb.AppendLine("# The harness PROVISIONS the permissions its own protocols prescribe, so the");
        sb.AppendLine("# effective set is wider than task.json/guardrails.json declare. Both halves are");
        sb.AppendLine("# named below; the effective --allowedTools is their concatenation.");
        sb.Append("declared by the plan: ").AppendLine(Describe(declared));
        sb.Append("INJECTED by the harness: ").AppendLine(
            injected.Count == 0
                ? "(none — the plan already declares everything the harness needs)"
                : Describe(injected));

        try
        {
            AtomicFile.WriteAllText(Path.Combine(logDir, "attempt-tool-grants.log"), sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disclosure is best-effort; attempt-provenance.json still carries the same split.
        }
    }

    private static string Describe(IReadOnlyList<string> grants) =>
        grants.Count == 0 ? "(none)" : string.Join(", ", grants);

    /// <summary>
    /// Write <c>attempt-route.log</c> — the human-readable twin of <c>attempt-provenance.json</c>
    /// (issue #201, DoR §6.2/§9.3), a sibling of <c>attempt-tool-grants.log</c> in the attempt's own
    /// log dir. It names the resolved runner block, model and effort, the rung REQUESTED and the rung
    /// SERVED, and the <c>tierSource</c> — then carries the two lines §6.2 requires to be LOUD:
    /// <list type="bullet">
    ///   <item>a <b>climb</b>, naming BOTH rungs — a route change the operator cannot see is a cost and
    ///     latency change they will attribute to the prompt, so §6.2 says a climb is recorded
    ///     <i>and</i> logged rather than silently absorbed.</item>
    ///   <item>a <b>binding D28 costly ceiling on a re-attempt</b>, naming the blocks the harness was
    ///     not permitted to pick. Without it, a failure caused by the weaker model running out of
    ///     reasoning is indistinguishable from an ordinary failure and the operator tunes prompts
    ///     against a constraint they cannot see. From attempt 2 only: the first attempt has not failed
    ///     yet, so a ceiling warning there is noise on every single tiered run.</item>
    /// </list>
    ///
    /// <para><b>Both data are READ off <paramref name="route"/>, never re-derived.</b>
    /// <see cref="TierResolution.Climbed"/> and the
    /// <see cref="TierResolution.CostlyCeilingBound"/>/<see cref="TierResolution.CostlyCeilingBlocks"/>
    /// pair fall out of the resolver's candidacy sweep; re-testing the costly flag here would be a
    /// second copy of the one candidacy predicate D22a forbids duplicating. This changes what is
    /// LOGGED, never what is SELECTED — the costly floor is untouched, and a warning is not a new path
    /// to a costly model.</para>
    ///
    /// <para><b>Called TWICE per attempt (#349).</b> Once at LAUNCH, from the requested route — an attempt
    /// that dies before the runner returns must still leave a route log — and again the moment the observed
    /// model is folded onto the provenance, from that folded object, so the file on disk names the model
    /// that actually RAN and carries <c>requested model: </c> when the two disagree. The second write
    /// supersedes the first, exactly as the provenance re-mirror beside it does; both reads are of the SAME
    /// <paramref name="provenance"/> the fold produced, never a re-derivation.</para>
    ///
    /// <para>No-op when there is no resolution to report: a script attempt has no route at all, and an
    /// attempt with no provenance has no model to disclose. Best-effort, exactly like the tool-grant
    /// header — an IO hiccup must never fail an attempt over a disclosure artifact, and the
    /// machine-readable copy is already safe in <c>attempt-provenance.json</c>.</para>
    /// </summary>
    private static void WriteRouteDisclosure(
        string logDir,
        int attemptNumber,
        TierResolution? route,
        Journal.AttemptProvenance? provenance)
    {
        if (route is null || provenance is null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# route resolved for this attempt (issue #201, DoR §6)");
        sb.AppendLine("# The prose twin of attempt-provenance.json: the promptRunners block, the model");
        sb.AppendLine("# and the effort this attempt launched on, plus which rung was asked for versus");
        sb.AppendLine("# which one was served. §6.2 requires a route change and a bound cost ceiling to");
        sb.AppendLine("# be loud, not merely recorded in a JSON field nobody reads mid-run.");
        sb.Append("runner block: ").AppendLine(route.RunnerName ?? "(none — no block was selected)");
        sb.Append("model: ").AppendLine(provenance.Model ?? "(none)");

        // #349: the model the ROUTE ASKED FOR, beside the one that actually ran — the exact sibling of the
        // `requested tier: `/`served tier: ` pair below, in the same `key: value` idiom. Written ONLY when
        // provenance carries it, which is ONLY on a disagreement: its PRESENCE is the mismatch signal (see
        // AttemptProvenance.RequestedModel), so an always-written line would be a duplicate of `model: ` on
        // the overwhelmingly common agreeing attempt and would say nothing on either. Not a WARNING: the
        // provider serving something else is a disclosure about what ran, not a route the harness changed.
        if (provenance.RequestedModel is { } requestedModel)
        {
            sb.Append("requested model: ").AppendLine(requestedModel);
        }

        sb.Append("effort: ").AppendLine(route.Effort ?? "(none — the runner's own default applies)");
        sb.Append("requested tier: ").AppendLine(RungOrNone(route.RequestedTier));
        sb.Append("served tier: ").AppendLine(RungOrNone(route.Tier));
        sb.Append("tierSource: ").AppendLine(
            provenance.TierSource is { } source
                ? Journal.JournalJson.TierSourceToken(source)
                : "(none — nothing resolved through routing and nothing was overridden)");

        // §6.2's climb, on ONE line carrying BOTH rungs: "served at X" alone reads as an ordinary task
        // at X unless the request it replaced is sitting right beside it.
        if (route.Climbed)
        {
            sb.Append("WARNING: tier climb — asked for '").Append(route.RequestedTier)
                .Append("', served at '").Append(route.Tier)
                .AppendLine("'. No promptRunners block is a candidate at the rung asked for, so the nearest stronger rung with one served this attempt (DoR §6.2). This is a cost and latency change the plan did not ask for; widen a block's `routing.tiers` if it was not intended.");
        }

        // D28: the ceiling becomes news exactly when the cheaper route has already lost once. The pair
        // is READ off the resolution — the sweep that computed it is the only place it can be computed.
        if (attemptNumber >= 2 && route is { CostlyCeilingBound: true, CostlyCeilingBlocks.Count: > 0 })
        {
            sb.Append("WARNING: a cost ceiling bound this re-attempt — ")
                .Append(string.Join(", ", route.CostlyCeilingBlocks.Select(name => $"promptRunners.{name}")))
                .Append(" declare tier '").Append(route.RequestedTier)
                .AppendLine("' or a stronger one but are marked `costly: true`, which the harness never auto-selects (D22/D28), so this re-attempt ran on the weaker route again. A failure here may be the ceiling rather than the prompt: pin one of those blocks on the task (`action.runner` + `action.model`) or clear the flag that excluded it.");
        }

        try
        {
            AtomicFile.WriteAllText(Path.Combine(logDir, "attempt-route.log"), sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disclosure is best-effort; attempt-provenance.json still carries the machine-readable copy.
        }
    }

    /// <summary>
    /// A rung for the disclosure, or the reason there is none — never an empty quote. The absent form
    /// deliberately spells no rung TOKEN, so "a run with zero tier activity names no rung anywhere"
    /// stays literally true of this file.
    /// </summary>
    private static string RungOrNone(string? tier) => tier ?? "(none — no rung resolved)";

    /// <summary>
    /// The ONE §6 attempt-launch resolution for <paramref name="task"/> (issues #198/#200/#201): which
    /// <c>promptRunners</c> block, model, effort and rung this attempt runs on, decided by
    /// <see cref="TierResolver.Resolve"/>'s §6.1 precedence — a full <c>action.runner</c>/
    /// <c>action.model</c> pin, else tier resolution, else the legacy two-level fallback this call
    /// REPLACES (D30 makes that fallback the resolver's own third branch, so it is one code path now
    /// rather than two that agree by construction).
    ///
    /// <para><b>Called once per ATTEMPT, retries included</b> — deliberately not hoisted to a per-task
    /// computation "because v1 is a pure function". Neither input is frozen for the life of a run (a
    /// resumed run whose <c>guardrails.json</c> was edited between sessions moves one mid-run), and this
    /// is the seam the v2 dynamic inputs — probes §6.4, the ladder §7, steering §8 — slot into without
    /// moving it.</para>
    ///
    /// <para>Null for a SCRIPT action: no model, no route, nothing to resolve — which is exactly what
    /// the two-level fallback returned there.</para>
    ///
    /// <para>The CLI-level default model is null because the harness exposes no such setting today, so
    /// the legacy branch's last rung means "let the runner CLI pick its own default" — the fact the
    /// <c>"(cli default)"</c> display sentinel has always stood for.</para>
    /// </summary>
    private TierResolution? ResolveRoute(TaskNode task) =>
        task.Action.Kind == ActionKind.Prompt
            ? TierResolver.Resolve(task.Action, _plan.Config, cliDefaultModel: null)
            : null;

    /// <summary>
    /// The operator-facing diagnosis a §6.2 no-route settle carries (DoR §12.4) — the text that becomes
    /// the <see cref="AttemptOutcome.NoRoute"/> attempt's summary and its <c>feedback.md</c>. It NAMES
    /// the rung that could not be served and says what to CHANGE: "no route" on its own tells an
    /// operator nothing, and this is the one message a human gets before the run stops.
    ///
    /// <para><b>The two causes GR2048 already distinguishes, because their fixes differ.</b> Nothing
    /// DECLARES the rung ⇒ the operator needs a new or widened <c>routing.tiers</c>. The only blocks
    /// declaring it are <c>costly: true</c> ⇒ the operator needs a pin, or to clear the flag; the floor
    /// itself does not move, so the excluded block is named as the CAUSE and never offered as a route
    /// (D22).</para>
    ///
    /// <para><b>Which case it is, is READ off the resolution.</b> The D28 pair
    /// (<c>CostlyCeilingBound</c> and the blocks behind it) fell out of the candidacy sweep and rides
    /// here for exactly this. Re-testing <c>costly</c> would be a second copy of the candidacy
    /// predicate — which D22a forbids, and which would disagree with the resolver the day that
    /// predicate moves.</para>
    /// </summary>
    private static string NoRouteReason(TierResolution route)
    {
        // The rung ASKED for. A no-route always has one (it is the input to the candidacy sweep); the
        // fallback keeps a defensive residual readable rather than printing an empty quote.
        string rung = route.RequestedTier ?? "(unknown)";
        string remedy = $"register a provider serving tier >= '{rung}'";

        return route is { CostlyCeilingBound: true, CostlyCeilingBlocks.Count: > 0 }
            ? $"no route for tier '{rung}': the only block(s) declaring '{rung}' or a stronger tier " +
              $"({string.Join(", ", route.CostlyCeilingBlocks.Select(name => $"promptRunners.{name}"))}) " +
              $"are marked `costly: true`, which the harness never auto-selects — {remedy} that is not " +
              "costly, or pin one of those blocks on the task (`action.runner` + `action.model`), or " +
              "clear its `costly` flag. `guardrails validate` reports this statically as GR2048."
            : $"no route for tier '{rung}': no promptRunners block declares '{rung}' or any stronger " +
              $"tier in its `routing.tiers` — {remedy}, or add the tier to an existing block's " +
              "`routing.tiers`. `guardrails validate` reports this statically as GR2048.";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// The HARD CUMULATIVE ceiling on the extra attempts ALL overwatcher grants combined may add to a single
    /// task's budget (doc 11 §5 "bounded by the retry budget ceiling"). The per-grant clamp
    /// (<see cref="Overwatch"/>) bounds ONE grant; this bounds the sum across every grant a task receives, so
    /// repeated grants (a future grant-capable seam — v2 <c>auto</c> or a mid-run TTY) can never grow the
    /// budget without limit even if every one is approved.
    /// </summary>
    private const int MaxCumulativeGrantedRetries = 4;

    /// <summary>
    /// Apply a #269 overwatcher GRANT (the ALLOWLIST action layer, doc 11 §3.2/§5): inject the sanctioned
    /// ephemeral guidance into the NEXT attempt (appended to the failed attempt's <c>feedback.md</c>, which
    /// the next attempt already reads via <c>GUARDRAILS_FEEDBACK</c>) and extend the retry budget by the
    /// sanctioned extra attempts — clamped to the per-task CUMULATIVE ceiling
    /// (<see cref="MaxCumulativeGrantedRetries"/>) so repeated grants can never grow the budget past it.
    /// Touches NO authored file, no <c>PlanDefinitionHash</c>, no review marker — the safest levers, and the
    /// only ones the overwatcher may apply in v1.
    /// </summary>
    private void ApplyOverwatchGrant(
        OverwatchDecision grant, ref string? feedbackPath, ref int budget, ref int grantedRetriesTotal, TaskNode task)
    {
        if (!string.IsNullOrEmpty(grant.GuidanceInjection))
        {
            feedbackPath = InjectOverwatchGuidance(feedbackPath, grant.GuidanceInjection!, task);
        }

        if (grant.ExtraRetries > 0)
        {
            int remaining = Math.Max(0, MaxCumulativeGrantedRetries - grantedRetriesTotal);
            int allowed = Math.Min(grant.ExtraRetries, remaining);
            budget += allowed;
            grantedRetriesTotal += allowed;
        }
    }

    /// <summary>
    /// Append a <c>## Overwatch guidance</c> section to the failed attempt's <c>feedback.md</c> (so the next
    /// attempt sees it inlined by <see cref="Prompts.PromptComposer"/>), or write a fresh guidance file into
    /// the task-level log dir when there is no feedback file. Best-effort — a write hiccup falls back to the
    /// existing feedback path (the grant then reduces to a budget bump, never a crash).
    /// </summary>
    private string InjectOverwatchGuidance(string? feedbackPath, string guidance, TaskNode task)
    {
        string section = $"\n\n## Overwatch guidance\n\n{guidance.Trim()}\n";

        if (feedbackPath is { } path && File.Exists(path))
        {
            try
            {
                File.AppendAllText(path, section, new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to a fresh file.
            }
        }

        string taskLogDir = TaskLevelLogDir(task.Id);
        try
        {
            Directory.CreateDirectory(taskLogDir);
            string fresh = Path.Combine(taskLogDir, "overwatch-guidance.md");
            File.WriteAllText(fresh, $"# Overwatch guidance{section}", new UTF8Encoding(false));
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return feedbackPath ?? "";
        }
    }

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
    /// <para>
    /// The <c>Remove</c> calls below are only meaningful because
    /// <see cref="ProcessRunner.ApplyEnvironment"/> makes the returned dictionary the child's COMPLETE
    /// view of the <c>GUARDRAILS_*</c> namespace (issue #442). Before that, they merely withheld a key
    /// from the overlay, which a harness process carrying its own <c>GUARDRAILS_STATE_OUT</c> then
    /// handed to the guardrail child by plain inheritance anyway.
    /// </para>
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
    /// Canonicalize a directory path for a like-for-like <see cref="Path.GetRelativePath"/> comparison
    /// (#135 edge 1) — full-path normalization plus SYMLINK resolution, so a symlinked TEMP/CI root
    /// (macOS <c>/var</c> → <c>/private/var</c>) cannot make a genuinely-nested plan dir look like it
    /// escapes the workspace and emit a spurious <c>".."</c>. Best-effort: a missing path or a resolve
    /// failure degrades to the <see cref="Path.GetFullPath"/> form, never throws.
    /// </summary>
    /// <remarks>
    /// Issue #452: this used to call <see cref="Directory.ResolveLinkTarget"/> on the path itself, which
    /// resolves the FINAL SEGMENT only and returns null when that segment is not a link — so for
    /// <c>/var/folders/…/workspace</c> it resolved nothing at all, because the link is <c>/var</c>, several
    /// segments up. <see cref="RealPath.Resolve"/> walks every segment, which is what the comment above has
    /// always claimed.
    /// </remarks>
    private static string Canonicalize(string path) => RealPath.Resolve(path);

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
