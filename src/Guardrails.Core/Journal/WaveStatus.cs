namespace Guardrails.Core.Journal;

/// <summary>
/// Per-wave status in the run journal (SSOT §7 <c>waves[]</c> / §14) — the wave-level analogue of
/// <see cref="TaskStatus"/>. A wave is <see cref="Completed"/> when every task in it has a green durable
/// record AND its exit-gate marker is passed for the wave's current hash (SSOT §14.5).
/// </summary>
public enum WaveStatus
{
    /// <summary>Not yet run (or reset to be re-run). The resume target for all non-terminal wave states.</summary>
    Pending,

    /// <summary>In flight — its task DAG is draining.</summary>
    Running,

    /// <summary>Terminal success: every task green + the exit gate passed. Resume skips it (SSOT §14.6).</summary>
    Completed,

    /// <summary>A task in the wave halted at needs-human; the barrier stops later waves starting (SSOT §14.4).</summary>
    NeedsHuman,

    /// <summary>A task in the wave was blocked by an upstream failure.</summary>
    Blocked
}
