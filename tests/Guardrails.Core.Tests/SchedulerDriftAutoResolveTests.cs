using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler-level wiring tests for the Part C safe-auto-resolve gate keyed off the unified
/// <c>autonomyPolicy</c> (SSOT §2.1/§7.2): with the safe-suffix VERDICT scripted by a fake provider (the
/// pure check is proven exhaustively in <see cref="SafeSuffixEvaluatorTests"/>), assert the policy gate
/// resolves-vs-halts correctly, that a resolve rewinds + journal-resets + re-runs the safe set + records a
/// <c>boundary:"drift"</c> <see cref="DecisionEntry"/> to the unified <c>decisions[]</c> log, that an
/// UNSAFE drift halts under EVERY policy, and that a moved plan-branch tip (compare-and-swap) halts rather
/// than rewinds. TCS-free: the fake executor returns synchronously.
/// </summary>
public sealed class SchedulerDriftAutoResolveTests
{
    private sealed class DriftFakeJournal : ISchedulerJournal
    {
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _succeeded = new(StringComparer.Ordinal);

        public List<string> ResetToPending { get; } = [];
        public List<DecisionEntry> Recorded { get; } = [];

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

        public void RecordDecision(DecisionEntry entry) => Recorded.Add(entry);
    }

    /// <summary>A fake provider that SCRIPTS the safe-suffix verdict + the current tip and records the rewind, delegating every topology op to <see cref="FakeWorktreeProvider"/>.</summary>
    private sealed class DriftFakeProvider(SafeSuffixDecision decision, string currentTip = "") : IWorktreeProvider
    {
        private readonly FakeWorktreeProvider _inner = new();
        public List<string> RewoundTo { get; } = [];

        public SafeSuffixDecision EvaluateSafeSuffix(IntegrationHandle integ, IReadOnlySet<string> safeSet) => decision;
        public string CurrentPlanBranchTip(IntegrationHandle integ) => currentTip;
        public void RewindPlanBranchTo(IntegrationHandle integ, string resetTarget) => RewoundTo.Add(resetTarget);
        // TracksPlanBranchTrailers stays false (default) — no crash-marker write to a fake plan dir, and the
        // trailer reconciliation stays off (the journal alone is authoritative for these in-memory tests).

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
        public List<DecisionEntry> DecisionCalls { get; } = [];
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void DecisionRecorded(DecisionEntry entry) => DecisionCalls.Add(entry);
    }

    private static PlanDefinition DriftedLinearPlan(AutonomyPolicy policy, out TaskNode a, out TaskNode b)
    {
        a = Task("01-a");
        b = Task("02-b", "01-a");
        return Plan(a, b) with { Config = new RunConfig { Version = 1, AutonomyPolicy = policy } };
    }

    private static DriftFakeJournal JournalWithDriftOn01(TaskNode a, TaskNode b)
    {
        var journal = new DriftFakeJournal();
        journal.MarkSucceeded("01-a", "sha256:stale-does-not-match"); // drifted
        journal.MarkSucceeded("02-b", Guardrails.Core.Journal.TaskDefinitionHash.Compute(b)); // unchanged descendant
        return journal;
    }

    /// <summary>An operator-approved plan matching the scripted safe decision (ResetTarget + set + tip).</summary>
    private static DriftAuthorization Auth(string? resetTarget, string expectedTip, params string[] safeSet) =>
        new() { SafeSet = safeSet, ResetTarget = resetTarget, ExpectedTip = expectedTip };

    private static async Task<(RunReport Report, RecordingExecutor Exec, DriftFakeProvider Prov, DriftFakeJournal Jnl, RecordingObserver Obs)>
        RunAsync(PlanDefinition plan, DriftFakeJournal journal, DriftFakeProvider prov, DriftAuthorization? auth)
    {
        var exec = new RecordingExecutor();
        var obs = new RecordingObserver();
        var scheduler = new Scheduler(
            plan, exec, journal, worktreeProvider: prov, observer: obs, maxParallelism: 2, driftAuthorization: auth);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (report, exec, prov, journal, obs);
    }

    [Fact]
    public async Task Auto_SafeDrift_Rewinds_ReRunsSafeSet_RecordsDriftDecision()
    {
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Auto, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Safe("resetsha", removedCommitCount: 2));

        var (report, exec, _, jnl, obs) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.Null(report.DefinitionDrift);                 // resolved, not halted
        Assert.True(report.AllSucceeded);
        Assert.Equal(["resetsha"], prov.RewoundTo);          // DESTRUCTIVE rewind performed
        Assert.Equal(["01-a", "02-b"], jnl.ResetToPending.OrderBy(x => x)); // whole safe set journal-reset
        Assert.Contains("01-a", exec.Started);               // re-ran from the clean base
        Assert.Contains("02-b", exec.Started);

        // A boundary:"drift" DecisionEntry was recorded to the unified decisions[] log.
        DecisionEntry entry = Assert.Single(jnl.Recorded);
        Assert.Equal("drift", entry.Boundary);
        Assert.Equal("auto", entry.Policy);                  // autonomyPolicy=auto in force
        Assert.Equal("auto-applied", entry.Decision);
        Assert.Contains("resetsha", entry.Headline);         // the rewind target propagated
        Assert.Contains("01-a", entry.Subject);
        Assert.Contains("02-b", entry.Subject);              // descendant re-run too
        Assert.NotNull(report.Decision);
        Assert.Equal("auto-applied", Assert.Single(obs.DecisionCalls).Decision); // surfaced live
    }

    [Fact]
    public async Task Halt_SafeDrift_AlwaysHalts_NoRewind()
    {
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Halt, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Safe("resetsha", 2));

        var (report, exec, _, jnl, _) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.NotNull(report.DefinitionDrift);              // strict opt-out: Part A halt preserved
        Assert.True(report.DefinitionDrift!.SafeToAutoResolve); // it WAS safe — just not authorized
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
        Assert.Empty(jnl.ResetToPending);
        Assert.Empty(jnl.Recorded);
    }

    [Fact]
    public async Task Prompt_NotAuthorized_SafeDrift_Halts()
    {
        // Core never prompts; without the CLI's captured authorization a Prompt-policy safe drift HALTS.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Prompt, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Safe("resetsha", 2));

        var (report, exec, _, _, _) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.NotNull(report.DefinitionDrift);
        Assert.True(report.DefinitionDrift!.SafeToAutoResolve);
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
    }

    [Fact]
    public async Task Prompt_Authorized_MatchingPlan_Resolves_WithPromptApprovedDecision()
    {
        // Simulates the CLI having prompted OUTSIDE the live region and captured a `y` for THIS plan.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Prompt, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Safe("resetsha", 2));

        var (report, exec, _, jnl, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), prov, Auth("resetsha", "", "01-a", "02-b"));

        Assert.Null(report.DefinitionDrift);
        Assert.True(report.AllSucceeded);
        Assert.Equal(["resetsha"], prov.RewoundTo);
        Assert.Contains("01-a", exec.Started);
        DecisionEntry entry = Assert.Single(jnl.Recorded);
        Assert.Equal("prompt", entry.Policy);
        Assert.Equal("prompted-approved", entry.Decision);
    }

    [Fact]
    public async Task Prompt_Authorized_DivergedPlan_Halts_ConsentIntegrity()
    {
        // The operator approved a DIFFERENT plan (files edited during the blocking prompt) — the captured
        // authorization no longer matches this run's fresh decision, so the harness HALTS rather than
        // rewind a set the human never saw.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Prompt, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Safe("resetsha", 2));

        var (report, exec, _, _, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), prov, Auth("A-DIFFERENT-TARGET", "", "01-a", "02-b"));

        Assert.NotNull(report.DefinitionDrift);
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
    }

    [Fact]
    public async Task Auto_TipMoved_Halts_CompareAndSwap()
    {
        // A concurrent same-plan session advanced the branch between the decision and the rewind: the
        // scripted decision saw tip "A", but the current tip is "B" → CAS mismatch → HALT (no rewind), so
        // the harness never discards the other session's work.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Auto, out TaskNode a, out TaskNode b);
        var decision = SafeSuffixDecision.Safe("resetsha", 2) with { ExpectedTip = "tip-A" };
        var prov = new DriftFakeProvider(decision, currentTip: "tip-B");

        var (report, exec, _, jnl, _) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.NotNull(report.DefinitionDrift);
        Assert.True(report.DefinitionDrift!.SafeToAutoResolve); // safe in principle — only the tip moved
        Assert.Empty(prov.RewoundTo);
        Assert.Empty(jnl.Recorded);
        Assert.Empty(exec.Started);
    }

    [Fact]
    public async Task Auto_UnsafeDrift_AlwaysHalts_NoRewind_SurfacesRefusal()
    {
        // The un-overridable floor: no policy authorizes an unsound rewind, and the refusal reason + blocker
        // are surfaced so the CLI steers to the full rebuild instead of a re-halting flag.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Auto, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(
            SafeSuffixDecision.Refused("uncontained fan-in", blockingTask: "07-upstream"));

        var (report, exec, _, jnl, _) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.NotNull(report.DefinitionDrift);
        Assert.False(report.DefinitionDrift!.SafeToAutoResolve);          // UNSAFE — steer to reset -y
        Assert.Equal("uncontained fan-in", report.DefinitionDrift.RewindRefusal);
        Assert.Equal("07-upstream", report.DefinitionDrift.RewindBlockingTask);
        Assert.Empty(exec.Started);
        Assert.Empty(prov.RewoundTo);
        Assert.Empty(jnl.Recorded);
    }

    [Fact]
    public async Task Prompt_Authorized_UnsafeDrift_StillHalts()
    {
        // Even an operator `y` cannot authorize an unsafe rewind.
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Prompt, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(
            SafeSuffixDecision.Refused("interleaved non-S task", blockingTask: "05-other"));

        var (report, _, _, _, _) = await RunAsync(
            plan, JournalWithDriftOn01(a, b), prov, Auth("resetsha", "", "01-a", "02-b"));

        Assert.NotNull(report.DefinitionDrift);
        Assert.False(report.DefinitionDrift!.SafeToAutoResolve);
        Assert.Empty(prov.RewoundTo);
    }

    [Fact]
    public async Task Auto_NothingToRewind_JournalOnlyReset_NoGitRewind()
    {
        // No physical suffix to remove (serial / lost plan branch) — journal-only reset is sound; the git
        // rewind is skipped but the safe set still re-runs and the decision is recorded (no rewind target).
        PlanDefinition plan = DriftedLinearPlan(AutonomyPolicy.Auto, out TaskNode a, out TaskNode b);
        var prov = new DriftFakeProvider(SafeSuffixDecision.Nothing());

        var (report, exec, _, jnl, _) = await RunAsync(plan, JournalWithDriftOn01(a, b), prov, auth: null);

        Assert.Null(report.DefinitionDrift);
        Assert.True(report.AllSucceeded);
        Assert.Empty(prov.RewoundTo);                        // no git reset
        Assert.Contains("01-a", exec.Started);               // but the set still re-runs
        Assert.Contains("no plan-branch rewind needed", Assert.Single(jnl.Recorded).Headline);
    }
}
