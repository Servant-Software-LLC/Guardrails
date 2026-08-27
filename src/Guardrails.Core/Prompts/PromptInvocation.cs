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

    /// <summary>
    /// Kill the session when it has produced NO stream output for this long (issue #504). Null disables
    /// stall detection, which is the default for ordinary task attempts — they already have a meaningful
    /// wall clock, and their retry semantics differ.
    ///
    /// <para>This bounds SILENCE where <see cref="Timeout"/> bounds DURATION, and the two are not
    /// interchangeable. A caller that sets a stall bound should set <see cref="Timeout"/> to a generous
    /// BACKSTOP rather than a working ceiling: the point is to let a session that keeps progressing run
    /// as long as it needs, while still guaranteeing no silent state outlives the bound.</para>
    /// </summary>
    public TimeSpan? StallBound { get; init; }

    /// <summary>Absolute path the raw runner output stream is teed to (<c>claude-stream.jsonl</c>).</summary>
    public required string StreamLogPath { get; init; }

    /// <summary>
    /// Absolute path for the rendered, human/agent-readable transcript (<c>transcript.md</c>,
    /// issue #27) — the CLI-equivalent view derived deterministically from the raw stream.
    /// Null disables transcript rendering (e.g. a runner whose output is not a Claude stream).
    /// </summary>
    public string? TranscriptLogPath { get; init; }

    /// <summary>
    /// Fail-fast bound for a prompt whose every tool call is being REFUSED (issue #452): abort the run
    /// once this many permission denials arrive with no successful tool call in between. Null (the
    /// default, and every task action / guardrail) keeps the shipped behavior — grind to the turn cap.
    ///
    /// <para>A runner-agnostic POLICY the harness declares; DETECTION of a denial is the runner's own
    /// vendor-quarantined business (SSOT §9 — <see cref="ClaudePermissionScanner"/> for Claude), so no
    /// caller ever matches a denial string. Set for the harness's own supervisory prompts (the #269
    /// overwatcher's diagnose and the §9.2.1 terminal triage), which have a narrow read-only tool profile
    /// and nobody to approve an interactive prompt: for them a run of consecutive denials means the
    /// remaining turns are provably wasted, and the evidence in #452 is exactly that — 11 turns and
    /// $0.66 spent entirely re-trying blocked reads, terminating with no verdict.</para>
    /// </summary>
    public int? AbortAfterConsecutiveToolDenials { get; init; }
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
    /// Token volume reported by the runner; null when unknown. The tokens axis (DoR §12.4 /
    /// #230-lite) sits beside <see cref="CostUsd"/> because a costless provider honestly reports
    /// <c>0</c> spend — volume is then the only evidence of what the attempt did. Null, never
    /// <c>{ 0, 0 }</c>: a zeroed record is a CLAIM that nothing was consumed.
    /// </summary>
    public PromptUsage? Usage { get; init; }

    /// <summary>
    /// The model the runner OBSERVED itself running on (#349) — mined from the runner's own output
    /// stream, never from the model the harness requested. Null when the runner echoed none.
    /// <para>Runner-agnostic, so no caller reads a Claude-shaped field: the CLI quarantine
    /// (<see cref="ClaudePromptRunner"/>, SSOT §9) restates <see cref="ClaudeResult.Model"/> here, where
    /// the quarantine ends — exactly as <see cref="Usage"/> restates <c>ClaudeUsage</c>.</para>
    /// <para>The OBSERVED model is a different fact from the REQUESTED one recorded as
    /// <c>AttemptProvenance.Model</c> (the resolved route, or the <c>"(cli default)"</c> sentinel when
    /// nothing named a model), and it is the stronger one: the harness never forces <c>--model</c> to
    /// obtain it, because doing so would pin the zero-setup user who deliberately passes nothing.</para>
    /// </summary>
    public string? ObservedModel { get; init; }

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
/// Runner-agnostic token volume for one prompt run (DoR §12.4): the
/// <c>{ InputTokens, OutputTokens }</c> pair the per-tier spend line (#230-lite) aggregates
/// alongside cost, journalled as <c>AttemptRecord.Usage</c>. A straight CARRY of what the runner
/// reported — the harness never recomputes or defaults it.
/// </summary>
public sealed record PromptUsage
{
    /// <summary>Total input (prompt) tokens the run consumed, cache reads and writes included.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output (completion) tokens the run produced.</summary>
    public int OutputTokens { get; init; }
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
