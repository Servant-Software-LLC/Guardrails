// A COMPLETE, representative CORRECT artifact for 02-captures-are-bodiless-autoproperties.ps1
// (#468/#302): the model record after stage 3, carrying both load-time captures as bodiless, nullable,
// init-only auto-properties and naming no hash function at all. Kept complete rather than a fragment -
// an incomplete valid sample fails for a DIFFERENT reason and masks the real one.
//
// This header deliberately quotes none of the tokens the guardrail bans (taxonomy 13).
using System.Collections.Generic;

namespace Guardrails.Core.Model;

public sealed record TaskNode
{
    public required string Id { get; init; }

    public string? WaveDir { get; init; }

    public string? StableId { get; init; }

    public required string Directory { get; init; }

    public required string Description { get; init; }

    public required IReadOnlyList<string> DependsOn { get; init; }

    public int? Retries { get; init; }

    public int? TimeoutSeconds { get; init; }

    public required ActionDefinition Action { get; init; }

    public required IReadOnlyList<GuardrailDefinition> Guardrails { get; init; }

    public required IReadOnlyList<GuardrailDefinition> Preflights { get; init; }

    public bool IntegrationGate { get; init; }

    public IReadOnlyList<string>? WriteScope { get; init; }

    public IReadOnlyList<StagingOutput>? StagingOutputs { get; init; }

    /// <summary>
    /// The FULL-surface definition hash of the bytes the loader read, captured eagerly at construction.
    /// This is the value every write site stamps; it is never recomputed. Nullable rather than required:
    /// the loader is the only production constructor, and a null pin records a null hash with no
    /// fallback to disk at any site.
    /// </summary>
    public string? DefinitionHashAtLoad { get; init; }

    /// <summary>
    /// The per-file definition surface as of load, keyed by the enumeration's own label. The settle-time
    /// divergence gate diffs this against a fresh walk, which is how it can name WHICH files moved.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DefinitionFilesAtLoad { get; init; }
}
