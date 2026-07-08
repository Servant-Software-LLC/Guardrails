using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler-level wiring tests for the Part C safe-auto-resolve gate (issue #274, SSOT §7.2): with the
/// safe-suffix VERDICT scripted by a fake provider (the pure check is proven exhaustively in
/// <see cref="SafeSuffixEvaluatorTests"/>), assert the policy gate resolves-vs-halts correctly, that a
/// resolve rewinds + journal-resets + re-runs the safe set + records the audit, and — the floor — that an
/// UNSAFE drift halts under EVERY policy. TCS-free: the fake executor returns synchronously.
/// </summary>
public sealed class SchedulerDriftAutoResolveTests
{
    private sealed class DriftFakeJournal : ISchedulerJournal
    {
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _succeeded = new(StringComparer.Ordinal);

        public List<string> ResetToPending { get; } = [];
        public List<DriftResolution> Recorded { get; } = [];

        public void MarkSucceeded(string taskId, string? recordedHash)
        {
            _succeeded.Add(taskId);
            _hashes[taskId] = recordedHash;
        }

        public JournalTaskStatus StatusOf(string taskId) =>
            _succeeded.Contains(taskId) ? JournalTaskStatus.Succeeded : JournalTaskStatus.Pending;

        public string? RecordedDefinitionHash(string taskId) => _hashes.GetValueOrDefault(taskId);

        public void MarkBlocked(string taskId) { }

        public void ResetTaskToPending(string taskId)
        {
            _succeeded.Remove(taskId); // so the post-resolve re-detect sees it as pending (re-runnable)
            ResetToPending.Add(taskId);
        }

        public void RecordDriftResolution(DriftResolution resolution) => Recorded.Add(resolution);
    }

    /// <summary>A fake provider that SCRIPTS the safe-suffix verdict and records the rewind, delegating every topology op to <see cref="FakeWorktreeProvider"/>.</summary>
    private sealed class DriftFakeProvider(SafeSuffixDecision decision) : IWorktreeProvider
    {
        private readonly FakeWorktreeProvider _inner = new();
        public List<string> RewoundTo { get; } = [];

        public SafeSuffixDecision EvaluateSafeSuffix(IntegrationHandle integ, IReadOnlySet<string> safeSet) => decision;
        public void RewindPlanBranchTo(IntegrationHandle integ, string resetTarget) => RewoundTo.Add(resetTarget);

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            _inner.CreateIntegration(planName, runId, ct);
        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            _inner.CreateSegment(taskId, attempt, integ, ct);
        public WorktreeHandle ReuseSegment(WorktreeHandle upstream, string taskId, int attempt) =>
            _inner.ReuseSegment(upstream, taskId, attempt);
        public WorktreeHandle ForkFromTip(string producerSha, string taskId, int attempt) =>
            _inner.ForkFromTip(producerSha, taskId, attempt);
        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
            _inner.Integrate(segment, integ, ct);
        public void Discard(WorktreeHandle handle) => _inner.Discard(handle);
        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
            _inner.PruneOrphans(liveTaskIds, integ);
        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            _inner.MergePlanBranchIntoUserBranch(integ, ct);
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<DriftResolution> DriftResolvedCalls { get; } = [];
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void DriftResolved(DriftResolution resolution) => DriftResolvedCalls.Add(resolution);
    }

    private static PlanDefinition DriftedLinearPlan(DriftPolicy policy, out TaskNode a, out TaskNode b)
    {
        a = Task("01-a");
        b = Task("02-b", "01-a");
        return Plan(a, b) with { Config = new RunConfig { Version = 1, DriftPolicy = policy } };
    }

    private static DriftFakeJournal JournalWithDriftOn01(TaskNode a, TaskNode b)
    {
        var journal = new DriftFakeJournal();
        journal.MarkSucceeded("01-a", "sha256:stale-does-not-match"); // drifted
        journal.MarkSucceeded("02-b", Guardrails.Core.Journal.TaskDefinitionHash.Compute(b)); // unchanged descendant
        return journal;
    }

    private static async Task<(RunReport Report, RecordingExecutor Exec, DriftFakeProvider Prov, DriftFakeJournal Jnl, RecordingObserver Obs)>
        RunAsync(PlanDefinition plan, DriftFakeJournal journal, SafeSuffixDecision decision, bool preConfirmed)
    {
        var exec = new RecordingExecutor();
        var prov = new DriftFakeProvider(decision);
        var obs = new RecordingObserver();
        var scheduler = new Scheduler(
            plan, exec, journal, worktreeProvider: prov, observer: obs, maxParallelism: 2, driftPreConfirmed: preConfirmed);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (report, exec, prov, journal, obs);
    }

    [Fact]
    public async Task Reprocess_SafeDrift_Rewinds_ReRunsSafeSet_RecordsResolution()
    {
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Reprocess, out TaskNode a, out TaskNode b);
        DriftFakeJournal journal = JournalWithDriftOn01(a, b);

        var (report, exec, prov, jnl, obs) = await RunAsync(
            plan, journal, SafeSuffixDecision.Safe("resetsha", removedCommitCount: 2), preConfirmed: false);

        Assert.Null(report.DefinitionDrift);                 // resolved, not halted
        Assert.True(report.AllSucceeded);
        Assert.Equal(["resetsha"], prov.RewoundTo);          // DESTRUCTIVE rewind performed
        Assert.Equal(["01-a", "02-b"], jnl.ResetToPending.OrderBy(x => x)); // whole safe set journal-reset
        Assert.Contains("01-a", exec.Started);               // re-ran from the clean base
        Assert.Contains("02-b", exec.Started);

        DriftResolution resolution = Assert.Single(jnl.Recorded);
        Assert.Equal("reprocess", resolution.Trigger);
        Assert.Equal("resetsha", resolution.RewindTarget);
        Assert.Equal(["01-a", "02-b"], resolution.Tasks.Select(t => t.TaskId).OrderBy(x => x));
        Assert.NotNull(report.DriftResolution);
        DriftResolution observed = Assert.Single(obs.DriftResolvedCalls); // surfaced live
        Assert.Equal("reprocess", observed.Trigger);
    }

    [Fact]
    public async Task Halt_SafeDrift_AlwaysHalts_NoRewind()
    {
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Halt, out TaskNode a, out TaskNode b);

        var (report, exec, prov, jnl, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), SafeSuffixDecision.Safe("resetsha", 2), preConfirmed: false);

        Assert.NotNull(report.DefinitionDrift);              // strict opt-out: Part A halt preserved
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
        Assert.Empty(jnl.ResetToPending);
        Assert.Empty(jnl.Recorded);
    }

    [Fact]
    public async Task Prompt_NotPreConfirmed_SafeDrift_Halts()
    {
        // Core never prompts; without the CLI's pre-confirmation a Prompt-policy safe drift HALTS
        // (the non-interactive / unanswered default).
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Prompt, out TaskNode a, out TaskNode b);

        var (report, exec, prov, _, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), SafeSuffixDecision.Safe("resetsha", 2), preConfirmed: false);

        Assert.NotNull(report.DefinitionDrift);
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
    }

    [Fact]
    public async Task Prompt_PreConfirmed_SafeDrift_Resolves_WithPromptTrigger()
    {
        // Simulates the CLI having prompted the operator OUTSIDE the live region and gotten a `y`.
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Prompt, out TaskNode a, out TaskNode b);

        var (report, exec, prov, jnl, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), SafeSuffixDecision.Safe("resetsha", 2), preConfirmed: true);

        Assert.Null(report.DefinitionDrift);
        Assert.True(report.AllSucceeded);
        Assert.Equal(["resetsha"], prov.RewoundTo);
        Assert.Contains("01-a", exec.Started);
        Assert.Equal("prompt", Assert.Single(jnl.Recorded).Trigger);
    }

    [Fact]
    public async Task Reprocess_UnsafeDrift_AlwaysHalts_NoRewind()
    {
        // The un-overridable floor: no policy authorizes an unsound rewind.
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Reprocess, out TaskNode a, out TaskNode b);

        var (report, exec, prov, jnl, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b),
            SafeSuffixDecision.Refused("uncontained fan-in", blockingTask: "07-upstream"), preConfirmed: false);

        Assert.NotNull(report.DefinitionDrift);
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
        Assert.Empty(jnl.Recorded);
    }

    [Fact]
    public async Task Prompt_PreConfirmed_UnsafeDrift_StillHalts()
    {
        // Even an operator `y` cannot authorize an unsafe rewind.
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Prompt, out TaskNode a, out TaskNode b);

        var (report, _, prov, _, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b),
            SafeSuffixDecision.Refused("interleaved non-S task", blockingTask: "05-other"), preConfirmed: true);

        Assert.NotNull(report.DefinitionDrift);
        Assert.Empty(prov.RewoundTo);
    }

    [Fact]
    public async Task Reprocess_NothingToRewind_JournalOnlyReset_NoGitRewind()
    {
        // No physical suffix to remove (serial / lost plan branch) — journal-only reset is sound; the
        // git rewind is skipped but the safe set still re-runs and the resolution is recorded (no target).
        PlanDefinition plan = DriftedLinearPlan(DriftPolicy.Reprocess, out TaskNode a, out TaskNode b);

        var (report, exec, prov, jnl, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), SafeSuffixDecision.Nothing(), preConfirmed: false);

        Assert.Null(report.DefinitionDrift);
        Assert.True(report.AllSucceeded);
        Assert.Empty(prov.RewoundTo);                        // no git reset
        Assert.Contains("01-a", exec.Started);               // but the set still re-runs
        Assert.Null(Assert.Single(jnl.Recorded).RewindTarget);
    }
}
