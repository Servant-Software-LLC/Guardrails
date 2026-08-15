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
    /// present it must be a positive integer (GR2036); a long-running guardrail's progress heartbeat
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

    /// <summary>
    /// Optional difficulty tier from a PROMPT guardrail's frontmatter (SSOT §4.2, issue #225) —
    /// <c>easy</c>|<c>medium</c>|<c>hard</c>, joining <c>runner</c>/<c>maxTurns</c> as a frontmatter key.
    /// This is the judge-guardrail half of tier tagging: a judge resolves its own route, so it needs its
    /// own site to pin one. Bound VERBATIM (no trim, no case-fold) so an unrecognised value reaches the
    /// validator's GR2043 check as written, the same doctrine <c>action.tier</c> follows. Null = the key
    /// was absent, there is no frontmatter, or the guardrail is deterministic (a script guardrail runs no
    /// model and can never carry one). Nothing ROUTES on it yet — the resolver is Stage 2 (#226).
    /// </summary>
    public string? Tier { get; init; }
}
