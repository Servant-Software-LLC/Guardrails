using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler behavior for scope-based concurrency (M2, Plan 05 §4.2/§4.3/§10).
/// The rewired scheduler uses <see cref="ScopeLock"/> in place of <see cref="WorkspaceLock"/>:
/// independent tasks with disjoint write-scopes run concurrently; overlapping or universal
/// scopes serialize. <c>maxParallelism</c> caps the worker count independently.
///
/// Authored BEFORE the feature exists: <c>TaskNode.WriteScope</c> does not yet exist,
/// so this suite will not compile against current code — the intended failure.
/// </summary>
public sealed class ScopeSchedulerTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeJournal : ISchedulerJournal
    {
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<string> Blocked { get; } = [];

        public JournalTaskStatus StatusOf(string taskId) =>
            Succeeded.Contains(taskId) ? JournalTaskStatus.Succeeded : JournalTaskStatus.Pending;

        public void MarkBlocked(string taskId) => Blocked.Add(taskId);
    }

    /// <summary>
    /// Records each task's execution window (global sequence start/end) and peak concurrency.
    /// Gates are optional: when <see cref="Gated"/> the task waits on a per-task TCS, letting
    /// the test control exactly when each finishes.
    /// </summary>
    private sealed class WindowRecordingExecutor : ITaskExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates =
            new(StringComparer.Ordinal);
        private int _sequence;
        private int _live;

        public bool Gated { get; init; }
        public ConcurrentQueue<string> Started { get; } = [];
        public ConcurrentDictionary<string, (int StartSeq, int EndSeq)> Windows { get; } =
            new(StringComparer.Ordinal);
        public int MaxObservedConcurrency { get; private set; }

        public void Complete(string id) => _gates.GetOrAdd(id, NewGate()).TrySetResult();

        public async Task<TaskResult> ExecuteAsync(TaskNode task, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            int startSeq = Interlocked.Increment(ref _sequence);
            int live = Interlocked.Increment(ref _live);
            lock (Started) { MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, live); }

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

            int endSeq = Interlocked.Increment(ref _sequence);
            Windows[task.Id] = (startSeq, endSeq);

            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success"
            };
        }

        private static TaskCompletionSource NewGate() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ── Fixture helpers ────────────────────────────────────────────────────────

    /// <summary>Creates a script-action task with the given write scope and no DAG edges by default.</summary>
    private static TaskNode ScopedTask(string id, string[]? scope, params string[] dependsOn) =>
        Task(id, dependsOn) with { WriteScope = scope };

    private static string[] Narrow(params string[] globs) => globs;
    private static string[] Universal => ["**"];

    private static Scheduler Create(
        PlanDefinition plan, WindowRecordingExecutor executor, int parallelism = 8) =>
        new(plan, executor, new FakeJournal(), IRunObserver.Null, parallelism);

    // ── Concurrency: disjoint scopes run in parallel ───────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task ThreeDisjointScopeTasks_HaveOverlappingExecutionWindows()
    {
        // Three mutually-disjoint-scope tasks with no DAG edges must run concurrently.
        // Proof: gate all three; spin until all three are simultaneously in flight before
        // releasing — that proves their execution windows overlap.
        var executor = new WindowRecordingExecutor { Gated = true };
        PlanDefinition plan = Plan(
            ScopedTask("01-alpha", Narrow("src/Alpha/**")),
            ScopedTask("02-beta",  Narrow("src/Beta/**")),
            ScopedTask("03-gamma", Narrow("src/Gamma/**")));

        var run = Create(plan, executor).RunAsync(plan, TestContext.Current.CancellationToken);

        // All three must start before any finishes — that proves overlapping windows.
        while (executor.MaxObservedConcurrency < 3)
        {
            await System.Threading.Tasks.Task.Yield();
        }

        executor.Complete("01-alpha");
        executor.Complete("02-beta");
        executor.Complete("03-gamma");

        RunReport report = await run;
        Assert.True(report.AllSucceeded);
        Assert.Equal(3, executor.MaxObservedConcurrency);
    }

    // ── Serialization: overlapping scopes do not run concurrently ─────────────

    [Fact]
    public async System.Threading.Tasks.Task TwoUniversalScopeTasks_HaveNonOverlappingExecutionWindows()
    {
        // Two independent universal-scope tasks must serialize — the WorkspaceLock special case.
        var executor = new WindowRecordingExecutor();
        PlanDefinition plan = Plan(
            ScopedTask("01-broad", Universal),
            ScopedTask("02-broad", Universal));

        RunReport report = await Create(plan, executor)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Equal(1, executor.MaxObservedConcurrency);

        var w1 = executor.Windows["01-broad"];
        var w2 = executor.Windows["02-broad"];
        bool overlapping = w1.StartSeq < w2.EndSeq && w2.StartSeq < w1.EndSeq;
        Assert.False(overlapping,
            "universal-scope tasks must serialize — execution windows must not overlap");
    }

    [Fact]
    public async System.Threading.Tasks.Task TwoOverlappingNarrowScopeTasks_HaveNonOverlappingExecutionWindows()
    {
        // Two independent tasks whose scopes overlap (but are not universal) must also serialize.
        var executor = new WindowRecordingExecutor();
        PlanDefinition plan = Plan(
            ScopedTask("01-parent", Narrow("src/Feature/**")),
            ScopedTask("02-child",  Narrow("src/Feature/Thing.cs"))); // overlaps 01-parent

        RunReport report = await Create(plan, executor)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Equal(1, executor.MaxObservedConcurrency);

        var w1 = executor.Windows["01-parent"];
        var w2 = executor.Windows["02-child"];
        bool overlapping = w1.StartSeq < w2.EndSeq && w2.StartSeq < w1.EndSeq;
        Assert.False(overlapping,
            "overlapping-scope tasks must serialize — execution windows must not overlap");
    }

    // ── maxParallelism caps workers independently of scope ─────────────────────

    [Fact]
    public async System.Threading.Tasks.Task MaxParallelism_CapsWorkerCount_IndependentOfScope()
    {
        // Six mutually-disjoint-scope tasks, parallelism 2: scope alone would permit all 6
        // at once, but maxParallelism clamps the worker count to 2.
        PlanDefinition plan = Plan(
            ScopedTask("01-t", Narrow("src/A/**")),
            ScopedTask("02-t", Narrow("src/B/**")),
            ScopedTask("03-t", Narrow("src/C/**")),
            ScopedTask("04-t", Narrow("src/D/**")),
            ScopedTask("05-t", Narrow("src/E/**")),
            ScopedTask("06-t", Narrow("src/F/**")));
        var executor = new WindowRecordingExecutor { Gated = true };

        var run = Create(plan, executor, parallelism: 2)
            .RunAsync(plan, TestContext.Current.CancellationToken);

        // Drain: release tasks as they start, one at a time, mirroring MaxParallelism_IsACeiling.
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
            $"maxParallelism=2 was exceeded: observed {executor.MaxObservedConcurrency} concurrent workers");
    }
}
