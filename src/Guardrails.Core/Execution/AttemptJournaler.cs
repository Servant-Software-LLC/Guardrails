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
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence);

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
        bool isFinal)
    {
        string? validatedFragmentPath = null;

        if (File.Exists(fragmentOutPath))
        {
            string raw;
            try { raw = File.ReadAllText(fragmentOutPath); }
            catch (Exception ex)
            {
                string msg = $"cannot read fragment: {ex.Message}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg), isFinal,
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
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd);
            }

            if (node is not JsonObject fragObj)
            {
                string kind = node is null ? "null" : node.GetValueKind().ToString().ToLowerInvariant();
                string msg = $"invalid state fragment: top-level value must be a JSON object, was {kind}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg), isFinal,
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
                    RetryPolicy.ForForeignKey(task, attemptNumber, foreignKeys), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = reason },
                    costUsd: action.CostUsd);
            }

            validatedFragmentPath = fragmentOutPath;
        }

        string costSegment = task.Action.Kind == ActionKind.Script
            ? "; no LLM used (script)"
            : action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "; cost not reported";

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            FragmentPath = validatedFragmentPath,
            DeferredSettle = true,
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
            Outcome = TaskOutcome.NeedsHuman,
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
/// </summary>
internal sealed record AttemptResult(
    TaskResult Result,
    string? FeedbackPath,
    string? TransientReason = null,
    AttemptOutcome? Outcome = null);
