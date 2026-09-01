// The ONE defect 02-captures-are-bodiless-autoproperties.ps1 exists to catch, and it is the form that
// actually defeated draft 2 of section 9: an EXPRESSION-BODIED capture. Every write site can then read
// ".DefinitionHashAtLoad" verbatim and the hash is still computed from CURRENT DISK - the defect fully
// intact, one call frame further away. Identical to the .valid half apart from those two members.
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

    public string DefinitionHashAtLoad => Journal.TaskDefinitionHash.Compute(this);

    public IReadOnlyDictionary<string, string> DefinitionFilesAtLoad =>
        Journal.TaskDefinitionFiles.Enumerate(this).ToDictionary(f => f.Label, f => Hashing.HashText.OfFile(f.AbsolutePath));
}
