using Guardrails.Core.Model;

namespace Guardrails.Cli.Ui;

/// <summary>
/// One planned row of the live run table. A <see cref="LiveRunObserver"/> table is either a flat list
/// of per-task rows (a flat plan, or a waved plan under <c>--all-tasks</c>) or — for a waved plan
/// (issue #379) — the ACTIVE/pending waves' full task rows interleaved with one collapsed summary line
/// per COMPLETED wave. The (re)build of the Spectre table + its row-index map is driven off the ordered
/// list this discriminated shape produces, keeping the row-planning logic pure and unit-testable
/// (the live table itself never renders in a non-interactive test).
/// </summary>
public abstract record LiveTableRow;

/// <summary>A per-task row whose Status/Detail cells are updated in place as the task runs.</summary>
public sealed record TaskLiveRow(string TaskId) : LiveTableRow;

/// <summary>
/// A single collapsed summary line standing in for a whole COMPLETED wave (issue #379) — its per-task
/// rows are noise once the wave is settled (their logs remain reachable from the static log site +
/// live diagram, which keep every task). <see cref="TaskCount"/> is the wave's task count; a completed
/// wave is wholly green, so the rendered line reads <c>N/N tasks green</c>.
/// </summary>
public sealed record WaveSummaryLiveRow(string WaveDir, int TaskCount) : LiveTableRow;

/// <summary>
/// Pure row-planning for the live run table (issue #379). Decides, given the plan's tasks/waves, which
/// waves have COMPLETED, and whether the <c>--all-tasks</c> opt-out is set, the exact ordered rows the
/// table should show. Extracted as a pure function so the collapse behaviour is unit-testable without a
/// live terminal.
/// </summary>
public static class LiveTableRows
{
    /// <summary>
    /// The ordered rows the live table should render.
    /// <para>
    /// FLAT plan (<paramref name="waves"/> empty) OR <paramref name="showAllTasks"/>: exactly one
    /// <see cref="TaskLiveRow"/> per task in <paramref name="tasks"/> order — byte-identical to the
    /// pre-#379 table (the collapse is guarded behind "plan has waves" + not opted out).
    /// </para>
    /// <para>
    /// WAVED plan: each wave in <paramref name="waves"/> (strict order) whose <see cref="WaveNode.Dir"/>
    /// is in <paramref name="completedWaves"/> collapses to ONE <see cref="WaveSummaryLiveRow"/>; every
    /// other wave contributes its full <see cref="TaskLiveRow"/>s. A fresh waved run (no completed wave)
    /// therefore renders identically to the flat list, and each wave collapses only as it settles.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LiveTableRow> Plan(
        IReadOnlyList<TaskNode> tasks,
        IReadOnlyList<WaveNode> waves,
        IReadOnlySet<string> completedWaves,
        bool showAllTasks)
    {
        if (waves.Count == 0 || showAllTasks)
        {
            return tasks.Select(t => (LiveTableRow)new TaskLiveRow(t.Id)).ToList();
        }

        var rows = new List<LiveTableRow>();
        foreach (WaveNode wave in waves)
        {
            if (completedWaves.Contains(wave.Dir))
            {
                rows.Add(new WaveSummaryLiveRow(wave.Dir, wave.Tasks.Count));
            }
            else
            {
                foreach (TaskNode task in wave.Tasks)
                {
                    rows.Add(new TaskLiveRow(task.Id));
                }
            }
        }

        return rows;
    }
}
