using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #379: on a WAVED plan the live run table collapses each COMPLETED wave's per-task rows to a
/// single summary line, keeping the active (and not-yet-started) waves' full rows so the live work stays
/// on-screen. These pin the pure row-planning seam <see cref="LiveTableRows.Plan"/> that drives the
/// (re)build of the Spectre table — the live table itself never renders in a non-interactive test, so the
/// public pure function is the test seam (same convention as <see cref="LiveRunObserver.StatusMarkup"/>).
/// Covers: (a) a completed wave → one summary row; (b) the active wave → full rows; (c) a flat plan is
/// byte-identical to before; (d) the <c>--all-tasks</c> opt-out shows every row.
/// </summary>
public sealed class CollapseCompletedWavesTests
{
    private static GuardrailDefinition FakeGuardrail(string dir) => new()
    {
        Name = "01-check",
        Path = $"{dir}/guardrails/01-check.sh",
        Kind = ActionKind.Script
    };

    private static ActionDefinition FakeAction(string dir) => new()
    {
        Path = $"{dir}/action.sh",
        Kind = ActionKind.Script
    };

    private static TaskNode Task(string waveDir, string folder)
    {
        string dir = $"/fake/plan/{waveDir}/tasks/{folder}";
        return new TaskNode
        {
            Id = $"{waveDir}/{folder}",
            WaveDir = waveDir,
            Directory = dir,
            Description = $"fixture — {folder}",
            Action = FakeAction(dir),
            Guardrails = [FakeGuardrail(dir)]
        };
    }

    private static TaskNode FlatTask(string folder)
    {
        string dir = $"/fake/plan/tasks/{folder}";
        return new TaskNode
        {
            Id = folder,
            Directory = dir,
            Description = $"fixture — {folder}",
            Action = FakeAction(dir),
            Guardrails = [FakeGuardrail(dir)]
        };
    }

    private static WaveNode Wave(string dir, int number, params TaskNode[] tasks) => new()
    {
        Dir = dir,
        Number = number,
        Slug = dir.Split('-', 3)[2],
        Directory = $"/fake/plan/{dir}",
        Tasks = tasks
    };

    /// <summary>
    /// A three-wave plan: wave-01 (2 tasks), wave-02 (3 tasks), wave-03 (2 tasks). Flattened Tasks mirror
    /// the wave order, exactly as the loader produces (SSOT §14.2).
    /// </summary>
    private static (IReadOnlyList<TaskNode> Tasks, IReadOnlyList<WaveNode> Waves) ThreeWavePlan()
    {
        WaveNode w1 = Wave("wave-01-renderer-core", 1, Task("wave-01-renderer-core", "01-a"), Task("wave-01-renderer-core", "02-b"));
        WaveNode w2 = Wave("wave-02-classify", 2,
            Task("wave-02-classify", "01-a"), Task("wave-02-classify", "02-b"), Task("wave-02-classify", "03-c"));
        WaveNode w3 = Wave("wave-03-escalate", 3, Task("wave-03-escalate", "01-a"), Task("wave-03-escalate", "02-b"));

        IReadOnlyList<TaskNode> flat = [.. w1.Tasks, .. w2.Tasks, .. w3.Tasks];
        return (flat, [w1, w2, w3]);
    }

    private static IReadOnlySet<string> Completed(params string[] dirs) => new HashSet<string>(dirs, StringComparer.Ordinal);

    // (a) A completed wave collapses to exactly one summary row (with its task count), and NONE of that
    //     wave's task rows survive.
    [Fact]
    public void CompletedWave_CollapsesToOneSummaryRow()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(tasks, waves, Completed("wave-01-renderer-core"), showAllTasks: false);

        WaveSummaryLiveRow[] summaries = rows.OfType<WaveSummaryLiveRow>().ToArray();
        Assert.Single(summaries);
        Assert.Equal("wave-01-renderer-core", summaries[0].WaveDir);
        Assert.Equal(2, summaries[0].TaskCount); // "2/2 tasks green" in the rendered line

        // No wave-01 task keeps a per-task row.
        Assert.DoesNotContain(rows.OfType<TaskLiveRow>(), r => r.TaskId.StartsWith("wave-01-renderer-core/", StringComparison.Ordinal));
    }

    // (b) The active (and not-yet-started) waves keep their FULL per-task rows.
    [Fact]
    public void ActiveWave_KeepsFullTaskRows()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        // wave-01 complete; wave-02 active; wave-03 not started.
        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(tasks, waves, Completed("wave-01-renderer-core"), showAllTasks: false);

        string[] taskIds = rows.OfType<TaskLiveRow>().Select(r => r.TaskId).ToArray();

        // The active wave's three rows and the pending wave's two rows are all present, in order.
        Assert.Equal(
            [
                "wave-02-classify/01-a", "wave-02-classify/02-b", "wave-02-classify/03-c",
                "wave-03-escalate/01-a", "wave-03-escalate/02-b",
            ],
            taskIds);

        // Ordering: the wave-01 summary comes first, before any surviving task row.
        Assert.IsType<WaveSummaryLiveRow>(rows[0]);
        Assert.IsType<TaskLiveRow>(rows[1]);
    }

    // Multiple completed waves each collapse independently; only the single active wave shows task rows.
    [Fact]
    public void TwoCompletedWaves_BothCollapse_ActiveWaveKeepsRows()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(
            tasks, waves, Completed("wave-01-renderer-core", "wave-02-classify"), showAllTasks: false);

        Assert.Equal(
            ["wave-01-renderer-core", "wave-02-classify"],
            rows.OfType<WaveSummaryLiveRow>().Select(r => r.WaveDir).ToArray());
        Assert.Equal(
            ["wave-03-escalate/01-a", "wave-03-escalate/02-b"],
            rows.OfType<TaskLiveRow>().Select(r => r.TaskId).ToArray());
    }

    // (c) A NON-WAVED (flat) plan is byte-identical to before: one task row each, in order, no summary.
    [Fact]
    public void FlatPlan_RendersEveryTaskRow_NoSummary_Unchanged()
    {
        IReadOnlyList<TaskNode> flat = [FlatTask("01-first"), FlatTask("02-second"), FlatTask("03-third")];

        // A flat plan has NO waves; the completed-set is irrelevant.
        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(flat, [], Completed("anything"), showAllTasks: false);

        Assert.Empty(rows.OfType<WaveSummaryLiveRow>());
        Assert.Equal(
            ["01-first", "02-second", "03-third"],
            rows.OfType<TaskLiveRow>().Select(r => r.TaskId).ToArray());
        Assert.Equal(flat.Count, rows.Count); // exactly one row per task, nothing added or removed
    }

    // A fresh waved run (no wave completed yet) renders the same flat one-row-per-task list — the collapse
    // only ever engages as a wave settles, so the first frame matches the pre-#379 table.
    [Fact]
    public void WavedPlan_NoCompletedWaves_RendersFlatTaskList()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(tasks, waves, Completed(), showAllTasks: false);

        Assert.Empty(rows.OfType<WaveSummaryLiveRow>());
        Assert.Equal(tasks.Select(t => t.Id), rows.OfType<TaskLiveRow>().Select(r => r.TaskId));
    }

    // (d) The --all-tasks opt-out shows EVERY task's row across ALL waves even when waves are completed —
    //     no wave ever collapses.
    [Fact]
    public void AllTasksOptOut_ShowsEveryRow_EvenForCompletedWaves()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(
            tasks, waves, Completed("wave-01-renderer-core", "wave-02-classify"), showAllTasks: true);

        Assert.Empty(rows.OfType<WaveSummaryLiveRow>());
        Assert.Equal(tasks.Select(t => t.Id), rows.OfType<TaskLiveRow>().Select(r => r.TaskId));
    }

    // The observer must tolerate replayed events for a task whose wave has collapsed (a resume replays the
    // completed waves' `skipped` TaskFinished events). Driving WaveFinished(Completed) then a stale
    // TaskFinished for a now-collapsed task must NOT throw KeyNotFoundException (the main correctness risk
    // the issue calls out).
    [Fact]
    public async Task Observer_ToleratesReplayedEventsForCollapsedWaveTask()
    {
        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) = ThreeWavePlan();

        await using var observer = new LiveRunObserver(tasks, waves: waves, showAllTasks: false);

        WaveNode wave1 = waves[0];

        // A stray late event for a now-collapsed task (no row after the collapse) must be a silent no-op,
        // not a KeyNotFoundException — the main correctness risk the issue calls out. Drive the resume
        // replay order (the wave's tasks report `skipped`, THEN the wave finishes) and then fire more
        // stale events for a now-collapsed task.
        Exception? ex = Record.Exception(() =>
        {
            foreach (TaskNode t in wave1.Tasks)
            {
                observer.TaskFinished(new Core.Execution.TaskResult
                {
                    TaskId = t.Id,
                    Outcome = Core.Execution.TaskOutcome.Skipped,
                    Summary = "already succeeded (resumed) — skipped"
                });
            }

            observer.WaveFinished(wave1, Core.Journal.WaveStatus.Completed, skipped: true);

            // Post-collapse stale events for a task whose row is gone.
            observer.TaskStarting(wave1.Tasks[0]);
            observer.TaskFinished(new Core.Execution.TaskResult
            {
                TaskId = wave1.Tasks[0].Id,
                Outcome = Core.Execution.TaskOutcome.Skipped,
                Summary = "replay"
            });
            observer.GuardrailFinished(
                wave1.Tasks[0],
                new Core.Execution.GuardrailResult { Name = "01-check", Passed = true });
        });

        Assert.Null(ex);
    }
}
