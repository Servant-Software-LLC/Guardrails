using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// Scheduler-level unit tests for the resume definition-drift halt (SSOT §7.2, issue #274 Part A),
/// exercised through the JOURNAL path alone (serial mode, no worktree provider): a fake
/// <see cref="ISchedulerJournal"/> scripts each task's recorded status + <c>TaskDefinitionHash</c>, and
/// a <see cref="RecordingExecutor"/> proves whether anything was actually scheduled. The "current" hash
/// is deterministic for a <see cref="PlanFixtures"/> task (its definition files do not exist on disk), so
/// the test controls MATCH vs DRIFT purely via the recorded hash it seeds.
/// </summary>
public sealed class SchedulerDefinitionDriftTests
{
    private sealed class FakeJournal : ISchedulerJournal
    {
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);
        public HashSet<string> Succeeded { get; } = new(StringComparer.Ordinal);

        public void MarkSucceeded(string taskId, string? recordedHash)
        {
            Succeeded.Add(taskId);
            _hashes[taskId] = recordedHash;
        }

        public JournalTaskStatus StatusOf(string taskId) =>
            Succeeded.Contains(taskId) ? JournalTaskStatus.Succeeded : JournalTaskStatus.Pending;

        public string? RecordedDefinitionHash(string taskId) =>
            _hashes.TryGetValue(taskId, out string? h) ? h : null;

        public void MarkBlocked(string taskId) { }
    }

    private static async Task<(RunReport Report, RecordingExecutor Executor)> RunAsync(
        PlanDefinition plan, FakeJournal journal)
    {
        var executor = new RecordingExecutor();
        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (report, executor);
    }

    [Fact]
    public async Task RecordedHashMatchesCurrent_ResumesSkipped_NoDrift()
    {
        TaskNode a = Task("01-a");
        TaskNode b = Task("02-b", "01-a");
        PlanDefinition plan = Plan(a, b);

        var journal = new FakeJournal();
        journal.MarkSucceeded("01-a", TaskDefinitionHash.Compute(a));
        journal.MarkSucceeded("02-b", TaskDefinitionHash.Compute(b));

        (RunReport report, RecordingExecutor executor) = await RunAsync(plan, journal);

        Assert.Null(report.DefinitionDrift);
        Assert.True(report.AllSucceeded);
        Assert.Empty(executor.Started); // both pre-settled-green → nothing scheduled.
    }

    [Fact]
    public async Task RecordedHashDiffersFromCurrent_HaltsWithDefinitionDrift_SchedulesNothing()
    {
        TaskNode a = Task("01-a");
        TaskNode b = Task("02-b", "01-a");
        PlanDefinition plan = Plan(a, b);

        var journal = new FakeJournal();
        journal.MarkSucceeded("01-a", "sha256:stale-does-not-match"); // drifted
        journal.MarkSucceeded("02-b", TaskDefinitionHash.Compute(b)); // unchanged

        (RunReport report, RecordingExecutor executor) = await RunAsync(plan, journal);

        Assert.NotNull(report.DefinitionDrift);
        DriftedTask drifted = Assert.Single(report.DefinitionDrift!.Tasks);
        Assert.Equal("01-a", drifted.TaskId);
        Assert.Equal("sha256:stale-does-not-match", drifted.OldHash);
        Assert.Equal(TaskDefinitionHash.Compute(a), drifted.NewHash);
        // The transitive-descendant set is reported (a changed producer can change a consumer's inputs).
        Assert.Contains("02-b", drifted.Dependents);
        // The whole point: schedule NOTHING on drift.
        Assert.Empty(executor.Started);
        Assert.False(report.AllSucceeded);
    }

    [Fact]
    public async Task RecordedHashAbsent_TreatedAsUnchanged_ResumesSkipped()
    {
        // The backward-compat case: a pre-upgrade journal has a succeeded task with NO recorded hash.
        TaskNode a = Task("01-a");
        PlanDefinition plan = Plan(a);

        var journal = new FakeJournal();
        journal.MarkSucceeded("01-a", recordedHash: null);

        (RunReport report, RecordingExecutor executor) = await RunAsync(plan, journal);

        Assert.Null(report.DefinitionDrift); // "unknown → assume unchanged" — no re-run storm on upgrade.
        Assert.True(report.AllSucceeded);
        Assert.Empty(executor.Started);
    }

    [Fact]
    public async Task NonSucceededTask_IsNotDriftChecked_AndStillRuns()
    {
        // A pending task is not a pre-settle candidate, so no drift check applies and it runs normally.
        TaskNode a = Task("01-a");
        PlanDefinition plan = Plan(a);
        var journal = new FakeJournal(); // nothing succeeded

        (RunReport report, RecordingExecutor executor) = await RunAsync(plan, journal);

        Assert.Null(report.DefinitionDrift);
        Assert.Contains("01-a", executor.Started);
    }

    [Fact]
    public async Task DriftReport_IncludesFullTransitiveDescendantClosure()
    {
        // Diamond: 01 → {02, 03} → 04. Drift on the root reports the whole downstream closure.
        TaskNode root = Task("01-root");
        TaskNode left = Task("02-left", "01-root");
        TaskNode right = Task("03-right", "01-root");
        TaskNode sink = Task("04-sink", "02-left", "03-right");
        PlanDefinition plan = Plan(root, left, right, sink);

        var journal = new FakeJournal();
        journal.MarkSucceeded("01-root", "sha256:stale");
        journal.MarkSucceeded("02-left", TaskDefinitionHash.Compute(left));
        journal.MarkSucceeded("03-right", TaskDefinitionHash.Compute(right));
        journal.MarkSucceeded("04-sink", TaskDefinitionHash.Compute(sink));

        (RunReport report, _) = await RunAsync(plan, journal);

        DriftedTask drifted = Assert.Single(report.DefinitionDrift!.Tasks);
        Assert.Equal(new[] { "02-left", "03-right", "04-sink" }, drifted.Dependents.OrderBy(x => x).ToArray());
    }
}
