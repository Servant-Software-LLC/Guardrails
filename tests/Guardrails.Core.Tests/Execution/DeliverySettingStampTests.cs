using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #710: every <see cref="RunReport"/> carries the resolved delivery setting. The undelivered-work banner and
/// <c>delivery.reason</c> derive their cause from <see cref="RunReport.MergeOnSuccess"/>, and that member defaults to
/// <c>true</c>, so a report path that never stamped it reads "on, held by the interlock" on a run that had delivery
/// OFF: the false statement #710 was filed for.
/// <para>
/// <b>Why these rows exist.</b> The first real-Scheduler pins were both WAVED, and plan 40 was FLAT when #710
/// happened. A review mutation deleted the stamp from <c>Scheduler.BuildReport</c> and re-added it only to the
/// waved run-end report, and every targeted test stayed green. These rows drive a flat plan through the real
/// Scheduler, on the green report the undelivered surfaces read and on an aborted report, so "every report
/// passes through BuildReport" is pinned by a test rather than only by a comment.
/// </para>
/// </summary>
public sealed class DeliverySettingStampTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A flat, one-task plan whose config is exactly what RunCommand writes for <c>--no-merge-on-success</c>.</summary>
    private static PlanDefinition FlatPlanTurnedOff(WavePlanBuilder b)
    {
        b.FlatTask("01-a");
        PlanLoadResult result = b.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        PlanDefinition loaded = result.Plan!;
        Assert.False(loaded.IsWaved);
        return loaded with
        {
            Config = loaded.Config with { MergeOnSuccess = false, MergeOnSuccessSource = MergeOnSuccessSource.Flag }
        };
    }

    private static Scheduler NewScheduler(PlanDefinition plan, ITaskExecutor exec, RunJournal journal) =>
        new(plan, exec, journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: IRunObserver.Null, maxParallelism: 4);

    /// <summary>Records a TASK-boundary <c>proceeded-best-guess</c> while the task runs, the boundary plan 40's decision had.</summary>
    private sealed class BestGuessingExecutor(RunJournal journal) : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            journal.RecordDecision(new DecisionEntry
            {
                Boundary = "task",
                Policy = "auto",
                Decision = DecisionTokens.ProceededBestGuess,
                Subject = task.Id,
                Headline = $"best-guessed while running {task.Id}"
            });

            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
        }
    }

    /// <summary>Faults the run, so the Scheduler returns its ABORTED report instead of finalizing.</summary>
    private sealed class ThrowingExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("scripted executor fault");
    }

    [Fact]
    public async Task AFlatRunTurnedOffThatRecordedATaskBestGuess_CarriesTheSettingBesideTheDecision()
    {
        using var b = new WavePlanBuilder();
        PlanDefinition plan = FlatPlanTurnedOff(b);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, new BestGuessingExecutor(journal), journal).RunAsync(plan, Ct);

        // Both causes are true: plan 40's shape, on the report the undelivered surfaces read.
        Assert.True(report.WhollyGreenButUndelivered, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Equal("task", report.DeliverySuppressingDecision?.Boundary);

        Assert.False(report.MergeOnSuccess);
        Assert.Equal(MergeOnSuccessSource.Flag, report.MergeOnSuccessSource);
    }

    [Fact]
    public async Task AnAbortedFlatRun_StillCarriesTheSetting()
    {
        using var b = new WavePlanBuilder();
        PlanDefinition plan = FlatPlanTurnedOff(b);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, new ThrowingExecutor(), journal).RunAsync(plan, Ct);

        // An aborted report never reaches Finalize, so only BuildReport can have stamped these.
        Assert.True(report.Aborted);
        Assert.False(report.MergeOnSuccess);
        Assert.Equal(MergeOnSuccessSource.Flag, report.MergeOnSuccessSource);
    }
}
