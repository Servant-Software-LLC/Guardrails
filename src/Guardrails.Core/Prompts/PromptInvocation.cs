using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Everything a prompt runner needs to execute one prompt (action or guardrail), assembled
/// by the harness (SSOT §9). The composed prompt text is delivered via STDIN; the working
/// directory is the workspace; the plan dir is granted via <c>--add-dir</c>; the §5.1 env
/// set is injected; settings are the effective per-task/guardrail-resolved knobs; and the
/// raw runner stream is teed to <see cref="StreamLogPath"/> (SSOT §8 <c>claude-stream.jsonl</c>).
/// </summary>
public sealed record PromptInvocation
{
    /// <summary>The fully composed prompt text (body + appended harness sections), delivered via stdin.</summary>
    public required string ComposedPrompt { get; init; }

    /// <summary>cwd for the runner process — the resolved workspace.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The plan folder root — granted to the runner via <c>--add-dir</c> so it can reach state/verdict paths.</summary>
    public required string PlanDirectory { get; init; }

    /// <summary>The §5.1 environment variables for this prompt process.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>The effective runner settings (config + per-task/guardrail overrides applied).</summary>
    public required PromptRunnerSettings Settings { get; init; }

    /// <summary>The per-attempt timeout for this prompt.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Absolute path the raw runner output stream is teed to (<c>claude-stream.jsonl</c>).</summary>
    public required string StreamLogPath { get; init; }

    /// <summary>
    /// Absolute path for the rendered, human/agent-readable transcript (<c>transcript.md</c>,
    /// issue #27) — the CLI-equivalent view derived deterministically from the raw stream.
    /// Null disables transcript rendering (e.g. a runner whose output is not a Claude stream).
    /// </summary>
    public string? TranscriptLogPath { get; init; }
}

/// <summary>
/// The terminal outcome of a prompt run (SSOT §9). <see cref="Completed"/> is the process
/// disposition (the runner produced a terminal result and exited cleanly); <see cref="IsError"/>
/// is the runner's own report of whether the agent succeeded — for an ACTION, semantic
/// success = <c>Completed &amp;&amp; !IsError</c> (guardrail success is judged by the verdict file).
/// </summary>
public sealed record PromptResult
{
    /// <summary>True when the runner produced a terminal result and exited without error.</summary>
    public required bool Completed { get; init; }

    /// <summary>The terminal result's <c>is_error</c> flag (true = the agent reported an error).</summary>
    public required bool IsError { get; init; }

    /// <summary>The terminal result text (the agent's final message), if any.</summary>
    public string? ResultText { get; init; }

    /// <summary>Total cost in USD reported by the runner; null when unknown.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Number of agent turns reported by the runner; null when unknown.</summary>
    public int? NumTurns { get; init; }

    /// <summary>
    /// The runner-agnostic classification of a non-success outcome (SSOT §9, issues #114/#115/#119).
    /// <see cref="PromptFailureKind.None"/> on success. The CLI quarantine
    /// (<see cref="ClaudePromptRunner"/>) computes this; the harness routes on it without ever
    /// inspecting a Claude-specific string. <see cref="PromptFailureKind.Transient"/> is the only
    /// value that suppresses retry-budget consumption (the harness backs off and re-runs).
    /// </summary>
    public PromptFailureKind FailureKind { get; init; } = PromptFailureKind.None;

    /// <summary>
    /// An advisory, operator-facing reset hint extracted from a rate-limit message
    /// (e.g. <c>"11:20am"</c>), surfaced in the pause notice. Null when none was present. Never
    /// parsed into a sleep duration (timezone/day ambiguity) — display only.
    /// </summary>
    public string? ResetHint { get; init; }

    /// <summary>A short human-readable summary of the outcome (for logs and feedback).</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// The distinct file paths the runtime REFUSED to write/edit this run because the path is not on
    /// the granted permission allow-list (issues #86 / #104), in first-seen order. Empty when no
    /// permission wall was hit. Runner-agnostic: the CLI quarantine
    /// (<see cref="ClaudePermissionScanner"/>) mines these from the runner's tool-result events; the
    /// harness (<c>TaskExecutor</c> via <c>PermissionWallTracker</c>) routes on the list of paths only,
    /// never on a vendor-specific denial string. A repeated wall on the SAME path (or any wall on a
    /// <c>.claude/</c> path, a known-structural runtime restriction) settles the task
    /// <c>needs-human</c> immediately instead of burning the remaining retries.
    /// </summary>
    public IReadOnlyList<string> BlockedWritePaths { get; init; } = [];
}

/// <summary>
/// The pluggability seam (SSOT §9): one implementation per CLI quarantines all
/// flag-spelling and output-parsing specifics. The harness composes a
/// <see cref="PromptInvocation"/> and calls <see cref="RunAsync"/>; it never knows which
/// CLI ran or how its output was shaped.
/// </summary>
public interface IPromptRunner
{
    /// <summary>The runner's name (matches the <c>promptRunners</c> map key).</summary>
    string Name { get; }

    /// <summary>Run the prompt and return its terminal result.</summary>
    Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken);
}
