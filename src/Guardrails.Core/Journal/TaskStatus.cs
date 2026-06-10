namespace Guardrails.Core.Journal;

/// <summary>
/// Per-task status in the run journal (SSOT §7). The full set is modelled now so M4's
/// retry/needs-human machinery does not have to migrate the journal: M3 only ever writes
/// <see cref="Pending"/>, <see cref="Running"/>, <see cref="Succeeded"/>, and
/// <see cref="Failed"/>, but <see cref="NeedsHuman"/> and <see cref="Blocked"/> round-trip
/// correctly if present (e.g. a journal written by a later harness version).
/// </summary>
public enum TaskStatus
{
    /// <summary>Not yet run (or reset to be re-run). The resume target for all non-terminal states.</summary>
    Pending,

    /// <summary>In flight. On load this means a previous run crashed mid-task.</summary>
    Running,

    /// <summary>Terminal success. Resume skips it.</summary>
    Succeeded,

    /// <summary>Retry budget exhausted (M4). All transitive dependents become <see cref="Blocked"/>.</summary>
    NeedsHuman,

    /// <summary>A dependency did not succeed.</summary>
    Blocked,

    /// <summary>Failed this run (M3 terminal for a failed task; M4 replaces with needs-human on retry exhaustion).</summary>
    Failed
}
