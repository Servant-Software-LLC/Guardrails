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

    /// <summary>The operation string the harness reports for a fresh segment. Pinned here because it is operator-facing text.</summary>
    private const string FreshSegmentOperation = "creating a worktree off the plan branch";

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
    /// <see cref="RewindPlanBranchTo"/> can move BACKWARDS.
    ///
    /// <para>Two operations can be GATED, each blocking the calling thread synchronously — because that is
    /// what the real provider's unbounded <c>WaitForExit()</c> is: <see cref="CreateSegment"/> for one task
    /// id, and <see cref="CurrentPlanBranchTip"/> for every caller. The tip is read AFTER the
    /// <see cref="CreateSegment"/> gate, so a test can land a sibling's commit while a fan-in waits at the
    /// door.</para>
    /// </summary>
    private sealed class TipTrackingWorktreeProvider : IWorktreeProvider
    {
        private readonly object _tipGate = new();
        private int _tip;
        private readonly Dictionary<string, string> _headByWorktree = new(StringComparer.Ordinal);
        private readonly List<(string TaskId, int Tip)> _creates = [];
        private int _tipReads;
        private int _integrates;

        /// <summary>The task id whose <see cref="CreateSegment"/> blocks, or null for none.</summary>
        public string? GateCreateSegmentFor { get; init; }

        /// <summary>When true, EVERY <see cref="CurrentPlanBranchTip"/> call blocks until released.</summary>
        public bool GateCurrentPlanBranchTip { get; init; }

        /// <summary>Asked at the top of <see cref="CreateSegment"/>: had the observer already been told?</summary>
        public Func<string, bool>? WasAnnounced { get; set; }

        public TaskCompletionSource CreateSegmentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreateSegment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePlanTipRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> RewoundTo { get; } = [];

        /// <summary>taskId → whether the observer had ALREADY been told when the git call began (#722 F1).</summary>
        public ConcurrentDictionary<string, bool> AnnouncedBeforeCreate { get; } = new(StringComparer.Ordinal);

        public int TipReads => Volatile.Read(ref _tipReads);

        public static string Sha(int tip) => $"tip-{tip:D4}";

        /// <summary>The plan branch's tip right now, without going through the gated public read.</summary>
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
            // #722 F1: sampled BEFORE anything else, so the test can pin the documented ordering — the
            // observer is told the wait has begun BEFORE the call that may never return.
            AnnouncedBeforeCreate[taskId] = WasAnnounced?.Invoke(taskId) ?? false;

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
            lock (_tipGate)
            {
                _tip++;
                segment.RecordedCommitSha = Sha(_tip);
            }

            Interlocked.Increment(ref _integrates);
            return IntegrationResult.FastForward;
        }

        /// <summary>
        /// The OTHER unbounded git this seam can run (<c>git rev-parse</c>). Gateable for the same reason
        /// <see cref="CreateSegment"/> is: rejected option B would have put exactly this call back under the
        /// settle lock.
        /// </summary>
        public string CurrentPlanBranchTip(IntegrationHandle integ)
        {
            Interlocked.Increment(ref _tipReads);

            // Gated only ONCE THE DRAIN IS UNDER WAY — after at least one task has integrated. The
            // scheduler legitimately reads the tip in its synchronous setup (the resume/drift pre-pass),
            // long before any worker exists; blocking that would wedge the run during startup and prove
            // nothing about the settle lock. Keying on "has anything integrated yet" makes the arming
            // deterministic instead of a race against the test's own timing.
            if (GateCurrentPlanBranchTip && Volatile.Read(ref _integrates) > 0)
            {
                ReleasePlanTipRead.Task.GetAwaiter().GetResult();
            }

            return CurrentTip();
        }

        /// <summary>The one NON-monotone writer this fake models. Moving the tip backwards is possible here BY DESIGN.</summary>
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
    /// Records the ARRIVAL of each task's <see cref="IRunObserver.TaskFinished"/> and
    /// <see cref="IRunObserver.TaskWaitingOnWorktree"/> as completed tasks, so a test awaits the event
    /// itself rather than sampling a flag after a sleep.
    ///
    /// <para>Implementing <c>TaskWaitingOnWorktree</c> is load-bearing (#722 F1): while this observer
    /// inherited the interface's empty default, BOTH emitter lines in the Scheduler could be deleted and the
    /// entire suite stayed green — the containment half of the fix was unguarded at its source.</para>
    /// </summary>
    private sealed class SettleArrivalObserver : IRunObserver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiting = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _operations = new(StringComparer.Ordinal);

        public Task Finished(string taskId) => Slot(_finished, taskId).Task;

        public Task WaitingOn(string taskId) => Slot(_waiting, taskId).Task;

        public bool WasAnnounced(string taskId) => _operations.ContainsKey(taskId);

        public string? OperationFor(string taskId) =>
            _operations.TryGetValue(taskId, out string? operation) ? operation : null;

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) => Slot(_finished, result.TaskId).TrySetResult();

        public void TaskWaitingOnWorktree(TaskNode task, string operation)
        {
            _operations[task.Id] = operation;
            Slot(_waiting, task.Id).TrySetResult();
        }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        private static TaskCompletionSource Slot(ConcurrentDictionary<string, TaskCompletionSource> map, string key) =>
            map.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
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
        provider.WasAnnounced = observer.WasAnnounced;

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

            // #722 F1 — the containment half, guarded at its SOURCE. Without these three assertions both
            // emitter lines in the Scheduler could be deleted with the whole suite still green, on a
            // surface that by construction only appears during a hang nobody provokes in CI.
            await ArrivesAsync(
                observer.WaitingOn("03-fanin"),
                "the Scheduler never raised TaskWaitingOnWorktree for the blocked fan-in (#722): the wait "
                + "is invisible on every surface, which is the half of this issue that makes a hang legible.");

            Assert.Equal(FreshSegmentOperation, observer.OperationFor("03-fanin"));

            // The documented ordering: told BEFORE the call, because after it there may be nothing left to
            // tell anyone with.
            Assert.True(
                provider.AnnouncedBeforeCreate["03-fanin"],
                "TaskWaitingOnWorktree was raised AFTER CreateSegment began (#722). A git that never "
                + "returns would then announce nothing at all — the announcement must precede the call it "
                + "is about.");

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
    // 2. The PRE-PASS — the recovery path for this very bug (#722 F2).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The scheduler builds a worktree for every initially-ready task SERIALLY, before any worker starts.
    /// That is not the settle-lock deadlock — there are no workers to block — but it is the same silence,
    /// and it sits on the path an operator walks while RECOVERING from the deadlock: on resume, a fan-in
    /// whose producers are already green IS an initially-ready task, so its worktree is built here rather
    /// than at dequeue. The same applies to the first tasks of any run and to every wave's entry tasks.
    /// <para>Without the announcement, a hang here leaves no <c>events.jsonl</c> row, no <c>task-started</c>,
    /// every task <c>pending</c> and <c>Run state: RUNNING</c> — byte-for-byte the silence #722 is about.</para>
    /// </summary>
    [Fact]
    public async Task ThePrePass_AnnouncesAWorktreeWaitBeforeBuildingIt()
    {
        PlanDefinition plan = Plan(Task("01-root"));

        var provider = new TipTrackingWorktreeProvider { GateCreateSegmentFor = "01-root" };
        var executor = new GatedRecordingExecutor("01-root");
        var observer = new SettleArrivalObserver();
        provider.WasAnnounced = observer.WasAnnounced;

        // Task.Run, unlike every other test here, and for the reason this test exists: the pre-pass runs in
        // RunAsync's SYNCHRONOUS prologue, before the first await, so a gated root blocks the CALLER — the
        // returned Task would never even be assigned, and the test would deadlock at this line with no
        // bound able to save it. Handing that blocking prologue to a pool thread is what lets the test
        // observe it.
        // Fully qualified: this file has `using static PlanFixtures`, so in EXPRESSION context the bare
        // name `Task` binds to the fixture's Task(string, params string[]) helper, not the BCL type.
        Task<RunReport> run = System.Threading.Tasks.Task.Run(
            () => Create(plan, executor, provider, observer, parallelism: 2)
                .RunAsync(plan, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        try
        {
            await ArrivesAsync(
                observer.WaitingOn("01-root"),
                "the PRE-PASS built a worktree without announcing it (#722 F2): a hang there is invisible "
                + "on every surface — no events.jsonl row, no task-started, every task pending and the run "
                + "reading as RUNNING — which is exactly the state this issue was filed about, on the path "
                + "an operator hits while recovering from it.");

            Assert.Equal(FreshSegmentOperation, observer.OperationFor("01-root"));

            await ArrivesAsync(
                provider.CreateSegmentEntered.Task,
                "the pre-pass never entered CreateSegment — the fixture never reached the condition.");

            Assert.True(
                provider.AnnouncedBeforeCreate["01-root"],
                "the pre-pass announced the wait AFTER starting the git it is about (#722 F2).");

            provider.ReleaseCreateSegment.TrySetResult();
            executor.Complete("01-root");

            RunReport report = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(report.AllSucceeded, string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        }
        finally
        {
            provider.ReleaseCreateSegment.TrySetResult();
            executor.CompleteAll();
            try { await run.WaitAsync(Bound, CancellationToken.None); } catch (Exception) { /* teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. The OTHER unbounded git on this seam (#722 F7).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A TRIPWIRE, and deliberately labelled as one. <see cref="IWorktreeProvider.CurrentPlanBranchTip"/> is
    /// a <c>git rev-parse</c> through the same runner with the same unbounded wait, and putting it back
    /// under the settle lock is the exact shape of REJECTED option B — capture the base under the gate,
    /// defer only the tree. That change would leave this issue's hang fully in place while every other test
    /// in this file still passed, because they gate only <c>CreateSegment</c>.
    /// <para>Today nothing reads the tip during a drain, so the gate is not exercised; the assertion message
    /// says so rather than implying coverage it does not have. The moment someone adds such a read under
    /// <c>_gate</c>, this test deadlocks the sibling and fails.</para>
    /// </summary>
    [Fact]
    public async Task AHungPlanTipRead_DoesNotBlockASiblingsSettle()
    {
        PlanDefinition plan = Plan(
            Task("01-p1"),
            Task("02-p2"),
            Task("03-fanin", "01-p1", "02-p2"),
            Task("04-independent"));

        var provider = new TipTrackingWorktreeProvider
        {
            GateCreateSegmentFor = "03-fanin",
            GateCurrentPlanBranchTip = true
        };
        var executor = new GatedRecordingExecutor("01-p1", "02-p2", "03-fanin", "04-independent");
        var observer = new SettleArrivalObserver();
        provider.WasAnnounced = observer.WasAnnounced;

        Task<RunReport> run = Create(plan, executor, provider, observer, parallelism: 4)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        try
        {
            executor.Complete("01-p1");
            executor.Complete("02-p2");

            await ArrivesAsync(
                provider.CreateSegmentEntered.Task,
                "the fan-in's CreateSegment was never entered — the fixture never reached the condition.");

            executor.Complete("04-independent");

            await ArrivesAsync(
                observer.Finished("04-independent"),
                "a sibling's settle did not land while BOTH a fan-in's worktree creation and every "
                + "plan-tip read were blocked (#722 F7). Some git call is running under the scheduler's "
                + "settle lock again — most likely a CurrentPlanBranchTip capture, which is rejected "
                + $"option B's shape and leaves the original hang in place. Tip reads so far: {provider.TipReads}.");

            provider.ReleasePlanTipRead.TrySetResult();
            provider.ReleaseCreateSegment.TrySetResult();
            executor.Complete("03-fanin");

            RunReport report = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(report.AllSucceeded, string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        }
        finally
        {
            provider.ReleasePlanTipRead.TrySetResult();
            provider.ReleaseCreateSegment.TrySetResult();
            executor.CompleteAll();
            try { await run.WaitAsync(Bound, CancellationToken.None); } catch (Exception) { /* teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. No rewind during a drain (#722 F6 — narrowed to what it actually checks).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reading the fan-in's base at DEQUEUE rather than at its producer's settle is sound only while the
    /// plan branch is gains-only during a drain. This checks TWO things, and it is worth being exact about
    /// which, because an earlier version of this comment claimed more:
    /// <list type="number">
    ///   <item>the scheduler calls <see cref="IWorktreeProvider.RewindPlanBranchTo"/> — the one non-monotone
    ///     writer in the interface — ZERO times while a drain is in flight; and</item>
    ///   <item>the bases handed to successive segment creations are non-decreasing in read order.</item>
    /// </list>
    ///
    /// <para><b>What it does NOT check.</b> The real gains-only property does not live in this fake. It
    /// lives in <c>GitWorktreeProvider.Integrate</c>'s <c>--no-commit</c> staging plus
    /// <c>RollbackMerge</c>'s reset to the pre-merge integration HEAD: a failed re-verify creates no commit,
    /// so there is nothing to rewind. This fake models the tip as a counter that only <c>Integrate</c>
    /// raises, so a change that COMMITTED the union before re-verify and had <c>RollbackMerge</c> reset one
    /// commit back would move the real branch backwards mid-drain and orphan a fan-in's base <b>with this
    /// test still green</b>. Proving that belongs in an integration test against a real repository, not in a
    /// counter here — a second implementation of the integration protocol living in a unit fake would drift
    /// from the real one and hand back false confidence, which is this issue's own failure genre.</para>
    ///
    /// <para>Clause 2 additionally cannot be mutated independently through production code today: the only
    /// writer that can lower this fake's tip is <c>RewindPlanBranchTo</c>, which clause 1 catches first. It
    /// is a tripwire for a future non-monotone writer, not a currently-exercised assertion.</para>
    /// </summary>
    [Fact]
    public async Task NoPlanBranchRewindOccursDuringADrain_AndBasesAreReadNonDecreasing()
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

        // Clause 1: no non-monotone writer ran at all during the drain.
        Assert.Empty(provider.RewoundTo);

        IReadOnlyList<(string TaskId, int Tip)> creates = provider.Creates();

        // Non-vacuity: several bases were read, and the branch really did advance between them — otherwise
        // "never went backwards" would be true of a tip that never moved.
        Assert.True(creates.Count >= 4, $"expected at least 4 CreateSegment calls, got {creates.Count}");
        Assert.True(
            creates[^1].Tip > creates[0].Tip,
            $"the plan tip never advanced across {creates.Count} segment creations, so this guard proves nothing");

        // Clause 2.
        for (int i = 1; i < creates.Count; i++)
        {
            Assert.True(
                creates[i].Tip >= creates[i - 1].Tip,
                $"a base was read BACKWARDS during the drain: '{creates[i - 1].TaskId}' read tip "
                + $"{creates[i - 1].Tip} and '{creates[i].TaskId}' then read tip {creates[i].Tip}. A base read "
                + "at dequeue is only sound while the branch is gains-only in flight (#722).");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. Anti-tautology — the base IS the tree, read at dequeue, after a sibling's commit landed.
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
        provider.WasAnnounced = observer.WasAnnounced;

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
