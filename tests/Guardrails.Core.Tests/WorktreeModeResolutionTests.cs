using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins issue #596: worktree mode is decided ONCE per run, by ONE predicate, over a git probe whose
/// FAILURE is not reported as a fact.
///
/// <para><b>The two defects.</b> (1) <c>maxParallelism &gt; 1 &amp;&amp; IsGitRepository(...)</c> was spelled
/// twice in <c>SchedulerFactory</c> — the provider wiring and the public <c>WouldUseWorktreeMode</c> — and
/// re-evaluated, with a fresh <c>git rev-parse</c> subprocess each time, at six load-bearing consumers. Two
/// evaluations could disagree WITHIN one run in both directions, silently. (2) The probe ended
/// <c>catch { return false; }</c>, so "git says this is not a repository" and "git could not be run at all"
/// produced the same answer — the second being an unknown reported as a fact, which downgraded a parallel
/// run to serial without a single console line, observer event or journal field to show for it.</para>
///
/// <para><b>Why these tests are two-sided.</b> Each assertion below fails on the pre-#596 code:
/// <see cref="GitCannotBeRun_KeepsTheRequestedWorktreeMode_AndReportsTheProbeFailure"/> demands the exact
/// opposite of <c>catch { return false; }</c>, and
/// <see cref="Create_ReadsTheHandedDownResolution_AndNeverReprobes"/> demands zero probe calls where the
/// old wiring always ran its own. The counting/failing probes are the injectable-probe seam the harness
/// uses everywhere else (<c>IExecutableProbe</c>) rather than a dependency on whether the test host happens
/// to have git.</para>
/// </summary>
public sealed class WorktreeModeResolutionTests
{
    /// <summary>A probe that counts calls and answers with a fixed result — the concurrency-free equivalent
    /// of a gated fake: the COUNT is the observable that proves a decision was folded once.</summary>
    private sealed class CountingProbe(GitWorkTreeProbeResult answer) : IGitWorkTreeProbe
    {
        public int Calls { get; private set; }

        public GitWorkTreeProbeResult Probe(string workspace)
        {
            Calls++;
            return answer;
        }
    }

    private static PlanDefinition PlanWith(int maxParallelism, string? workspace = null) =>
        new()
        {
            PlanDirectory = Path.Combine(Path.GetTempPath(), "gr-596-plan"),
            Workspace = workspace ?? Path.Combine(Path.GetTempPath(), "gr-596-workspace"),
            Config = new RunConfig { Version = 1, MaxParallelism = maxParallelism },
            Tasks = []
        };

    [Fact]
    public void SerialPlan_ResolvesSerial_WithoutEverSpawningTheGitProbe()
    {
        var probe = new CountingProbe(GitWorkTreeProbeResult.Inside);

        WorktreeModeResolution resolution = SchedulerFactory.ResolveWorktreeMode(PlanWith(1), probe);

        Assert.False(resolution.Enabled);
        Assert.Equal(WorktreeModeReason.SerialByConfiguration, resolution.Reason);
        Assert.Null(resolution.GitProbeFailure);

        // A serial run has NO git dependency (SSOT §1, the shared-workspace model), so the probe must not
        // even be spawned — the short-circuit is contract, not an optimization.
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void ParallelPlan_InAGitWorkTree_ResolvesWorktreeMode()
    {
        var probe = new CountingProbe(GitWorkTreeProbeResult.Inside);

        WorktreeModeResolution resolution = SchedulerFactory.ResolveWorktreeMode(PlanWith(3), probe);

        Assert.True(resolution.Enabled);
        Assert.Equal(WorktreeModeReason.WorktreeMode, resolution.Reason);
        Assert.Null(resolution.GitProbeFailure);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void ParallelPlan_GitAnswersNotARepository_ResolvesSerial_WithNoProbeFailure()
    {
        var probe = new CountingProbe(GitWorkTreeProbeResult.Outside);

        WorktreeModeResolution resolution = SchedulerFactory.ResolveWorktreeMode(PlanWith(3), probe);

        // git RAN and said no. That is a legitimate answer: serial, and NOT a probe failure — the
        // distinction the pre-#596 code could not make.
        Assert.False(resolution.Enabled);
        Assert.Equal(WorktreeModeReason.WorkspaceNotAGitRepository, resolution.Reason);
        Assert.Null(resolution.GitProbeFailure);
    }

    [Fact]
    public void GitCannotBeRun_KeepsTheRequestedWorktreeMode_AndReportsTheProbeFailure()
    {
        // THE #596 REGRESSION BAR. Pre-fix this path was `catch { return false; }` — the run silently
        // became serial. An unavailable git is an UNKNOWN, and the workspace's git-ness was already
        // certified at validation (GR2015 walks the filesystem and spawns nothing), so the plan's request
        // stands and the failure is surfaced instead of being laundered into a fact.
        var probe = new CountingProbe(
            GitWorkTreeProbeResult.CouldNotRun("Win32Exception: The system cannot find the file specified."));

        WorktreeModeResolution resolution = SchedulerFactory.ResolveWorktreeMode(PlanWith(4), probe);

        Assert.True(resolution.Enabled);
        Assert.Equal(WorktreeModeReason.WorktreeMode, resolution.Reason);
        Assert.NotNull(resolution.GitProbeFailure);
        Assert.Contains("cannot find the file", resolution.GitProbeFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public void GitCannotBeRun_OnASerialPlan_ChangesNothing()
    {
        // The unavailable-git branch must not leak into a plan that never asked for worktree mode: the
        // probe is never reached, so there is nothing to announce and nothing to escalate.
        var probe = new CountingProbe(GitWorkTreeProbeResult.CouldNotRun("boom"));

        WorktreeModeResolution resolution = SchedulerFactory.ResolveWorktreeMode(PlanWith(1), probe);

        Assert.False(resolution.Enabled);
        Assert.Null(resolution.GitProbeFailure);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void WouldUseWorktreeMode_IsTheBooleanFaceOfTheSameResolution()
    {
        // One predicate, one place: the single-shot boolean helper (Revalidate's refusal) must not be a
        // second spelling that can drift from the resolution every other consumer reads.
        PlanDefinition serial = PlanWith(1);

        Assert.Equal(SchedulerFactory.ResolveWorktreeMode(serial).Enabled,
            SchedulerFactory.WouldUseWorktreeMode(serial));
    }

    [Fact]
    public void Create_ReadsTheHandedDownResolution_AndNeverReprobes()
    {
        // The de-duplication half of #596, asserted as a FACT about the code rather than as wording: the
        // factory is handed the run's one answer and does not re-derive it. Pre-fix the factory always ran
        // its own probe, so the count could never be zero — and that second evaluation is exactly what
        // could disagree with the CLI's.
        string workspace = Path.Combine(Path.GetTempPath(), "gr-596-create", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string planDir = Path.Combine(workspace, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        try
        {
            var plan = new PlanDefinition
            {
                PlanDirectory = planDir,
                Workspace = workspace,
                Config = new RunConfig { Version = 1, MaxParallelism = 3 },
                Tasks = []
            };

            // The workspace is a bare temp directory — NOT a git work tree — so a factory that re-derives
            // the predicate for itself necessarily lands on "serial" and fires the F7 no-provider clamp.
            var probe = new CountingProbe(GitWorkTreeProbeResult.Inside);
            WorktreeModeResolution handedDown = SchedulerFactory.ResolveWorktreeMode(plan, probe);
            Assert.True(handedDown.Enabled);
            Assert.Equal(1, probe.Calls);

            var observer = new ClampRecordingObserver();
            SchedulerFactory.Create(
                plan, new ProcessRunner(), FakeExecutableProbe.All, observer, worktreeMode: handedDown);

            // The factory honored the run's ONE answer: a provider was wired, so the Scheduler's F7
            // no-provider clamp never fired. Pre-#596 the factory ran its own `git rev-parse` here, got
            // "not a repository" for this temp directory, and clamped — the disagreement-within-one-run
            // this issue was filed on, reproduced as a test.
            Assert.Null(observer.ClampedRequested);

            // And the run's probe was consulted exactly once, by the resolver — never again.
            Assert.Equal(1, probe.Calls);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>Records the F7 no-provider clamp — the observable that says which way the wiring went.</summary>
    private sealed class ClampRecordingObserver : IRunObserver
    {
        public int? ClampedRequested { get; private set; }

        public void ParallelismClampedNoProvider(int requested) => ClampedRequested = requested;

        // The three required members; everything else on IRunObserver is a default no-op.
        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }
}
