using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// Which deterministic GATE stopped the run (issue #432, SSOT §7 <c>halt.kind</c>). Scoped deliberately to
/// the four-folder gate boundaries — the halts that settle NO task and therefore leave <c>tasks{}</c> a wall
/// of silent <c>pending</c> entries with nothing explaining why. A per-task halt (needs-human / blocked) is
/// already self-describing inside <c>tasks{}</c> and is NOT recorded here.
/// </summary>
public enum RunHaltKind
{
    /// <summary>The pre-DAG plan-level <c>&lt;plan&gt;/preflights/</c> Full Flight Checks failed (SSOT §7).</summary>
    PlanPreflightFailed,

    /// <summary>A wave's ENTRY preflight gate <c>&lt;plan&gt;/&lt;wave&gt;/preflights/</c> failed (SSOT §14.3).</summary>
    WaveEntryGateFailed,

    /// <summary>A wave's EXIT gate <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/</c> failed (SSOT §14.3).</summary>
    WaveExitGateFailed,

    /// <summary>The terminal plan-level <c>&lt;plan&gt;/guardrails/</c> gate failed on the merged HEAD (SSOT §3.3).</summary>
    PlanGuardrailFailed
}

/// <summary>
/// The machine-readable reason the run STOPPED, recorded as the OPTIONAL top-level <c>halt</c> section of
/// <c>state/run.json</c> (issue #432, SSOT §7).
/// <para>
/// A failing gate halts BEFORE (or instead of) settling tasks, so the journal's <c>tasks{}</c> alone reads
/// as "nothing happened" — the exact post-mortem dead end #432 was filed for: a maintainer's halted run left
/// 14 <c>pending</c> tasks and no recorded cause anywhere on disk. The per-gate sections
/// (<see cref="JournalDocument.PlanPreflights"/> / <see cref="JournalDocument.PlanGuardrails"/> /
/// <see cref="WaveJournalEntry.Entry"/> / <see cref="WaveJournalEntry.Exit"/>) hold the full per-check
/// detail; this section is the single, uniformly-shaped pointer that says WHICH of them stopped the run and
/// WHERE its captured output is, so a reader (the log viewer, post-mortem tooling) never has to know the
/// four-folder model to answer "why did this stop?".
/// </para>
/// <para>
/// Written only when a gate fails, and CLEARED on resume (<see cref="RunJournal.LoadOrCreate"/>) so a stale
/// halt from a previous attempt can never be mistaken for the current run's.
/// </para>
/// </summary>
public sealed record RunHalt
{
    /// <summary>Which gate stopped the run.</summary>
    public required RunHaltKind Kind { get; init; }

    /// <summary>UTC time the halt was recorded (ISO-8601).</summary>
    public required DateTimeOffset HaltedAt { get; init; }

    /// <summary>The one-line, human-readable headline — the same sentence the console printed.</summary>
    public required string Headline { get; init; }

    /// <summary>
    /// The wave directory for a wave-scoped gate (e.g. <c>wave-01-scaffold</c>); absent for a plan-scoped
    /// gate.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaveDir { get; init; }

    /// <summary>The checks that FAILED (name + actionable reason) — always at least one.</summary>
    public IReadOnlyList<FailedGuardrail> FailedChecks { get; init; } = [];

    /// <summary>
    /// Plan-relative, forward-slash path to the gate's captured output tree (see
    /// <see cref="Execution.GateArtifacts"/>), e.g. <c>logs/&lt;runId&gt;/wave-01-scaffold/preflights</c>.
    /// Each check's <c>stdout.log</c>/<c>stderr.log</c>/<c>result.json</c> sits in a
    /// <c>&lt;check-name&gt;/</c> subdirectory. Absent only when the run id was unavailable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogDir { get; init; }
}
