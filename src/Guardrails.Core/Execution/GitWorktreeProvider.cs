using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// Real git worktree lifecycle for plan 08 M2. Creates and manages git worktrees for
/// parallel plan execution using actual git operations.
/// </summary>
/// <remarks>
/// One instance per run. <see cref="CreateIntegration"/> must be called before any
/// topology methods that need the run id (<see cref="ForkFromTip"/>).
/// </remarks>
public sealed class GitWorktreeProvider : IWorktreeProvider
{
    private readonly string _repoPath;
    private readonly string _worktreeRoot;
    private IntegrationHandle? _integration;

    public GitWorktreeProvider(string repoPath, string worktreeRoot)
    {
        _repoPath = repoPath;
        _worktreeRoot = worktreeRoot;
    }

    /// <inheritdoc />
    public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct)
    {
        string originalBranch = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        string originalHead = Git("rev-parse", "HEAD").Trim();
        string planBranch = $"guardrails/{planName}";

        // Create plan branch off user's current HEAD without switching to it.
        Git("branch", planBranch);

        // Create the integration worktree checked out on the plan branch.
        string integPath = Path.Combine(_worktreeRoot, runId, "_integration");
        Directory.CreateDirectory(Path.GetDirectoryName(integPath)!);
        Git("worktree", "add", integPath, planBranch);

        _integration = new IntegrationHandle
        {
            IntegrationWorktreePath = integPath,
            PlanBranchName = planBranch,
            OriginalBranch = originalBranch,
            OriginalHeadSha = originalHead,
            RunId = runId
        };
        return _integration;
    }

    /// <inheritdoc />
    public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct)
    {
        string planHead = Git("rev-parse", integ.PlanBranchName).Trim();
        string segBranch = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}";
        string segPath = Path.Combine(_worktreeRoot, integ.RunId, taskId, $"attempt-{attempt}");
        Directory.CreateDirectory(Path.GetDirectoryName(segPath)!);
        Git("worktree", "add", "-b", segBranch, segPath, planHead);

        return new WorktreeHandle
        {
            WorktreePath = segPath,
            SegmentBranchName = segBranch,
            TaskBase = planHead,
            RecordedCommitSha = "",
            PlanBranchHead = planHead
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// No git operations — the same physical worktree is reused. The new handle's
    /// <c>TaskBase</c> is set to the upstream's <c>RecordedCommitSha</c> so a retry
    /// can reset to just before this task's WIP (W-2 invariant).
    /// </remarks>
    public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
        new()
        {
            WorktreePath = upstreamSegment.WorktreePath,
            SegmentBranchName = upstreamSegment.SegmentBranchName,
            TaskBase = upstreamSegment.RecordedCommitSha,
            RecordedCommitSha = upstreamSegment.RecordedCommitSha,
            PlanBranchHead = upstreamSegment.PlanBranchHead
        };

    /// <inheritdoc />
    /// <remarks>
    /// W-2 gate: forks from the producer's RECORDED commit sha, not the live tip of the
    /// segment branch (which a linear inherit-one successor may have already advanced).
    /// </remarks>
    public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt)
    {
        string runId = _integration?.RunId
            ?? throw new InvalidOperationException("CreateIntegration must be called before ForkFromTip");
        string forkBranch = $"guardrails/{runId}/fork/{taskId}/attempt-{attempt}";
        string forkPath = Path.Combine(_worktreeRoot, runId, "fork", taskId, $"attempt-{attempt}");
        Directory.CreateDirectory(Path.GetDirectoryName(forkPath)!);
        Git("worktree", "add", "-b", forkBranch, forkPath, producerRecordedSha);

        return new WorktreeHandle
        {
            WorktreePath = forkPath,
            SegmentBranchName = forkBranch,
            TaskBase = producerRecordedSha,
            RecordedCommitSha = producerRecordedSha,
            PlanBranchHead = producerRecordedSha
        };
    }

    /// <inheritdoc />
    public FanInHandle CreateFanIn(
        WorktreeHandle chosenUpstream,
        IReadOnlyList<WorktreeHandle> others,
        string taskId,
        int attempt,
        CancellationToken ct)
    {
        // M5 – merge logic is not yet implemented.
        throw new NotImplementedException("Fan-in worktree creation is implemented in M5.");
    }

    /// <inheritdoc />
    public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
    {
        // M4 – FF/merge logic is not yet implemented.
        throw new NotImplementedException("Integration merge is implemented in M4.");
    }

    /// <inheritdoc />
    public void Discard(WorktreeHandle handle)
    {
        Git("worktree", "remove", "--force", handle.WorktreePath);
    }

    /// <inheritdoc />
    public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ)
    {
        Git("worktree", "prune");
    }

    /// <inheritdoc />
    public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
    {
        // M5 – not yet implemented.
        throw new NotImplementedException("MergePlanBranchIntoUserBranch is implemented in M5.");
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {_repoPath}) exited {proc.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}
