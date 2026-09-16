using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #722 — a fan-in task's worktree was created UNDER the scheduler's settle lock, with an unbounded
/// git wait inside it. <c>TaskFinished</c> and the ready-queue enqueue both run only after that lock is
/// released, so one git call that never returned blocked every other task's settle and all dispatch,
/// permanently and silently: plan 40's run sat dead for 28 hours with a live process, no children and no
/// halt record.
///
/// <para>The fix defers the WHOLE fan-in <see cref="IWorktreeProvider.CreateSegment"/> to DEQUEUE, exactly
/// as the fork-the-rest path already defers its own <c>git worktree add</c> — so the git runs on the
/// worker that owns the task, off the gate, and a hung git parks one task instead of the run.</para>
///
/// <para><b>Every assertion here is an ARRIVAL or a VALUE, never a duration.</b> The 30-second bounds
/// exist so a regression FAILS instead of hanging a CI agent forever; they are not the thing being
/// asserted, and no test here passes or fails on how long anything took.</para>
/// </summary>
public sealed class FanInWorktreeDeferralTests
{
    /// <summary>How long a test waits for an arrival before declaring the run wedged. Not an assertion — see the class remarks.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fakes
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeJournal : ISchedulerJournal
    {
        public JournalTaskStatus StatusOf(string taskId) => JournalTaskStatus.Pending;
        public void MarkBlocked(string taskId) { }
    }

    /// <summary>
    /// A no-git provider that MODELS A PLAN BRANCH: a tip that <see cref="Integrate"/> advances and that
    /// <see cref="RewindPlanBranchTo"/> can move BACKWARDS. The backwards move is the point — it is what
    /// makes the monotonicity test a checked invariant rather than a restatement of the fake.
    ///
    /// <para><see cref="CreateSegment"/> can be GATED for one task id: it signals
    /// <see cref="CreateSegmentEntered"/> and then blocks the calling thread until
    /// <see cref="ReleaseCreateSegment"/> is set — a synchronous block, because that is what the real
    /// provider's unbounded <c>WaitForExit()</c> is. The tip is read AFTER the gate, so a test can land a
    /// sibling's commit while a fan-in waits at the door.</para>
    /// </summary>
    private sealed class TipTrackingWorktreeProvider : IWorktreeProvider
    {
        private readonly object _tipGate = new();
        private int _tip;
        private readonly Dictionary<string, string> _headByWorktree = new(StringComparer.Ordinal);
        private readonly List<(string TaskId, int Tip)> _creates = [];

        /// <summary>The task id whose <see cref="CreateSegment"/> blocks, or null for none.</summary>
        public string? GateCreateSegmentFor { get; init; }

        public TaskCompletionSource CreateSegmentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreateSegment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> RewoundTo { get; } = [];

        public static string Sha(int tip) => $"tip-{tip:D4}";

        /// <summary>The plan branch's tip right now.</summary>
        public string CurrentTip()
        {
            lock (_tipGate)
            {
                return Sha(_tip);
            }
        }

        /// <summary>Each <see cref="CreateSegment"/>'s (task, tip) in the order the tip was READ — read and recorded under one lock, so this order is the read order even under parallel workers.</summary>
        public IReadOnlyList<(string TaskId, int Tip)> Creates()
        {
            lock (_tipGate)
            {
                return [.. _creates];
            }
        }

        /// <summary>What the worktree at <paramref name="path"/> is actually checked out at.</summary>
        public string HeadOfWorktree(string path)
        {
            lock (_tipGate)
            {
                return _headByWorktree[path];
            }
        }

        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) => new()
        {
            IntegrationWorktreePath = $"integ://{runId}/_integration",
            PlanBranchName = $"guardrails/{planName}",
            OriginalBranch = "main",
            OriginalHeadSha = Sha(0),
            RunId = runId
        };

        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct)
        {
            if (GateCreateSegmentFor is { } gated && string.Equals(taskId, gated, StringComparison.Ordinal))
            {
                CreateSegmentEntered.TrySetResult();

                // Synchronous, like the real GitInWithEnv's WaitForExit(): this thread is GONE until
                // released. Whether that costs one task or the whole run is exactly what is under test.
                ReleaseCreateSegment.Task.GetAwaiter().GetResult();
            }

            string path = $"seg://{integ.RunId}/{taskId}/attempt-{attempt}";
            string tip;
            lock (_tipGate)
            {
                tip = Sha(_tip);
                _creates.Add((taskId, _tip));

                // The worktree IS checked out at the tip the handle names: base and tree come from the
                // same read, which is the property the anti-tautology test pins.
                _headByWorktree[path] = tip;
            }

            return new WorktreeHandle
            {
                WorktreePath = path,
                SegmentBranchName = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}",
                TaskBase = tip,
                RecordedCommitSha = "",
                PlanBranchHead = tip,
                TaskId = taskId
            };
        }

        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) => new()
        {
            WorktreePath = upstreamSegment.WorktreePath,
            SegmentBranchName = upstreamSegment.SegmentBranchName,
            TaskBase = upstreamSegment.RecordedCommitSha,
            RecordedCommitSha = upstreamSegment.RecordedCommitSha,
            PlanBranchHead = upstreamSegment.PlanBranchHead,
            TaskId = taskId
        };

        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt)
        {
            string path = $"fork://{taskId}/attempt-{attempt}";
            lock (_tipGate)
            {
                _headByWorktree[path] = producerRecordedSha;
            }

            return new WorktreeHandle
            {
                WorktreePath = path,
                SegmentBranchName = $"guardrails/fork/{taskId}/attempt-{attempt}",
                TaskBase = producerRecordedSha,
                RecordedCommitSha = producerRecordedSha,
                PlanBranchHead = producerRecordedSha,
                TaskId = taskId
            };
        }

        /// <summary>A settled task's work lands on the plan branch: the tip GAINS a commit.</summary>
        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
        {
            string taskId = string.IsNullOrEmpty(segment.TaskId) ? segment.SegmentBranchName : segment.TaskId;
            lock (_tipGate)
            {
                _tip++;
                segment.RecordedCommitSha = Sha(_tip);
            }

            return IntegrationResult.FastForward;
        }

        public string CurrentPlanBranchTip(IntegrationHandle integ) => CurrentTip();

        /// <summary>The one NON-monotone writer. Moving the tip backwards is possible here BY DESIGN, so a mid-drain rewind would be caught rather than argued about.</summary>
        public void RewindPlanBranchTo(IntegrationHandle integ, string resetTarget)
        {
            RewoundTo.Enqueue(resetTarget);
            lock (_tipGate)
            {
                _tip = Math.Max(0, _tip - 1);
            }
        }

        public void Discard(WorktreeHandle handle) { }

        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) { }

        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            MergeOnSuccessResult.FastForwarded;
    }

    /// <summary>
    /// A TCS-gated executor that also records what handle each task was given. Gated tasks block until
    /// <see cref="Complete"/>; ungated ones return immediately.
    /// </summary>
    private sealed class GatedRecordingExecutor : ITaskExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _gated;

        public GatedRecordingExecutor(params string[] gated) =>
            _gated = new HashSet<string>(gated, StringComparer.Ordinal);

        public ConcurrentDictionary<string, string> AssignedPath { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, string> AssignedTaskBase { get; } = new(StringComparer.Ordinal);

        /// <summary>Runs on the worker thread before the (possibly gated) wait — the seam a mutation proof uses.</summary>
        public Action<TaskNode, IWorktreeProvider>? OnExecute { get; init; }

        public IWorktreeProvider? Provider { get; set; }

        public void Complete(string id) => Gate(id).TrySetResult();

        public void CompleteAll()
        {
            foreach (string id in _gated)
            {
                Gate(id).TrySetResult();
            }
        }

        public async Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            AssignedPath[task.Id] = worktree.WorktreePath;
            AssignedTaskBase[task.Id] = worktree.TaskBase;

            if (Provider is { } provider)
            {
                OnExecute?.Invoke(task, provider);
            }

            if (_gated.Contains(task.Id))
            {
                await Gate(task.Id).Task.WaitAsync(cancellationToken);
            }

            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success"
            };
        }

        private TaskCompletionSource Gate(string id) =>
            _gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// Records the ARRIVAL of each task's <see cref="IRunObserver.TaskFinished"/> as a completed task, so a
    /// test awaits the event itself rather than sampling a flag after a sleep.
    /// </summary>
    private sealed class SettleArrivalObserver : IRunObserver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new(StringComparer.Ordinal);

        public Task Finished(string taskId) => Slot(taskId).Task;

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) => Slot(result.TaskId).TrySetResult();

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        private TaskCompletionSource Slot(string taskId) =>
            _finished.GetOrAdd(taskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static Scheduler Create(
        PlanDefinition plan, ITaskExecutor executor, IWorktreeProvider provider, IRunObserver observer, int parallelism) =>
        new(plan, executor, new FakeJournal(), provider, observer, parallelism);

    /// <summary>
    /// Await <paramref name="arrival"/>, failing with <paramref name="whatItMeans"/> if it never comes. The
    /// ASSERTION is that the event arrived; the bound only turns a wedged run into a readable failure
    /// instead of a hung agent.
    /// </summary>
    private static async Task ArrivesAsync(Task arrival, string whatItMeans)
    {
        try
        {
            await arrival.WaitAsync(Bound, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail(whatItMeans);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. The issue's test — a sibling settles while the fan-in's git is hung.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHungFanInWorktreeCreation_DoesNotBlockASiblingsSettle()
    {
        // 01-p1 + 02-p2 → 03-fanin (a multi-producer dependent: a fresh segment off the plan tip).
        // 04-independent shares nothing with any of them — its settle is the one that must still land.
        PlanDefinition plan = Plan(
            Task("01-p1"),
            Task("02-p2"),
            Task("03-fanin", "01-p1", "02-p2"),
            Task("04-independent"));

        var provider = new TipTrackingWorktreeProvider { GateCreateSegmentFor = "03-fanin" };
        var executor = new GatedRecordingExecutor("01-p1", "02-p2", "03-fanin", "04-independent");
        var observer = new SettleArrivalObserver();

        Task<RunReport> run = Create(plan, executor, provider, observer, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        try
        {
            executor.Complete("01-p1");
            executor.Complete("02-p2");

            // 02-p2's settle is what makes 03-fanin ready. Either way, its CreateSegment is now running —
            // the question this test exists to answer is WHERE.
            await ArrivesAsync(
                provider.CreateSegmentEntered.Task,
                "the fan-in's CreateSegment was never entered, so this test never reached the condition it "
                + "is about — check the fixture's topology, not the fix.");

            executor.Complete("04-independent");

            // THE assertion. Under the pre-#722 code the fan-in's CreateSegment runs INSIDE the settle
            // lock, so this sibling's settle cannot take the gate, its TaskFinished never fires, and every
            // other task's dispatch stops with it — for as long as the git call takes, which in the plan-40
            // incident was 28 hours and counting.
            await ArrivesAsync(
                observer.Finished("04-independent"),
                "04-independent settled but its TaskFinished never arrived while the fan-in's worktree "
                + "creation was blocked (#722): the fan-in's CreateSegment is still running under the "
                + "scheduler's settle lock, so one hung git stops every settle and all dispatch.");

            provider.ReleaseCreateSegment.TrySetResult();
            executor.Complete("03-fanin");

            RunReport report = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(report.AllSucceeded, string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

            // And the fan-in did get a FRESH segment (not a reuse or a fork) — the M1 §A1 choice the
            // deferral must preserve.
            Assert.Contains(provider.Creates(), c => c.TaskId == "03-fanin");
        }
        finally
        {
            provider.ReleaseCreateSegment.TrySetResult();
            executor.CompleteAll();
            try { await run.WaitAsync(Bound, CancellationToken.None); } catch (Exception) { /* teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. Monotonicity guard — the enumeration turned into a checked invariant.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reading the fan-in's base at DEQUEUE rather than at its producer's settle is only safe because the
    /// plan branch is gains-only while a drain is in flight: a failed re-verify creates no commit to rewind,
    /// and the one non-monotone writer (<see cref="IWorktreeProvider.RewindPlanBranchTo"/>) runs in the
    /// pre-pass and between waves, never during dispatch.
    /// <para>The fake CAN move its tip backwards, so this asserts that property rather than assuming it.</para>
    /// </summary>
    [Fact]
    public async Task ThePlanBranchNeverMovesBackwards_WhileADrainIsInFlight()
    {
        // Two fan-ins (04-d, 06-f) so more than one base is read at dequeue, with integrations landing
        // between them.
        PlanDefinition plan = Plan(
            Task("01-a"),
            Task("02-b", "01-a"),
            Task("03-c", "01-a"),
            Task("04-d", "02-b", "03-c"),
            Task("05-e"),
            Task("06-f", "04-d", "05-e"));

        var provider = new TipTrackingWorktreeProvider();
        var executor = new GatedRecordingExecutor();
        var observer = new SettleArrivalObserver();

        RunReport report = await Create(plan, executor, provider, observer, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // No non-monotone writer ran at all during the drain.
        Assert.Empty(provider.RewoundTo);

        IReadOnlyList<(string TaskId, int Tip)> creates = provider.Creates();

        // Non-vacuity: several bases were read, and the branch really did advance between them — otherwise
        // "never went backwards" would be true of a tip that never moved.
        Assert.True(creates.Count >= 4, $"expected at least 4 CreateSegment calls, got {creates.Count}");
        Assert.True(
            creates[^1].Tip > creates[0].Tip,
            $"the plan tip never advanced across {creates.Count} segment creations, so this guard proves nothing");

        for (int i = 1; i < creates.Count; i++)
        {
            Assert.True(
                creates[i].Tip >= creates[i - 1].Tip,
                $"the plan branch moved BACKWARDS during the drain: '{creates[i - 1].TaskId}' read tip "
                + $"{creates[i - 1].Tip} and '{creates[i].TaskId}' then read tip {creates[i].Tip}. A base read "
                + "at dequeue is only sound while the branch is gains-only in flight (#722).");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. Anti-tautology — the base IS the tree, read at dequeue, after a sibling's commit landed.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fan-in held at the door while another task's work lands on the plan branch must come out based on
    /// the tip that ALREADY CONTAINS that work, and its handle's <c>TaskBase</c> must equal what its
    /// worktree is actually checked out at — the two are the same read, which is what makes a dequeue-time
    /// base atomic by construction. A future refactor that separates the capture from the creation (the
    /// rejected option B) fails here rather than in a 28-hour run.
    /// </summary>
    [Fact]
    public async Task AFanInMaterializedAtDequeue_IsBasedOnTheTipItsWorktreeActuallyHas()
    {
        PlanDefinition plan = Plan(
            Task("01-p1"),
            Task("02-p2"),
            Task("03-fanin", "01-p1", "02-p2"),
            Task("04-late"));

        var provider = new TipTrackingWorktreeProvider { GateCreateSegmentFor = "03-fanin" };
        var executor = new GatedRecordingExecutor("01-p1", "02-p2", "03-fanin", "04-late");
        var observer = new SettleArrivalObserver();

        Task<RunReport> run = Create(plan, executor, provider, observer, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        try
        {
            executor.Complete("01-p1");
            executor.Complete("02-p2");

            await ArrivesAsync(
                provider.CreateSegmentEntered.Task,
                "the fan-in's CreateSegment was never entered — the fixture never reached the condition.");

            string tipWhileWaiting = provider.CurrentTip();

            // 04-late's work lands on the plan branch while the fan-in is still at the door.
            executor.Complete("04-late");
            await ArrivesAsync(
                observer.Finished("04-late"),
                "04-late never settled while the fan-in's worktree creation was blocked (#722): its settle "
                + "is stuck behind the fan-in's git, so no later commit can land for the fan-in to see.");

            string tipAfterLate = provider.CurrentTip();
            Assert.NotEqual(tipWhileWaiting, tipAfterLate);

            provider.ReleaseCreateSegment.TrySetResult();
            executor.Complete("03-fanin");

            RunReport report = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(report.AllSucceeded, string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

            string fanInBase = executor.AssignedTaskBase["03-fanin"];
            string fanInPath = executor.AssignedPath["03-fanin"];

            // It saw the LATER commit — the base was read at dequeue, not captured at its producer's settle.
            Assert.Equal(tipAfterLate, fanInBase);

            // And base and tree are the same read: a capture separated from the creation would let these
            // two disagree, silently, exactly when a commit lands between them.
            Assert.Equal(provider.HeadOfWorktree(fanInPath), fanInBase);
        }
        finally
        {
            provider.ReleaseCreateSegment.TrySetResult();
            executor.CompleteAll();
            try { await run.WaitAsync(Bound, CancellationToken.None); } catch (Exception) { /* teardown */ }
        }
    }
}
