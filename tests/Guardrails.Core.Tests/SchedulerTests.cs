using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler unit tests with a fake executor (no processes, no journal file).
/// Determinism comes from TaskCompletionSource-gated fakes, never from Task.Delay.
/// </summary>
public sealed class SchedulerTests
{
    /// <summary>Fake journal: everything pending unless seeded; records blocked calls.</summary>
    private sealed class FakeJournal : ISchedulerJournal
    {
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<string> Blocked { get; } = [];

        public JournalTaskStatus StatusOf(string taskId) =>
            Succeeded.Contains(taskId) ? JournalTaskStatus.Succeeded : JournalTaskStatus.Pending;

        public void MarkBlocked(string taskId) => Blocked.Add(taskId);
    }

    /// <summary>
    /// Fake executor: per-task scripted outcomes; optionally gated so tests control
    /// exactly when each task finishes. Records start order and live concurrency.
    /// </summary>
    private sealed class FakeExecutor : ITaskExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _throwing = new(StringComparer.Ordinal);
        private int _live;

        public ConcurrentQueue<string> Started { get; } = [];
        public int MaxObservedConcurrency { get; private set; }
        public bool Gated { get; init; }

        public void FailTask(string id) => _failing.Add(id);

        /// <summary>Make <paramref name="id"/>'s execution throw an unexpected (non-cancellation) exception.</summary>
        public void ThrowOnTask(string id, Exception ex) => _throwing[id] = ex;

        public void Complete(string id) => _gates.GetOrAdd(id, NewGate()).TrySetResult();

        public async Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            int now = Interlocked.Increment(ref _live);
            lock (Started)
            {
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, now);
            }

            try
            {
                if (Gated)
                {
                    await _gates.GetOrAdd(task.Id, NewGate()).Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }

            if (_throwing.TryGetValue(task.Id, out Exception? boom))
            {
                throw boom;
            }

            bool fails = _failing.Contains(task.Id);
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = fails ? TaskOutcome.GuardrailFailed : TaskOutcome.Succeeded,
                Summary = fails ? "scripted failure" : "scripted success"
            };
        }

        private static TaskCompletionSource NewGate() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Scheduler Create(PlanDefinition plan, FakeExecutor executor, FakeJournal journal, int parallelism = 4) =>
        new(plan, executor, journal, observer: IRunObserver.Null, maxParallelism: parallelism);

    [Fact]
    public async Task TopologicalOrder_DependencyAlwaysStartsBeforeDependent()
    {
        PlanDefinition plan = Plan(
            Task("01-root"),
            Task("02-mid", "01-root"),
            Task("03-leaf", "02-mid"));
        var executor = new FakeExecutor();
        RunReport report = await Create(plan, executor, new FakeJournal()).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        string[] order = [.. executor.Started];
        Assert.True(Array.IndexOf(order, "01-root") < Array.IndexOf(order, "02-mid"));
        Assert.True(Array.IndexOf(order, "02-mid") < Array.IndexOf(order, "03-leaf"));
    }

    [Fact]
    public async Task MaxParallelism_IsACeiling()
    {
        // Six independent tasks, parallelism 2, gated: at most 2 run at once.
        TaskNode[] tasks = [.. Enumerable.Range(1, 6).Select(i => Task($"{i:00}-t"))];
        PlanDefinition plan = Plan(tasks);
        var executor = new FakeExecutor { Gated = true };

        Task<RunReport> run = Create(plan, executor, new FakeJournal(), parallelism: 2).RunAsync(plan, TestContext.Current.CancellationToken);

        // Drain: release tasks as they appear until all six finish.
        var released = new HashSet<string>(StringComparer.Ordinal);
        while (released.Count < 6)
        {
            if (executor.Started.TryDequeue(out string? id))
            {
                released.Add(id);
                executor.Complete(id);
            }
            else
            {
                await System.Threading.Tasks.Task.Yield();
            }
        }

        RunReport report = await run;
        Assert.True(report.AllSucceeded);
        Assert.True(executor.MaxObservedConcurrency <= 2,
            $"observed concurrency {executor.MaxObservedConcurrency} exceeded the ceiling");
    }

    [Fact]
    public async Task Failure_BlocksExactlyTheTransitiveClosure_IndependentBranchFinishes()
    {
        PlanDefinition plan = Plan(
            Task("01-fail"),
            Task("02-child", "01-fail"),
            Task("03-grandchild", "02-child"),
            Task("04-independent"),
            Task("05-independent-child", "04-independent"));
        var executor = new FakeExecutor();
        executor.FailTask("01-fail");
        var journal = new FakeJournal();

        RunReport report = await Create(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.GuardrailFailed, Result(report, "01-fail").Outcome);
        Assert.Equal(TaskOutcome.Blocked, Result(report, "02-child").Outcome);
        Assert.Equal(TaskOutcome.Blocked, Result(report, "03-grandchild").Outcome);
        Assert.Equal(TaskOutcome.Succeeded, Result(report, "04-independent").Outcome);
        Assert.Equal(TaskOutcome.Succeeded, Result(report, "05-independent-child").Outcome);
        Assert.Equal(new HashSet<string> { "02-child", "03-grandchild" }, journal.Blocked.ToHashSet());
        // Blocked tasks never started.
        Assert.DoesNotContain("02-child", executor.Started);
        Assert.DoesNotContain("03-grandchild", executor.Started);
    }

    [Fact]
    public async Task DiamondJoin_BlockedWhenEitherParentFails_NotDoubleCounted()
    {
        PlanDefinition plan = Plan(
            Task("01-root"),
            Task("02-left", "01-root"),
            Task("03-right", "01-root"),
            Task("04-join", "02-left", "03-right"));
        var executor = new FakeExecutor();
        executor.FailTask("02-left");

        RunReport report = await Create(plan, executor, new FakeJournal()).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.Blocked, Result(report, "04-join").Outcome);
        Assert.Equal(TaskOutcome.Succeeded, Result(report, "03-right").Outcome);
        Assert.Equal(4, report.Tasks.Count);
    }

    [Fact]
    public async Task Resume_PreSettledSucceeded_SkippedAndUnlocksDependents()
    {
        PlanDefinition plan = Plan(
            Task("01-done"),
            Task("02-next", "01-done"));
        var executor = new FakeExecutor();
        var journal = new FakeJournal();
        journal.Succeeded.Add("01-done");

        RunReport report = await Create(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Equal(TaskOutcome.Skipped, Result(report, "01-done").Outcome);
        Assert.Equal(TaskOutcome.Succeeded, Result(report, "02-next").Outcome);
        Assert.DoesNotContain("01-done", executor.Started); // never re-ran
    }

    [Fact]
    public async Task Cancellation_DrainsCleanly_UnstartedTasksReportedCancelled()
    {
        PlanDefinition plan = Plan(
            Task("01-running"),
            Task("02-waiting", "01-running"));
        var executor = new FakeExecutor { Gated = true };
        using var cts = new CancellationTokenSource();

        Task<RunReport> run = Create(plan, executor, new FakeJournal()).RunAsync(plan, cts.Token);

        // Wait until the first task is genuinely in flight, then cancel.
        while (executor.Started.IsEmpty)
        {
            await System.Threading.Tasks.Task.Yield();
        }

        cts.Cancel();
        RunReport report = await run;

        Assert.True(report.Cancelled);
        Assert.Equal(TaskOutcome.Cancelled, Result(report, "02-waiting").Outcome);
        Assert.DoesNotContain("02-waiting", executor.Started);
    }

    [Fact]
    public async Task UnexpectedExecutorThrow_TerminatesTheRun_AndReturnsAnAbortedReport()
    {
        // Regression for the worker loop catching ONLY OperationCanceledException: a task whose
        // executor throws any OTHER exception used to escape the worker, leaving Remaining never
        // decremented and the channel never completed, so sibling workers blocked forever and the
        // whole run HUNG. The fix records the fault, drains siblings, and (issue #150) surfaces it as
        // an ABORTED RunReport — an honest halt the CLI renders + exits non-zero, NOT an unhandled
        // re-throw that escaped to the CLI as a raw stack-trace headline. A bounded timeout makes a
        // regression fail as a timeout, not a suite hang.
        PlanDefinition plan = Plan(
            Task("01-boom"),
            Task("02-dependent", "01-boom"),
            Task("03-independent"));
        var executor = new FakeExecutor();
        var boom = new InvalidOperationException("no interpreter registered for '.qux'");
        executor.ThrowOnTask("01-boom", boom);

        // Bind the run to a timeout so a hung (regressed) scheduler trips the deadline rather
        // than blocking the test host. The fix completes well within this window.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        Task<RunReport> run = Create(plan, executor, new FakeJournal()).RunAsync(plan, deadline.Token);
        Task finished = await System.Threading.Tasks.Task.WhenAny(run, System.Threading.Tasks.Task.Delay(Timeout.Infinite, deadline.Token));

        Assert.False(deadline.IsCancellationRequested, "the run hung — the scheduler did not terminate after an unexpected executor throw");
        Assert.Same(run, finished);

        // The fault is surfaced as an aborted report (honest halt), NOT thrown unhandled.
        RunReport report = await run;
        Assert.True(report.Aborted, "an unexpected executor throw must produce an aborted report (issue #150)");
        Assert.NotNull(report.Abort);
        // The full fault text is preserved for the logs (a dev tool keeps the detail).
        Assert.Contains("no interpreter registered for '.qux'", report.Abort!.Detail);
        // A headline + remedy exist for the console one-liner.
        Assert.False(string.IsNullOrWhiteSpace(report.Abort.Headline));
        Assert.False(string.IsNullOrWhiteSpace(report.Abort.Remedy));
    }

    [Fact]
    public async Task CycleInGraph_ThrowsWithPath()
    {
        PlanDefinition plan = Plan(
            Task("01-a", "02-b"),
            Task("02-b", "01-a"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(plan, new FakeExecutor(), new FakeJournal()).RunAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("01-a", ex.Message);
        Assert.Contains("02-b", ex.Message);
    }

    private static TaskResult Result(RunReport report, string id) =>
        report.Tasks.Single(t => t.TaskId == id);
}
