namespace Guardrails.Core.Prompts;
public sealed record TierResolution
{
    public bool Climbed { get; init; }
    /// <summary>The rung the FIRST resolution served. NOT Climbed - see EscalatedFrom docs.</summary>
    public string? EscalatedFrom { get; init; }
}
