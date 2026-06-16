using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler behavior for the per-run cost cap (SSOT §2 / plan 04). Before a worker launches a
/// task's attempt, the scheduler compares the journal's cumulative cost to
/// <see cref="RunConfig.MaxCostUsd"/>: at or over the cap it does NOT launch the task — it settles
/// the task <c>needs-human</c> (reason "cost cap reached") and blocks its transitive dependents via
/// the existing halt path. Below the cap (or with no cap) every task runs normally. Uses the
/// in-memory fake-executor/fake-journal style of <see cref="SchedulerTests"/>; the journal fake
/// preloads a cumulative cost so a test can stand the run "already $X spent".
///
/// Authored BEFORE the feature exists: it references <c>RunConfig.MaxCostUsd</c> and expects the
/// scheduler to read cumulative cost from the journal (see <see cref="FakeJournal.CurrentCostUsd"/>),
/// neither of which exists yet. The suite will not compile / these tests will fail against current
/// code — the intended failure that proves the behavior is unbuilt.
/// </summary>
public sealed class CostCapSchedulerTests
{
    /// <summary>
    /// Fake journal: everything pending unless seeded; records blocked calls; and exposes a
    /// preloadable cumulative cost. The implementation task adds <c>CurrentCostUsd()</c> to
    /// <see cref="ISchedulerJournal"/> (a default interface method, so existing fakes keep
    /// compiling) and has the scheduler read it before each launch. This fake overrides it to
    /// return the preloaded value, modelling "the run has already spent this much".
    /// </summary>
    private sealed class FakeJournal : ISchedulerJournal
    {
        public decimal PreloadedCostUsd { get; init; }
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<string> Blocked { get; } = [];

        public JournalTaskStatus StatusOf(string taskId) =>
            Succeeded.Contains(taskId) ? JournalTaskStatus.Succeeded : JournalTaskStatus.Pending;

        public void MarkBlocked(string taskId) => Blocked.Add(taskId);

        /// <summary>The run's cumulative journaled cost the scheduler reads to enforce the cap.</summary>
        public decimal CurrentCostUsd() => PreloadedCostUsd;
    }

    /// <summary>Fake executor: records start order and reports every launched task succeeded.</summary>
    private sealed class RecordingExecutor : ITaskExecutor
    {
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            // Fully qualified: `using static PlanFixtures` brings a `Task(...)` method into scope,
            // which would otherwise shadow the System.Threading.Tasks.Task type here.
            return System.Threading.Tasks.Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success"
            });
        }
    }

    private static Scheduler Create(PlanDefinition plan, RecordingExecutor executor, FakeJournal journal) =>
        new(plan, executor, journal, IRunObserver.Null, maxParallelism: 4);

    private static PlanDefinition WithCap(PlanDefinition plan, decimal? cap) =>
        plan with { Config = plan.Config with { MaxCostUsd = cap } };

    [Fact]
    public async Task CapAlreadyExceeded_TaskNotLaunched_SettlesNeedsHuman_AndBlocksDependents()
    {
        PlanDefinition plan = WithCap(
            Plan(
                Task("01-capped"),
                Task("02-dependent", "01-capped")),
            cap: 1.00m);
        var executor = new RecordingExecutor();
        var journal = new FakeJournal { PreloadedCostUsd = 5.00m }; // already over the $1.00 cap

        RunReport report = await Create(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        // The over-budget task is never launched; it settles needs-human with the cost-cap reason.
        TaskResult capped = Result(report, "01-capped");
        Assert.Equal(TaskOutcome.NeedsHuman, capped.Outcome);
        Assert.Contains("cost cap reached", capped.Summary);
        Assert.DoesNotContain("01-capped", executor.Started);

        // Its dependent blocks via the existing halt path and likewise never runs.
        Assert.Equal(TaskOutcome.Blocked, Result(report, "02-dependent").Outcome);
        Assert.Contains("02-dependent", journal.Blocked);
        Assert.DoesNotContain("02-dependent", executor.Started);

        Assert.False(report.AllSucceeded);
    }

    [Fact]
    public async Task CostBelowCap_AllTasksRunNormally()
    {
        PlanDefinition plan = WithCap(
            Plan(
                Task("01-root"),
                Task("02-leaf", "01-root")),
            cap: 10.00m);
        var executor = new RecordingExecutor();
        var journal = new FakeJournal { PreloadedCostUsd = 0.50m }; // comfortably below the $10.00 cap

        RunReport report = await Create(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Contains("01-root", executor.Started);
        Assert.Contains("02-leaf", executor.Started);
    }

    [Fact]
    public async Task NoCap_IgnoresJournaledCost_AllTasksRunNormally()
    {
        // Absent maxCostUsd ⇒ no cap, regardless of how much has been spent (regression-safe).
        PlanDefinition plan = WithCap(
            Plan(
                Task("01-root"),
                Task("02-leaf", "01-root")),
            cap: null);
        var executor = new RecordingExecutor();
        var journal = new FakeJournal { PreloadedCostUsd = 999m };

        RunReport report = await Create(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded);
        Assert.Contains("01-root", executor.Started);
        Assert.Contains("02-leaf", executor.Started);
    }

    private static TaskResult Result(RunReport report, string id) =>
        report.Tasks.Single(t => t.TaskId == id);
}
