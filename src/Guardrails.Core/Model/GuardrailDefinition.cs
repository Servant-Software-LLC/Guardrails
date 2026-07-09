namespace Guardrails.Core.Model;

/// <summary>
/// A resolved guardrail for a task. SSOT §4. Guardrails are executed in filename
/// sort order; every one must pass. Deterministic guardrails may carry an optional
/// metadata sidecar (SSOT §4.1).
/// </summary>
public sealed record GuardrailDefinition
{
    /// <summary>Guardrail name = the file's basename without extension (e.g. "01-build-passes").</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the guardrail file on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Whether this guardrail runs as a script/executable or an LLM prompt verdict.</summary>
    public required ActionKind Kind { get; init; }

    /// <summary>Human description from the sidecar (deterministic) — may be null.</summary>
    public string? Description { get; init; }

    /// <summary>Arguments from the metadata sidecar (deterministic guardrails only).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Per-guardrail timeout ceiling in seconds; null = inherit from task/config.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Optional author-set expected wall-clock duration in seconds (SSOT §4.1, issue #331). When
    /// present it must be a positive integer (GR2035); a long-running guardrail's progress heartbeat
    /// surfaces it as an "expected ~Xm" hint next to the elapsed time, and flags "over budget" once
    /// elapsed exceeds it by a multiple — turning "is this hung or just slow?" into a glance. Null =
    /// no hint (the heartbeat shows elapsed only). Read-only metadata: it never bounds execution
    /// (that is <see cref="TimeoutSeconds"/>).
    /// </summary>
    public int? ExpectedDurationSeconds { get; init; }

    /// <summary>
    /// Optional scope tag from the guardrail metadata sidecar (plan 08 M2, SSOT §4.3).
    /// The only value currently meaningful to the harness is <c>"integration"</c>, which marks
    /// this guardrail as the whole-repo soundness check run at an <c>integrationGate</c> sink.
    /// Null when the sidecar omits the field or there is no sidecar.
    /// </summary>
    public string? Scope { get; init; }
}
