namespace Guardrails.Core.Model;

/// <summary>
/// A fully loaded plan folder: its run config plus every task. SSOT §1.
/// </summary>
public sealed record PlanDefinition
{
    /// <summary>Absolute path to the plan folder root (contains <c>guardrails.json</c>).</summary>
    public required string PlanDirectory { get; init; }

    /// <summary>The deserialized run configuration with defaults applied.</summary>
    public required RunConfig Config { get; init; }

    /// <summary>
    /// ALL tasks in the plan, in order. For a FLAT plan this is the <c>tasks/</c> folders in folder-name
    /// (ordinal) order. For a WAVED plan (SSOT §14) it is every wave's tasks FLATTENED in strict wave order
    /// then task-folder order, each carrying its wave-qualified <see cref="TaskNode.Id"/> — so every
    /// whole-plan check (unique ids, guardrails present, cross-task state, interpreters) operates over the
    /// whole plan unchanged. Keyed lookups use <see cref="TaskNode.Id"/>.
    /// </summary>
    public required IReadOnlyList<TaskNode> Tasks { get; init; }

    /// <summary>
    /// The plan's waves in strict total order (SSOT §14), or EMPTY for a FLAT plan. When non-empty the plan
    /// is WAVED (<see cref="IsWaved"/>): its tasks live in <c>&lt;plan&gt;/&lt;waveDir&gt;/tasks/</c> and each
    /// wave carries its own entry/exit gates. <see cref="Tasks"/> is the flattened union of every wave's
    /// <see cref="WaveNode.Tasks"/>.
    /// </summary>
    public IReadOnlyList<WaveNode> Waves { get; init; } = [];

    /// <summary>True when this is a WAVED plan (has ≥1 wave). Equivalent to <c>Waves.Count &gt; 0</c>.</summary>
    public bool IsWaved => Waves.Count > 0;

    /// <summary>The absolute resolved workspace (cwd for child processes), from <see cref="RunConfig.Workspace"/>.</summary>
    public required string Workspace { get; init; }

    /// <summary>
    /// Plan-level "Full Flight Check" preflights parsed from <c>&lt;plan&gt;/preflights/</c>
    /// (design-of-record 09-preflight-first-class, SSOT §1/§4) — evaluated ONCE, before the DAG,
    /// against the run's starting bytes. Guardrail-shaped files (the same <see cref="GuardrailDefinition"/>
    /// shape and parser as task guardrails: <c>NN-name.ps1|.sh|.py</c> + optional <c>.json</c> sidecar,
    /// or <c>NN-name.prompt.md</c>, opening with a <c>catches:</c> declaration). Empty when the folder
    /// is absent. The pre-DAG phase that runs these lands in a later deliverable.
    /// </summary>
    public IReadOnlyList<GuardrailDefinition> PlanPreflights { get; init; } = [];

    /// <summary>
    /// Plan-level terminal / integration-gate checks parsed from <c>&lt;plan&gt;/guardrails/</c>
    /// (SSOT §1/§3.3/§4) — evaluated ONCE, at run end, on the merged plan-branch HEAD. This folder
    /// carries the re-homed terminal-gate obligation: a multi-leaf/fan-in plan must hold at least one
    /// check here that actually re-runs the integration set (whole-repo build / full suite / a union
    /// invariant) — the content teeth GR2018 used to enforce on the retired <c>integrationGate</c> task
    /// kind, now checked by <see cref="Loading.DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun"/>.
    /// Empty when the folder is absent. The terminal phase that runs these lands in a later deliverable.
    /// </summary>
    public IReadOnlyList<GuardrailDefinition> PlanGuardrails { get; init; } = [];
}
