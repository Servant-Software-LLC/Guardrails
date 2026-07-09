namespace Guardrails.Core.Execution;

/// <summary>
/// The authority class the mechanical asymmetry (SSOT §9.2, doc 11 §3) assigns to a proposed
/// overwatcher fix operation. The class is decided DETERMINISTICALLY by the harness
/// (<see cref="OverwatchFixClassifier"/>), never by the judge — that is what makes "self-healing must
/// never soften a deterministic guardrail's verdict" a mechanical guarantee rather than a vibe.
/// </summary>
public enum OverwatchAuthorityClass
{
    /// <summary>
    /// The action/budget layer: ephemeral guidance injection + runtime <c>maxTurns</c>/<c>retries</c>/
    /// <c>timeoutSeconds</c> overrides. Touches no authored file, no <c>PlanDefinitionHash</c>, no review
    /// marker. In v1 these are PROPOSED (prompt tier); silent auto-apply is the v2 <c>auto</c> bet.
    /// </summary>
    Allowlist,

    /// <summary>
    /// The verdict surface — FORBIDDEN to auto-apply at EVERY tier, including <c>auto</c>: any guardrail/
    /// preflight body (the four folders) and the <c>task.json</c> verdict-driving fields
    /// (<c>writeScope</c>, <c>scope</c>, <c>dependsOn</c>, <c>integrationGate</c>). A denylist op may only
    /// be emitted as a proposal requiring human approval + a <c>/guardrails-review</c> re-run; applying it
    /// changes <c>PlanDefinitionHash</c> and RE-STALES the review marker (#260, the trust anchor).
    /// </summary>
    Denylist,

    /// <summary>
    /// Anything not on either list → propose-only (closed allowlist, fail-safe): it is impossible to
    /// auto-apply an unclassified operation.
    /// </summary>
    Default
}

/// <summary>The kind of fix the overwatcher's diagnose prompt proposed (doc 11 §3.1/§3.2).</summary>
public enum OverwatchFixKind
{
    /// <summary>Ephemeral guidance appended to the NEXT attempt's composed prompt (allowlist; the safest v1 lever).</summary>
    GuidanceInjection,

    /// <summary>A runtime budget override — <c>maxTurns</c>/<c>retries</c>/<c>timeoutSeconds</c> (allowlist when the field is one of those).</summary>
    BudgetOverride,

    /// <summary>An edit to an authored file (classified by path — a guardrail/preflight body is denylist; anything else is default).</summary>
    FileEdit,

    /// <summary>An edit to a <c>task.json</c> field (classified by field — a verdict-driving field is denylist; anything else is default).</summary>
    TaskFieldEdit
}

/// <summary>
/// One typed fix operation the overwatcher's diagnose prompt proposed. The judge PROPOSES these; the
/// harness CLASSIFIES each (<see cref="OverwatchFixClassifier"/>) and, in v1, applies only the
/// allowlist members (guidance / budget), and only when the tier + interaction sanction it. A malformed
/// op is simply dropped (advisory-never-gates).
/// </summary>
public sealed record OverwatchFixOp
{
    /// <summary>The kind of fix.</summary>
    public required OverwatchFixKind Kind { get; init; }

    /// <summary>For <see cref="OverwatchFixKind.GuidanceInjection"/>: the guidance text to append to the next attempt.</summary>
    public string? Guidance { get; init; }

    /// <summary>For <see cref="OverwatchFixKind.BudgetOverride"/>: the budget field name (<c>maxTurns</c>/<c>retries</c>/<c>timeoutSeconds</c>).</summary>
    public string? BudgetField { get; init; }

    /// <summary>For <see cref="OverwatchFixKind.BudgetOverride"/>: the requested value (bounded by the hard caps at application).</summary>
    public int? BudgetValue { get; init; }

    /// <summary>For <see cref="OverwatchFixKind.FileEdit"/>: the target path (workspace/plan-relative or absolute) the judge would edit.</summary>
    public string? TargetPath { get; init; }

    /// <summary>For <see cref="OverwatchFixKind.TaskFieldEdit"/>: the <c>task.json</c> field the judge would edit.</summary>
    public string? TaskField { get; init; }
}
