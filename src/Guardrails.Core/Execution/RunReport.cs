namespace Guardrails.Core.Execution;

/// <summary>The result of running a single guardrail.</summary>
public sealed record GuardrailResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }

    /// <summary>One-line actionable reason on failure (the guardrail's stdout), else null.</summary>
    public string? Reason { get; init; }
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

    public bool Succeeded => Outcome == TaskOutcome.Succeeded;
}

/// <summary>The aggregate result of an entire serial run.</summary>
public sealed record RunReport
{
    /// <summary>Per-task results in execution order.</summary>
    public required IReadOnlyList<TaskResult> Tasks { get; init; }

    /// <summary>True when every task succeeded.</summary>
    public bool AllSucceeded => Tasks.All(t => t.Succeeded);

    /// <summary>True when at least one task failed or was blocked.</summary>
    public bool AnyFailed => Tasks.Any(t => !t.Succeeded);
}
