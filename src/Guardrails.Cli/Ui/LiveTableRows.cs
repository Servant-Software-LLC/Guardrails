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
/// One synthetic row standing for a WAVE-SCOPED PHASE that is not a task and has no attempt loop
/// (issue #469). Emitted for a wave with ZERO authored tasks — a JIT stub — from RUN START, which fixes two
/// things at once: the 30-minute breakdown had no row to update (so a completed wave 1 collapsed to one
/// green line and the run read as FINISHED while it was mid-authoring), and an unauthored wave was invisible
/// from run start (an operator running a two-wave JIT plan had never been shown that a wave 2 exists).
///
/// <para>Because the row exists from run start, no mid-run <c>RebuildRows()</c> is needed and no new race is
/// introduced. Its cells are keyed <c>"&lt;waveDir&gt;/(&lt;phase&gt;)"</c> and it is named for the GENERAL
/// case on purpose: #476 (wave exit gates going silent) reuses this row, this key shape and the existing
/// 1 Hz ticker as a CONTENT change, not a second mechanism.</para>
/// </summary>
/// <param name="WaveDir">The wave this phase belongs to — the row's label and the key's prefix.</param>
public sealed record WavePhaseLiveRow(string WaveDir) : LiveTableRow
{
    /// <summary>The phase discriminator in this row's key. Only <c>breakdown</c> exists today (#469).</summary>
    public const string BreakdownPhase = "breakdown";

    /// <summary>
    /// The row key a <see cref="LiveRunObserver"/> indexes this row by: <c>&lt;waveDir&gt;/(&lt;phase&gt;)</c>.
    /// The parenthesised segment can never collide with a task id — SSOT §14.2 wave-qualified ids are
    /// <c>&lt;waveDir&gt;/&lt;taskFolder&gt;</c>, and a task folder name cannot contain parentheses.
    /// </summary>
    public static string KeyFor(string waveDir, string phase) => $"{waveDir}/({phase})";

    /// <summary>This row's key for the JIT-breakdown phase.</summary>
    public string BreakdownKey => KeyFor(WaveDir, BreakdownPhase);
}

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
    /// <para>
    /// A non-collapsed wave with ZERO tasks — a JIT stub, the one case that is currently invisible — leads
    /// its block with one <see cref="WavePhaseLiveRow"/> (issue #469). <b>The #485 rule is honoured:</b> a
    /// flat plan, and a waved plan whose waves are all AUTHORED, emit zero phase rows and therefore produce
    /// a byte-identical row list to before. The dominant case costs nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LiveTableRow> Plan(
        IReadOnlyList<TaskNode> tasks,
        IReadOnlyList<WaveNode> waves,
        IReadOnlySet<string> completedWaves,
        bool showAllTasks)
    {
        if (waves.Count == 0)
        {
            return tasks.Select(t => (LiveTableRow)new TaskLiveRow(t.Id)).ToList();
        }

        var rows = new List<LiveTableRow>();
        foreach (WaveNode wave in waves)
        {
            // --all-tasks suppresses the COLLAPSE, and only the collapse: an unauthored wave has no task
            // rows to expand, so its phase row is emitted either way (design 23 §5.4). Walking the waves
            // rather than the flattened list under the opt-out yields the identical task-row sequence —
            // the loader flattens in strict wave order (SSOT §14.2).
            if (!showAllTasks && completedWaves.Contains(wave.Dir))
            {
                rows.Add(new WaveSummaryLiveRow(wave.Dir, wave.Tasks.Count));
            }
            else
            {
                // An unauthored wave contributes no task rows at all, so without this it contributes NOTHING
                // — the defect #469 measured. One synthetic phase row stands for the whole wave until its
                // tasks exist.
                if (wave.Tasks.Count == 0)
                {
                    rows.Add(new WavePhaseLiveRow(wave.Dir));
                }

                foreach (TaskNode task in wave.Tasks)
                {
                    rows.Add(new TaskLiveRow(task.Id));
                }
            }
        }

        return rows;
    }
}
