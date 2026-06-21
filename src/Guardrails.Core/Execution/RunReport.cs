namespace Guardrails.Core.Execution;

/// <summary>The result of running a single guardrail.</summary>
public sealed record GuardrailResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }

    /// <summary>One-line actionable reason on failure (the guardrail's first stdout line), else null.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The guardrail's full captured output on failure (stdout, or stderr when stdout is empty),
    /// for the retry feedback (issue #26 Gap 1). The one-line <see cref="Reason"/> truncates at
    /// the first line, which hid 8-of-9 build errors in a real failure; the retry agent needs
    /// every error, not just the first. Null for passing guardrails and prompt guardrails (whose
    /// signal is the one-line verdict reason).
    /// </summary>
    public string? Output { get; init; }
}

/// <summary>The full result of a single task in an M2 serial run.</summary>
public sealed record TaskResult
{
    public required string TaskId { get; init; }
    public required TaskOutcome Outcome { get; init; }

    /// <summary>The action's exit code, or null when the task was blocked and never ran.</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>Guardrail results in execution order (empty if action failed or task was blocked).</summary>
    public IReadOnlyList<GuardrailResult> Guardrails { get; init; } = [];

    /// <summary>A short human-readable explanation of the outcome (for the summary and logs).</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// In worktree mode, the path to the validated fragment file for deferred B1 settle in the
    /// Scheduler. Null in serial mode (AttemptJournaler handles the merge immediately).
    /// </summary>
    public string? FragmentPath { get; init; }

    /// <summary>
    /// True when the Scheduler must perform the B1 deferred settle (fragment merge → git commit →
    /// journal RecordSettle) for this result. Set by <see cref="AttemptJournaler.ValidateFragmentForSettle"/>
    /// in worktree mode. False in serial mode (AttemptJournaler already merged and journaled).
    /// </summary>
    public bool DeferredSettle { get; init; }

    /// <summary>True only for a genuine success this run (not a resume skip).</summary>
    public bool Succeeded => Outcome == TaskOutcome.Succeeded;

    /// <summary>
    /// True when this task is "green" for the run's overall verdict: it succeeded this run
    /// or was skipped because the journal already recorded it as succeeded (resume).
    /// </summary>
    public bool IsGreen => Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped;
}

/// <summary>The aggregate result of an entire run.</summary>
public sealed record RunReport
{
    /// <summary>Per-task results in plan order.</summary>
    public required IReadOnlyList<TaskResult> Tasks { get; init; }

    /// <summary>True when the run was cancelled (Ctrl+C) before quiescence.</summary>
    public bool Cancelled { get; init; }

    /// <summary>True when every task is green (succeeded this run or skipped as already-succeeded).</summary>
    public bool AllSucceeded => Tasks.All(t => t.IsGreen);

    /// <summary>True when at least one task failed or was blocked.</summary>
    public bool AnyFailed => Tasks.Any(t => !t.IsGreen);

    /// <summary>
    /// The outcome of the end-of-run merge-on-success delivery (plan 08 SSOT §5.3).
    /// Null when <c>mergeOnSuccess</c> is false or the run was not wholly green.
    /// Implemented by task 22.
    /// </summary>
    public MergeOnSuccessResult? MergeOnSuccessOutcome { get; init; }
}
