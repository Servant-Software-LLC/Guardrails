using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Public <see cref="IAiMergeWorker"/> implementation. Delegates to <see cref="AiMergeResolver"/>
/// which owns the full three-way env contract + deterministic gates.
/// </summary>
public sealed class AiMergeWorker : IAiMergeWorker
{
    private readonly AiMergeResolver _resolver;

    public AiMergeWorker(IPromptRunner runner) => _resolver = new AiMergeResolver(runner);

    /// <inheritdoc/>
    public Task<bool> TryResolveAsync(
        string worktreePath,
        string segmentBranch,
        string planDirectory,
        ISchedulerJournal journal,
        CancellationToken ct)
        => _resolver.TryResolveAsync(worktreePath, segmentBranch, planDirectory, journal, ct);
}
