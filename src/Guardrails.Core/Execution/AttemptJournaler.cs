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

        return new AttemptResult(result, feedbackPath);
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

/// <summary>One attempt's terminal result plus the feedback file it left for the next attempt.</summary>
internal sealed record AttemptResult(TaskResult Result, string? FeedbackPath);
