using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// Turns an attempt's disposition into its journal record and its terminal
/// <see cref="AttemptResult"/> (SSOT §6/§7/§8): merging the state fragment on success, writing
/// <c>feedback.md</c> on failure, and journaling each attempt with the right status transition
/// (<c>succeeded</c>, <c>running</c>/<c>needs-human</c>, or back to <c>pending</c> on cancel).
/// Extracted from <see cref="TaskExecutor"/> so the loop stays a thin orchestrator and every
/// journal transition for a task lives in one place.
/// </summary>
internal sealed class AttemptJournaler
{
    private readonly StateManager _stateManager;
    private readonly RunJournal _journal;

    public AttemptJournaler(StateManager stateManager, RunJournal journal)
    {
        _stateManager = stateManager;
        _journal = journal;
    }

    public AttemptResult CompleteSucceededOrInvalidFragment(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal,
        AttemptProvenance? provenance = null)
    {
        long? mergeSequence = null;

        if (File.Exists(fragmentOutPath))
        {
            long reserved = _journal.ReserveMergeSequence();
            MergeFragmentResult merge = _stateManager.MergeFragment(task.Id, fragmentOutPath, reserved, logDir);

            if (!merge.Merged)
            {
                string reason = merge.Reason ?? "invalid state fragment";
                // A foreign top-level key gets feedback that names the exact stray key so a confused
                // agent drops it on retry (SSOT §6.2, single-writer-per-key); any other rejection uses
                // the generic invalid-fragment feedback. Both route to the same invalid-fragment outcome.
                string feedback = merge.Rejection == FragmentRejection.ForeignKey
                    ? RetryPolicy.ForForeignKey(task, attemptNumber, merge.ForeignKeys)
                    : RetryPolicy.ForInvalidFragment(task, attemptNumber, reason);
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
            LogDir = relativeLogDir,
            Provenance = provenance
        };
        // §7.2 (#274 Part A): stamp the task's definition hash on the serial-mode success settle, so a
        // later resume compares the current definition against it and halts on drift instead of skipping.
        _journal.RecordAttempt(
            task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, TaskDefinitionHash.Compute(task));

        // Always show a cost field so the summary column never reads as a reporting gap (issue #58).
        // Key the marker off the ACTION KIND, not cost-nullness: a succeeded PROMPT action can
        // legitimately have a null CostUsd (the Claude `result` line omitted total_cost_usd, or a
        // non-Claude runner reports no cost — see ClaudeStreamParser), so inferring "no LLM used
        // (script)" from null would lie about a task that DID call a model. A script never invokes a
        // model; a prompt whose cost wasn't reported says exactly that.
        string costSegment = task.Action.Kind == ActionKind.Script
            ? "; no LLM used (script)"
            : action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "; cost not reported";

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed"
                      + costSegment
                      + (mergeSequence is null ? "" : $"; merged (seq {mergeSequence})")
        }, FeedbackPath: null);
    }

    /// <summary>
    /// Worktree-mode success path: validate the fragment (same rules as
    /// <see cref="CompleteSucceededOrInvalidFragment"/>) but do NOT merge into state.json and do
    /// NOT call RecordAttempt. Returns a succeeded <see cref="AttemptResult"/> with
    /// <see cref="TaskResult.FragmentPath"/> set so the Scheduler can perform the B1 deferred settle
    /// (fragment merge → git commit → journal settle) under the integration lock.
    /// </summary>
    public AttemptResult ValidateFragmentForSettle(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal,
        AttemptProvenance? provenance = null)
    {
        string? validatedFragmentPath = null;

        // Worktree mode: a state-rejected non-final attempt has its segment reset to taskBase before
        // the next attempt (TaskExecutor F2 reset), so the attempt's FILE writes are reverted too. The
        // rejection feedback discloses that rollback (issue #162) so the agent re-authors its files
        // instead of fixing only the key and then failing a file-exists guardrail. The final attempt is
        // never reset, so it claims no rollback.
        bool fileWritesRolledBack = !isFinal;

        if (File.Exists(fragmentOutPath))
        {
            string raw;
            try { raw = File.ReadAllText(fragmentOutPath); }
            catch (Exception ex)
            {
                string msg = $"cannot read fragment: {ex.Message}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd);
            }

            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch (JsonException ex)
            {
                string msg = $"fragment is not valid JSON: {ex.Message}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd);
            }

            if (node is not JsonObject fragObj)
            {
                string kind = node is null ? "null" : node.GetValueKind().ToString().ToLowerInvariant();
                string msg = $"invalid state fragment: top-level value must be a JSON object, was {kind}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd);
            }

            List<string> foreignKeys = fragObj
                .Select(pair => pair.Key)
                .Where(k => !string.Equals(k, task.Id, StringComparison.Ordinal) && !StateManager.ReservedMergeKeys.Contains(k))
                .ToList();

            if (foreignKeys.Count > 0)
            {
                string reason = $"foreign top-level key(s): {string.Join(", ", foreignKeys.Select(k => $"'{k}'"))}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForForeignKey(task, attemptNumber, foreignKeys, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = reason },
                    costUsd: action.CostUsd);
            }

            validatedFragmentPath = fragmentOutPath;
        }

        string costSegment = task.Action.Kind == ActionKind.Script
            ? "; no LLM used (script)"
            : action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "; cost not reported";

        // #196: carry the not-yet-journaled attempt data to the Scheduler's B1 settle. The settle
        // records a real AttemptRecord (built from these fields — the SAME shape the serial success
        // path records above) TOGETHER with the reserved mergeSequence, so a succeeded worktree task's
        // Attempts list is non-empty (SSOT §7), matching serial mode. The record is deferred (not
        // written here) because the outcome and the mergeSequence are only known after the integration
        // commit, under the integration lock.
        var pendingAttempt = new PendingAttempt
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            ActionExitCode = action.ExitCode,
            CostUsd = action.CostUsd,
            LogDir = relativeLogDir,
            Provenance = provenance
        };

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            FragmentPath = validatedFragmentPath,
            DeferredSettle = true,
            PendingAttempt = pendingAttempt,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed{costSegment}"
        }, FeedbackPath: null);
    }

    /// <summary>
    /// Record a failed attempt: write <c>feedback.md</c> into the attempt's log dir, journal
    /// the attempt (non-final attempts keep status <c>running</c>; the final one goes
    /// <c>needs-human</c>), and hand the feedback path to the next attempt.
    /// </summary>
    public AttemptResult FailedAttempt(
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

        return new AttemptResult(result, feedbackPath, Outcome: outcome);
    }

    /// <summary>
    /// The rate-limit-exhausted halt (issue #115): a transient pause budget was spent without the
    /// limit clearing. Record one attempt with the <see cref="AttemptOutcome.Timeout"/>-distinct
    /// rate-limit signal and settle the task <c>needs-human</c> — but with a DISTINCT, actionable
    /// reason ("re-run later") so the operator waits rather than debugging a healthy task. Distinct
    /// from a generic budget-exhaustion needs-human.
    /// <para>
    /// <b>Journal vs in-memory outcome (issue #190 part 1).</b> The JOURNAL's <c>status</c> string
    /// stays <c>needs-human</c> (<see cref="JournalTaskStatus.NeedsHuman"/>) — deliberately NOT a new
    /// journal-level status. A rate-limited task IS, durably, halted pending a human/time-based
    /// re-run — exactly what <c>needs-human</c> means for resume purposes (§7: any non-succeeded
    /// status resumes to <c>pending</c> with a fresh budget; a distinct journal status would need its
    /// own <c>ResumeStatus</c> entry that behaves IDENTICALLY, pure churn with no behavioral payoff).
    /// The attempt-level <c>outcome</c> already carries the distinct <c>rate-limited</c> string (SSOT
    /// §7), which is sufficient to reconstruct "why" from the journal on disk. What was missing was the
    /// PER-RUN/UI-facing signal: the live table and the run summary rendered every non-green,
    /// non-blocked outcome as a generic "needs human", indistinguishable from a genuine stuck task. The
    /// fix is therefore <see cref="TaskOutcome"/>-only: <see cref="TaskOutcome.RateLimited"/> is a new
    /// terminal value the CLI's observers/renderers switch on, while the returned <see cref="TaskResult"/>
    /// still reports as non-green (<see cref="TaskResult.IsGreen"/> already excludes anything but
    /// <see cref="TaskOutcome.Succeeded"/>/<see cref="TaskOutcome.Skipped"/>) so scheduling/exit-code
    /// behavior is UNCHANGED.
    /// </para>
    /// </summary>
    public AttemptResult RateLimitExhausted(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string reason,
        TimeSpan pausedFor)
    {
        Directory.CreateDirectory(logDir);
        string summary = $"paused (rate-limited): {reason}; did not clear within " +
                         $"{(int)pausedFor.TotalSeconds}s — re-run later";

        string feedback =
            $"# Task '{task.Id}' is rate-limited\n\n" +
            $"Task: {task.Description}\n\n" +
            $"A transient infrastructure limit did not clear within the pause budget " +
            $"({(int)pausedFor.TotalSeconds}s):\n\n> {reason}\n\n" +
            "This is NOT a task defect and NOT something to debug — the provider was rate-limiting/" +
            "overloaded. RE-RUN this plan later (the harness will resume from here) once the limit " +
            "has cleared. Raise `transientPauseBudgetSeconds` in guardrails.json to wait longer " +
            "automatically.\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = null,
            Outcome = AttemptOutcome.RateLimited,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.RateLimited,
            Summary = summary
        }, FeedbackPath: null, Outcome: AttemptOutcome.RateLimited);
    }

    /// <summary>
    /// The needsHuman short-circuit (SSOT §9): a prompt action wrote a root <c>needsHuman</c>
    /// key to its fragment. Record the attempt with the <c>needs-human</c> outcome and journal
    /// the task <c>needs-human</c> immediately — no retry, no guardrails. Returns a non-green
    /// result so the scheduler blocks dependents.
    /// </summary>
    public AttemptResult NeedsHuman(
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

    /// <summary>
    /// The permission-wall early halt (issues #86 / #104): the runtime refused a write/edit on a path
    /// retrying cannot clear (a <c>.claude/</c> structural path, or the same path across repeated
    /// attempts). Record ONE attempt with the distinct <see cref="AttemptOutcome.PermissionDenied"/>
    /// outcome, write a task-level <c>feedback.md</c> naming the wall and its remediation, and settle
    /// the task <c>needs-human</c> immediately — no further retries. Returns a non-green result so the
    /// scheduler blocks dependents.
    /// </summary>
    public AttemptResult PermissionWall(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        PermissionWallDecision decision)
    {
        Directory.CreateDirectory(logDir);
        string feedback = RetryPolicy.ForPermissionWall(task, decision.StructuralPaths, decision.RepeatedPaths);
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        string paths = string.Join(", ", decision.AllPaths);
        string summary = decision.HasStructural
            ? $"needs human: write to .claude/ blocked by the runtime (structural) — {paths}"
            : $"needs human: write repeatedly refused (permission wall) — {paths}";

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.PermissionDenied,
            CostUsd = action.CostUsd,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            Summary = summary
        }, FeedbackPath: null, Outcome: AttemptOutcome.PermissionDenied);
    }

    /// <summary>
    /// #329: the OUTCOME-AWARE structural <c>.claude/</c>-wall halt. #326 settles a NON-converged attempt
    /// that carries a structural <c>.claude/</c> wall to <c>needs-human</c> on ONE attempt (the #104
    /// fast-halt). This method preserves that halt DECISION unchanged — one recorded attempt, journal
    /// status <c>needs-human</c>, no further retries — but journals the TRUE primary outcome and its
    /// evidence instead of a blanket <see cref="AttemptOutcome.PermissionDenied"/> with an EMPTY
    /// <c>failedGuardrails[]</c>: a guardrail that genuinely ran and FAILED is recorded as
    /// <see cref="AttemptOutcome.GuardrailFailed"/> with its <paramref name="failedGuardrails"/> populated.
    /// The <see cref="TaskResult.Summary"/> and <c>feedback.md</c> LEAD with that cause and disclose the
    /// <c>.claude/</c> wall as SECONDARY context, so a human is not misdirected into chasing a
    /// permission/config issue when a real guardrail failed (issue #329). Returns a non-green result
    /// (<see cref="TaskOutcome.NeedsHuman"/>) so the scheduler blocks dependents, exactly as the
    /// <see cref="PermissionWall"/> halt did.
    /// </summary>
    public AttemptResult StructuralWallHalt(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        AttemptOutcome primaryOutcome,
        string summary,
        string feedback,
        IReadOnlyList<GuardrailResult> guardrailResults,
        IReadOnlyList<FailedGuardrail> failedGuardrails)
    {
        Directory.CreateDirectory(logDir);
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = primaryOutcome,
            FailedGuardrails = failedGuardrails,
            CostUsd = action.CostUsd,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrailResults,
            Summary = summary
        }, FeedbackPath: null, Outcome: primaryOutcome);
    }

    /// <summary>
    /// The task-level preflight short-circuit (two-scope preflights F9, SSOT §7): a RED
    /// <c>tasks/&lt;id&gt;/preflights/</c> slot fired BEFORE the attempt loop. Record ONE attempt with the
    /// distinct <see cref="AttemptOutcome.TaskPreflightFailed"/> outcome carrying the failed preflight
    /// checks (name + actionable reason), write a task-level <c>feedback.md</c> naming what was missing,
    /// and settle the task <c>needs-human</c> — so <c>run.json</c> shows WHAT preflight failed and WHY
    /// (not a bare <c>{status: needs-human, attempts: []}</c>).
    /// <para>
    /// This attempt does NOT burn a retry: the action never ran and the retry budget is never consulted
    /// (the short-circuit returns before the attempt loop AND before <see cref="RunJournal.MarkRunning"/>).
    /// The no-burn property is STRUCTURAL — a preflight-fail record is present but the budget is untouched,
    /// exactly as the SSOT §7 wire example shows (<c>attempts: [ { attempt: 1, outcome: "task-preflight-failed" } ]</c>).
    /// Returns a non-green result so the scheduler blocks the transitive cone.
    /// </para>
    /// </summary>
    public AttemptResult TaskPreflightFailed(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        IReadOnlyList<FailedGuardrail> failedChecks)
    {
        Directory.CreateDirectory(logDir);

        string checkList = string.Join(", ", failedChecks.Select(c => c.Name));
        string detail = string.Join(
            "\n", failedChecks.Select(c => $"- **{c.Name}** — {c.Reason}"));
        string feedback =
            $"# Task '{task.Id}' failed its task-level preflight\n\n" +
            $"Task: {task.Description}\n\n" +
            "A `tasks/<id>/preflights/` check gates this task on a producer having actually delivered in " +
            "the bytes this task inherited. The following preflight check(s) failed, so the task did NOT " +
            "run its action (no retry attempt was burned):\n\n" +
            $"{detail}\n\n" +
            "This is a dependency-delivery gate, not a task defect: fix the upstream producer (or the " +
            "inherited bytes) so the preflight passes, then re-run.\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = null,
            Outcome = AttemptOutcome.TaskPreflightFailed,
            FailedGuardrails = failedChecks,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"task-preflight failed: {checkList}"
        }, FeedbackPath: null, Outcome: AttemptOutcome.TaskPreflightFailed);
    }

    public AttemptResult Cancelled(
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
}

/// <summary>
/// One attempt's terminal result plus the feedback file it left for the next attempt.
/// <see cref="TransientReason"/> is set ONLY for a transient pause (issue #115): the operator-facing
/// cause (with any reset hint), which the loop passes to <see cref="IRunObserver.PromptPaused"/>.
/// <para>
/// <see cref="ActionWasNoOp"/>, <see cref="ActionOutputFingerprint"/> and
/// <see cref="GuardrailFailureFingerprint"/> drive the no-op short-circuit (issues #174 / #182): set
/// ONLY on a guardrail-failed attempt. <see cref="ActionWasNoOp"/> is true when the action exited 0,
/// wrote no state fragment, and — in a real git segment (worktree mode) — made no file changes this
/// attempt; <see cref="GuardrailFailureFingerprint"/> is a canonical signature of the failed
/// guardrails' names + reasons + output; <see cref="ActionOutputFingerprint"/> is a canonical
/// signature of the action's own stdout+stderr (the serial-mode proxy for "the action behaved
/// identically", since serial mode has no <c>taskBase</c> to diff files against).
/// Worktree mode (#174): two consecutive attempts that are BOTH no-ops AND carry the IDENTICAL
/// guardrail fingerprint cannot differ — the loop escalates to needs-human immediately. Serial mode
/// (#182): with no <c>taskBase</c>, the loop additionally requires the action output fingerprint to
/// match across the two attempts before escalating — the loop escalates to needs-human immediately
/// instead of exhausting the retry budget.
/// </para>
/// <para>
/// #264 (deterministic-script reproduction): the SAME three fields also drive a sibling short-circuit
/// for a <c>script</c> action that WROTE FILES (so it is not a no-op and #174 never fires) but whose
/// <see cref="ActionOutputFingerprint"/> reproduced byte-identically across two guardrail-failed
/// attempts — positive evidence the script is deterministic, so re-running it is provably pointless.
/// A write-scope violation (a guardrail-class failure raised before the task's own guardrails) sets
/// <see cref="GuardrailFailureFingerprint"/> to the stable set of offending paths so it participates
/// too. Scoped to worktree mode; the byte-identical action-output requirement is the
/// flaky/nondeterministic-script escape hatch.
/// </para>
/// </summary>
internal sealed record AttemptResult(
    TaskResult Result,
    string? FeedbackPath,
    string? TransientReason = null,
    AttemptOutcome? Outcome = null,
    bool ActionWasNoOp = false,
    string? GuardrailFailureFingerprint = null,
    string? ActionOutputFingerprint = null);
