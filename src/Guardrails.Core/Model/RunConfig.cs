namespace Guardrails.Core.Model;

/// <summary>
/// The plan's run configuration — the deserialized <c>guardrails.json</c> (SSOT §2),
/// with documented defaults applied. Only the fields M2 exercises are modelled
/// concretely; <see cref="Interpreters"/> and <see cref="PromptRunnerNames"/> carry
/// enough to validate and run a script-only plan.
/// </summary>
public sealed record RunConfig
{
    /// <summary>Schema version of <c>guardrails.json</c> (required field; SSOT §2).</summary>
    public required int Version { get; init; }

    /// <summary>Worker count for the scheduler. Default 4. (Parallelism is M4; serial in M2.)</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Retries after the first attempt. Default 2. (Retry is M4; not honored in M2.)</summary>
    public int DefaultRetries { get; init; } = 2;

    /// <summary>
    /// Optional per-run cost ceiling in USD (SSOT §2). When set, the scheduler stops launching
    /// new attempts once the journal's cumulative cost reaches or exceeds it, settling the
    /// remaining work <c>needs-human</c> ("cost cap reached"). Null (the default) means no cap —
    /// today's behavior, unchanged. A present non-positive value is a validation error
    /// (<see cref="Loading.DiagnosticCodes.CostCapNonPositive"/>).
    /// </summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>Per-attempt timeout ceiling when nothing narrower applies. Default 1800s.</summary>
    public int DefaultTimeoutSeconds { get; init; } = 1800;

    /// <summary>How guardrail failures are handled within an attempt. Default <see cref="GuardrailMode.FailFast"/>.</summary>
    public GuardrailMode GuardrailMode { get; init; } = GuardrailMode.FailFast;

    /// <summary>cwd for all child processes, relative to the plan dir. Default "..".</summary>
    public string Workspace { get; init; } = "..";

    /// <summary>Interpreter overrides/extensions from <c>guardrails.json</c> (SSOT §5.2). Keyed by extension (".ps1").</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Interpreters { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Names declared under <c>promptRunners</c> (excluding the "default" pointer).
    /// Used to validate runner references on tasks. Kept consistent with
    /// <see cref="PromptRunners"/> (it is exactly its key set).
    /// </summary>
    public IReadOnlySet<string> PromptRunnerNames { get; init; } = new HashSet<string>();

    /// <summary>The value of <c>promptRunners.default</c>, if present.</summary>
    public string? DefaultPromptRunner { get; init; }

    /// <summary>
    /// The full prompt-runner configurations (SSOT §2/§9), keyed by runner name. Empty for a
    /// script-only plan; a plan with prompt tasks but an empty map is a validation error
    /// (GR2008). The key set equals <see cref="PromptRunnerNames"/>.
    /// </summary>
    public IReadOnlyDictionary<string, PromptRunnerConfig> PromptRunners { get; init; } =
        new Dictionary<string, PromptRunnerConfig>();

    /// <summary>
    /// Workspace-relative glob patterns excluded from scope-enforcement diffing (§5.2 of Plan 05).
    /// Files matching these are never tracked in the snapshot or reported as violations.
    /// Default: <c>state/</c>, <c>.git/</c>, <c>**/bin/**</c>, <c>**/obj/**</c>, <c>**/node_modules/**</c>.
    /// </summary>
    public IReadOnlyList<string> EnforcementIgnore { get; init; } =
        ["state/", ".git/", "**/bin/**", "**/obj/**", "**/node_modules/**"];
}
