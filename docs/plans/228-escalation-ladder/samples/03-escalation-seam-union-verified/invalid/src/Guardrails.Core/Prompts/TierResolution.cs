namespace Guardrails.Core.Prompts;
public sealed record TierResolution
{
    public bool Climbed { get; init; }
    // TODO: EscalatedFrom went missing in the union
}
