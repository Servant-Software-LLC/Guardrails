namespace Guardrails.Core.Journal;
public enum TierSource
{
    Task,
    PlanDefault,
    Override,
    Escalated
}
public sealed record AttemptProvenance
{
    /// <summary>The rung the first resolution served: EscalatedFrom.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EscalatedFrom { get; init; }
}
