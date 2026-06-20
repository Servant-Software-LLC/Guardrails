namespace Guardrails.Core.Execution;

/// <summary>
/// Identifies the private worktree used to merge multiple upstream segment branches before a
/// fan-in task runs (plan 08 M5). Created by <see cref="IWorktreeProvider.CreateFanIn"/>.
/// </summary>
public sealed class FanInHandle
{
    /// <summary>Absolute path to the private fan-in worktree.</summary>
    public string PrivateWorktreePath { get; init; } = "";
}
