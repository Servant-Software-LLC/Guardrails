namespace Guardrails.Core.Model;

/// <summary>
/// A single task in the plan DAG. SSOT §3. The task id is its folder name.
/// </summary>
public sealed record TaskNode
{
    /// <summary>
    /// The task's canonical id. In a FLAT plan this is the task folder name (kebab-case, e.g.
    /// "01-write-greeting-script"). In a WAVED plan (SSOT §14.2) it is the WAVE-QUALIFIED id
    /// <c>&lt;waveDir&gt;/&lt;taskFolder&gt;</c> (e.g. "wave-02-provision/01-author-tests"), so two waves
    /// may each reuse <c>01-</c> numbering without colliding. This id is the journal <c>tasks{}</c> key,
    /// the <c>Guardrails-Task:</c> trailer value, AND the §6.2 single-writer state-fragment key — a
    /// fragment's top-level key must equal this exact id (a bare, non-qualified key from another wave is
    /// rejected as foreign, #164/§14.2).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The wave directory this task belongs to in a WAVED plan (e.g. "wave-02-provision"), or null for a
    /// FLAT plan (SSOT §14.2). When non-null, <see cref="Id"/> is <c>{WaveDir}/{folderName}</c>. Used to
    /// scope <c>dependsOn</c> to the wave (GR2034) and to give the GR2022 cross-wave state-read lint its
    /// wave-aware branch (earlier-wave read satisfied by the barrier; later-wave read is an error).
    /// </summary>
    public string? WaveDir { get; init; }

    /// <summary>
    /// Optional stable identity that survives renumbering/slug edits across regenerations
    /// (SSOT §11 / issue #5). Null when the plan does not declare one. Reserved for the
    /// regeneration merge; not yet consumed at runtime.
    /// </summary>
    public string? StableId { get; init; }

    /// <summary>Absolute path to the task folder.</summary>
    public required string Directory { get; init; }

    /// <summary>Required one-line human description (used in feedback and reporting).</summary>
    public required string Description { get; init; }

    /// <summary>Task ids this task depends on. May be empty; never null.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Retry override; null = use <c>defaultRetries</c>. (Retries are M4; carried for fidelity.)</summary>
    public int? Retries { get; init; }

    /// <summary>Whole-attempt timeout ceiling in seconds; null = use <c>defaultTimeoutSeconds</c>.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The resolved action for this task.</summary>
    public required ActionDefinition Action { get; init; }

    /// <summary>The resolved guardrails, in filename sort order. At least one (validated).</summary>
    public required IReadOnlyList<GuardrailDefinition> Guardrails { get; init; }

    /// <summary>
    /// Task-level JIT dependency-delivery preflights parsed from <c>tasks/&lt;id&gt;/preflights/</c>
    /// (design-of-record 09-preflight-first-class, SSOT §1/§4) — the sibling of the postcondition
    /// <see cref="Guardrails"/> folder, evaluated in the task's own segment worktree at <c>taskBase</c>
    /// BEFORE the attempt loop, to verify a producer actually delivered in the bytes this consumer
    /// inherited. Guardrail-shaped files (same shape and parser as <see cref="Guardrails"/>, opening with
    /// a <c>catches:</c> declaration). Empty when the folder is absent (the common case). The per-task
    /// preflight slot that runs these lands in a later deliverable.
    /// </summary>
    public IReadOnlyList<GuardrailDefinition> Preflights { get; init; } = [];

    /// <summary>
    /// When true, this task is the terminal integration gate for the plan (plan 08 M2, SSOT §3.3).
    /// A multi-leaf or fan-in plan must have exactly one such sink, which must carry at least one
    /// guardrail with <c>scope:"integration"</c>. Default false (no gate role).
    /// </summary>
    public bool IntegrationGate { get; init; }

    /// <summary>
    /// The declared write-scope for this task (plan 08 §2/§3.4, SSOT §3.4). Each entry is a
    /// glob pattern matched against <c>git diff --name-status</c> paths after the action runs.
    /// Null (absent in <c>task.json</c>) is the off-switch — no write-scope check runs.
    /// </summary>
    public IReadOnlyList<string>? WriteScope { get; init; }

    /// <summary>
    /// The declared staging outputs for autonomous <c>.claude/</c> delivery (SSOT §3.5, issue #130).
    /// Each entry maps a <c>from</c> path/glob (relative to <c>GUARDRAILS_STAGING_DIR</c>, where the
    /// action writes its deliverable) to a <c>to</c> destination under <c>.claude/</c> (where the
    /// harness moves it after the action succeeds and before guardrails run). Null (absent in
    /// <c>task.json</c>) means no staging — the unchanged default behavior. A present-but-malformed
    /// list is a validation error (<see cref="Loading.DiagnosticCodes.StagingOutputsInvalid"/>).
    /// </summary>
    public IReadOnlyList<StagingOutput>? StagingOutputs { get; init; }
}

/// <summary>
/// One <c>stagingOutputs[]</c> mapping (SSOT §3.5): the action writes its deliverable to
/// <see cref="From"/> (relative to the per-task staging root <c>GUARDRAILS_STAGING_DIR</c>), and the
/// harness moves it to <see cref="To"/> (a workspace-relative path under <c>.claude/</c>) after the
/// action succeeds and before guardrails run.
/// </summary>
public sealed record StagingOutput
{
    /// <summary>The source path or glob relative to the per-task staging root.</summary>
    public required string From { get; init; }

    /// <summary>The workspace-relative destination under <c>.claude/</c>.</summary>
    public required string To { get; init; }
}
