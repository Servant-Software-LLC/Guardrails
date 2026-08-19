namespace Guardrails.Core.Model;

/// <summary>
/// Which source supplied <see cref="ActionDefinition.Tier"/> (DoR §12.4). The loader resolves the tier by
/// precedence — <c>task.json action.tier</c> &gt; <c>tiering.defaultTier</c> &gt; null — and that collapse
/// destroys the answer to "where did this rung come from?"; this records it instead of losing it.
///
/// <para>It is the INPUT the journal's <c>provenance.tierSource</c> is derived from, not that field itself.
/// <c>tierSource</c>'s third value, <c>"override"</c>, is produced by a full <c>action.runner</c>/
/// <c>action.model</c> pin bypassing resolution entirely (DoR §6.1 item 1, D31) — the resolver decides that
/// one for itself from <see cref="ActionDefinition.Runner"/> / <see cref="ActionDefinition.Model"/>, which
/// are still visible to it. Nothing about a pin is destroyed at load, so nothing about it is recorded here.</para>
///
/// <para>The origin ALWAYS agrees with what is actually in <c>Tier</c>: <see cref="None"/> means no tier was
/// resolved at all and <c>Tier</c> is null — neither site declared one, or the plan-wide default was an
/// unrecognized token the loader refuses to propagate (GR2043 reports that once, at its declaration site).</para>
/// </summary>
public enum TierOrigin
{
    /// <summary>No tier resolved — <see cref="ActionDefinition.Tier"/> is null, and no <c>tierSource</c> is journalled.</summary>
    None = 0,

    /// <summary>The task's own <c>action.tier</c> supplied the rung — journal <c>tierSource: "task"</c>.</summary>
    Task,

    /// <summary>The plan-wide <c>tiering.defaultTier</c> supplied the rung — journal <c>tierSource: "plan-default"</c>.</summary>
    PlanDefault
}

/// <summary>
/// A resolved action for a task — the single action file plus the settings that
/// govern how it runs. SSOT §3 ("action" block). Discovered by convention
/// (one <c>action.*</c> file) or pointed at explicitly via <c>task.json action.path</c>.
/// </summary>
public sealed record ActionDefinition
{
    /// <summary>Absolute path to the action file on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Whether this action runs as a script/executable or an LLM prompt.</summary>
    public required ActionKind Kind { get; init; }

    /// <summary>Arguments for deterministic (script/executable) actions. Empty for prompt actions.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Prompt-runner name for prompt actions; null = use <c>promptRunners.default</c>. M5.</summary>
    public string? Runner { get; init; }

    /// <summary>Turn ceiling override for prompt actions; null = inherit from the runner config / frontmatter.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Model override for prompt actions; null = inherit from the runner config default.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Difficulty tier — <c>easy</c>|<c>medium</c>|<c>hard</c> (SSOT §3, issue #225); null = untagged (no
    /// <c>action.tier</c> declared and no plan-wide default configured). RESOLVED AT LOAD, precedence
    /// <c>task.json action.tier</c> &gt; <c>tiering.defaultTier</c> &gt; null — which is what makes the
    /// plan-wide default reach a task hand-added to the folder after breakdown. Nothing ROUTES on it in
    /// Stage 1; the resolver is Stage 2 (#226).
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// Which of the two load-time sources supplied <see cref="Tier"/> — the provenance the
    /// <c>?? defaultTier</c> collapse above would otherwise destroy (DoR §12.4). <see cref="TierOrigin.None"/>
    /// (the default) whenever <see cref="Tier"/> is null. See <see cref="Model.TierOrigin"/> for why
    /// <c>override</c> is not one of the values.
    /// </summary>
    public TierOrigin TierOrigin { get; init; }

    /// <summary>
    /// Per-task thinking-effort override for prompt actions (SSOT §3, issue #201); null = inherit the
    /// resolved route's effort. Mirrors <see cref="Model"/>'s SHAPE (opaque string, GR2050 shape check)
    /// but deliberately NOT its BYPASS: <c>action.model</c>/<c>action.runner</c> are full pins that skip
    /// tier resolution, whereas <c>action.effort</c> ALONE still lets resolution select the block and
    /// only overrides that route's effort — <c>{ "tier": "medium", "effort": "xhigh" }</c> means "route
    /// by tier, but think hard". Nothing CONSUMES it yet; the Stage 2 resolver (#226) is its first reader.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>Per-action timeout ceiling in seconds; null = inherit from task/config.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Overrides the config workspace as the process cwd. Relative to the plan dir; null = config workspace.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables injected into this action's process.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>();
}
