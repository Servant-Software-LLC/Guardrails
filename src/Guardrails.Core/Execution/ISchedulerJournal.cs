namespace Guardrails.Core.Execution;

/// <summary>
/// The minimal journal surface the <see cref="Scheduler"/> needs: resume skips
/// (<see cref="StatusOf"/>), blocking the transitive dependents of a halted task
/// (<see cref="MarkBlocked"/>), and the run's cumulative cost for the per-run cost cap
/// (<see cref="CurrentCostUsd"/>). All other journal transitions belong to
/// <see cref="TaskExecutor"/>. Faked in scheduler unit tests.
/// </summary>
public interface ISchedulerJournal
{
    /// <summary>The journaled status of a task (resume rules already applied on load).</summary>
    Journal.TaskStatus StatusOf(string taskId);

    /// <summary>
    /// The <c>TaskDefinitionHash</c> recorded at a task's most recent successful settle (SSOT §7.2,
    /// issue #274 Part A), or null when none was recorded. Drives the resume definition-drift check.
    /// Default null for fakes that do not model it; <see cref="Journal.RunJournal"/> reads the real
    /// recorded hash.
    /// </summary>
    string? RecordedDefinitionHash(string taskId) => null;

    /// <summary>Mark a task blocked because an upstream dependency cannot succeed.</summary>
    void MarkBlocked(string taskId);

    /// <summary>
    /// Charge OVERHEAD prompt spend that is NOT a task attempt (SSOT §7/§9.2, issues #269/#314) — the
    /// overwatcher's diagnose prompts, the AI-merge worker, and the terminal needs-human triage — to the
    /// run's cumulative cost, so it BOTH counts toward the <c>maxCostUsd</c> gate (<see cref="CurrentCostUsd"/>)
    /// AND appears in the reported total. A null cost is a no-op. Default no-op for fakes that do not model
    /// cost; <see cref="Journal.RunJournal"/> accumulates and persists it.
    /// </summary>
    void AddOverheadCost(decimal? cost) { }

    /// <summary>
    /// The run's cumulative journaled cost in USD, used to enforce the per-run cost cap
    /// (<see cref="Model.RunConfig.MaxCostUsd"/>). Defaults to 0 — a journal that records no cost
    /// never trips a cap, so existing implementations need not change; <see cref="Journal.RunJournal"/>
    /// overrides it with the real total (<see cref="Journal.JournalCost.Total"/>).
    /// </summary>
    decimal CurrentCostUsd() => 0m;

    /// <summary>
    /// Reserve the next merge sequence number (advancing the counter). Default returns a dummy
    /// value for fakes that do not track sequences. <see cref="Journal.RunJournal"/> provides the
    /// real monotonic counter.
    /// </summary>
    long ReserveMergeSequence() => 0L;

    /// <summary>
    /// Record the terminal settle of a worktree task: update Status and optionally MergeSequence
    /// WITHOUT adding an AttemptRecord (the attempt was already recorded by the executor).
    /// Default is a no-op for fakes. <see cref="Journal.RunJournal"/> provides the real impl.
    /// </summary>
    void RecordSettle(
        string taskId, Journal.TaskStatus status, long? mergeSequence = null, string? definitionHash = null) { }

    /// <summary>
    /// Record the SUCCESSFUL settle of a worktree task (issue #196): append <paramref name="attempt"/>
    /// to the task's attempt list AND set Status + MergeSequence atomically. The worktree success path
    /// defers the attempt record to the settle (unlike serial mode, which records it inline), so
    /// without this the task would settle succeeded with an EMPTY <c>Attempts</c> list — contradicting
    /// SSOT §7, which shows a succeeded task with a populated <c>attempts[]</c>. Default delegates to
    /// <see cref="RecordSettle"/> for fakes that do not model attempts; <see cref="Journal.RunJournal"/>
    /// provides the real impl that also appends the attempt.
    /// </summary>
    void RecordSettleWithAttempt(
        string taskId, Journal.AttemptRecord attempt, Journal.TaskStatus status, long? mergeSequence = null,
        string? definitionHash = null) =>
        RecordSettle(taskId, status, mergeSequence, definitionHash);

    /// <summary>
    /// Force a task back to <c>pending</c> (keeping its attempt history), so the next scheduling wave
    /// re-runs it — the journal half of a Part C safe-drift resolution (issue #274, SSOT §7.2). Default
    /// no-op for fakes; <see cref="Journal.RunJournal"/> resets the real entry.
    /// </summary>
    void ResetTaskToPending(string taskId) { }

    /// <summary>
    /// Append an autonomy-policy decision to the durable, unified top-level <c>decisions[]</c> journal
    /// section (SSOT §2.1/§7) — the audit of what a decision boundary did (M1: a drift rewind's discard).
    /// Default no-op for fakes; <see cref="Journal.RunJournal"/> persists it.
    /// </summary>
    void RecordDecision(DecisionEntry entry) { }

    // --- Multi-wave surface (SSOT §7/§14, #254 M2b) ---------------------------------------
    // A WAVED plan drives N per-wave DAG drains against ONE continuous journal; the Scheduler reads
    // a wave's durable completion/phase record and writes its markers through these seams. All default
    // to the "no wave record" answer so a FLAT-plan fake (which never calls them) is unaffected;
    // <see cref="Journal.RunJournal"/> reads/writes the real waves[] section.

    /// <summary>The wave's durable journal record (status, definition hash, entry/exit markers, marker sha), or null when the wave has none. Default null for fakes.</summary>
    Journal.WaveJournalEntry? WaveEntryOf(string waveDir) => null;

    /// <summary>Record the wave ENTRY-preflight phase marker (SSOT §14.6) and set the wave <c>running</c>. Default no-op.</summary>
    void RecordWaveEntry(string waveDir, Journal.PlanPreflightsSection entry) { }

    /// <summary>Record the wave EXIT/terminal-gate phase marker (SSOT §14.6). Default no-op.</summary>
    void RecordWaveExit(string waveDir, Journal.PlanGuardrailsSection exit) { }

    /// <summary>Record a wave settling <c>completed</c> (SSOT §14.5): its <c>WaveDefinitionHash</c> + the <c>Guardrails-Wave:</c> marker commit sha. Default no-op.</summary>
    void RecordWaveCompleted(string waveDir, string definitionHash, string? markerSha) { }

    /// <summary>Set a wave's status (e.g. <c>running</c>/<c>needs-human</c>/<c>blocked</c>) without touching its markers. Default no-op.</summary>
    void RecordWaveStatus(string waveDir, Journal.WaveStatus status) { }

    /// <summary>Reset a wave to <c>pending</c>, clearing its completion hash, marker sha, and entry/exit markers — the wave half of a wave-drift resolution / wave-scoped reset (SSOT §14.6/§14.8). Default no-op.</summary>
    void ResetWaveToPending(string waveDir) { }
}
