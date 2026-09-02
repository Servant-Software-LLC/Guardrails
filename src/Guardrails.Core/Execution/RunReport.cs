namespace Guardrails.Core.Execution;

/// <summary>The result of running a single guardrail.</summary>
public sealed record GuardrailResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }

    /// <summary>One-line actionable reason on failure (the guardrail's first stdout line), else null.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The guardrail's full captured output on failure (stdout, or stderr when stdout is empty),
    /// for the retry feedback (issue #26 Gap 1). The one-line <see cref="Reason"/> truncates at
    /// the first line, which hid 8-of-9 build errors in a real failure; the retry agent needs
    /// every error, not just the first. Null for passing guardrails and prompt guardrails (whose
    /// signal is the one-line verdict reason).
    /// </summary>
    public string? Output { get; init; }
}

/// <summary>The full result of a single task in an M2 serial run.</summary>
public sealed record TaskResult
{
    public required string TaskId { get; init; }
    public required TaskOutcome Outcome { get; init; }

    /// <summary>The action's exit code, or null when the task was blocked and never ran.</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>Guardrail results in execution order (empty if action failed or task was blocked).</summary>
    public IReadOnlyList<GuardrailResult> Guardrails { get; init; } = [];

    /// <summary>A short human-readable explanation of the outcome (for the summary and logs).</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// The bounded, enumerated options a structured <c>needsHuman</c> escape carried (issue #387), in order.
    /// Empty for a free-text <c>needsHuman</c> and for any non-needs-human outcome. Read by the autonomous
    /// classify-then-act dispatch (<see cref="Scheduler.ClassifyTaskGateAsync"/>) so the raised escalation
    /// record carries the options a pick surface later presents.
    /// </summary>
    public IReadOnlyList<string> NeedsHumanOptions { get; init; } = [];

    /// <summary>
    /// The agent's optional classification of a needs-human halt (issue #485):
    /// <see cref="NeedsHumanKinds.BlockedWork"/> ("I cannot complete this work" — look at the TASK) or
    /// <see cref="NeedsHumanKinds.DefectiveGuardrail"/> ("this check is itself wrong" — look at the CHECK).
    /// Null means UNCLASSIFIED, and the harness invents no default: it records what the agent asserted and
    /// lets a human adjudicate. Rides HERE, beside <see cref="NeedsHumanOptions"/> and for the same reason
    /// (the #387 precedent), so no <see cref="IRunObserver"/> member is needed — <c>TaskFinished</c> already
    /// delivers it to every observer.
    /// <para><b>Never stamped into <see cref="Summary"/>.</b> <c>Scheduler.ExtractNeedsHumanQuestion</c>
    /// parses the literal <c>needs human: </c> prefix out of the summary and treats the remainder as the
    /// escalation's question; a kind spliced in there would either break that dispatch or pollute the
    /// recorded question. It is a FIELD, never a substring.</para>
    /// </summary>
    public string? NeedsHumanKind { get; init; }

    /// <summary>
    /// In worktree mode, the path to the validated fragment file for deferred B1 settle in the
    /// Scheduler. Null in serial mode (AttemptJournaler handles the merge immediately).
    /// </summary>
    public string? FragmentPath { get; init; }

    /// <summary>
    /// True when the Scheduler must perform the B1 deferred settle (fragment merge → git commit →
    /// journal RecordSettle) for this result. Set by <see cref="AttemptJournaler.ValidateFragmentForSettle"/>
    /// in worktree mode. False in serial mode (AttemptJournaler already merged and journaled).
    /// </summary>
    public bool DeferredSettle { get; init; }

    /// <summary>
    /// In worktree mode, the not-yet-journaled attempt data the Scheduler's B1 settle records (issue
    /// #196): a succeeded worktree task must journal a real <see cref="Journal.AttemptRecord"/> — with
    /// the same shape serial mode records — TOGETHER with the reserved <c>mergeSequence</c>, so
    /// <c>journal.Tasks[id].Attempts</c> is non-empty for a succeeded task in BOTH modes (SSOT §7).
    /// The executor computes this per-attempt data (attempt number, timing, cost, relative log dir,
    /// and the #198 provenance) but cannot record it, because the settle (and thus the outcome +
    /// mergeSequence) is deferred to the Scheduler under the integration lock. Null in serial mode
    /// (AttemptJournaler already recorded the attempt) and for non-deferred results.
    /// </summary>
    public PendingAttempt? PendingAttempt { get; init; }

    /// <summary>
    /// Set when this task hit at least one class-(b) transient PAUSE (issue #115 <see cref="TransientBackoff"/>)
    /// that RESOLVED WITHIN the per-task pause budget and the attempt then SUCCEEDED (doc 12 §4.1/§4.2). Null
    /// when the task never paused. The executor already resolved the transient (it re-ran the paused attempt to
    /// success) — this is the forensic signal the autonomous layer (<see cref="Scheduler.ClassifyTaskGateAsync"/>)
    /// reads to record a <c>blocker-retried</c> decision WITHOUT re-running any wait. Distinct from
    /// <see cref="TaskOutcome.RateLimited"/> (a transient that did NOT clear within budget): a resolved
    /// transient settles <see cref="TaskOutcome.Succeeded"/> and carries this instead.
    /// </summary>
    public ResolvedTransient? ResolvedTransient { get; init; }

    /// <summary>True only for a genuine success this run (not a resume skip).</summary>
    public bool Succeeded => Outcome == TaskOutcome.Succeeded;

    /// <summary>
    /// True when this task is "green" for the run's overall verdict: it succeeded this run
    /// or was skipped because the journal already recorded it as succeeded (resume).
    /// </summary>
    public bool IsGreen => Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped;
}

/// <summary>
/// The per-attempt data a worktree-mode success carries to the Scheduler's B1 settle so it can journal
/// a real <see cref="Journal.AttemptRecord"/> alongside the reserved <c>mergeSequence</c> (issue #196).
/// The executor computes all of it during the attempt but defers the actual record because the outcome
/// (succeeded vs a non-FF-union rollback to needs-human) and the mergeSequence are only known after the
/// integration commit, under the integration lock. The settle path builds the AttemptRecord from these
/// fields — the SAME shape serial mode's <see cref="AttemptJournaler.CompleteSucceededOrInvalidFragment"/>
/// records — so a succeeded task has a populated <c>Attempts</c> list in BOTH modes.
/// </summary>
public sealed record PendingAttempt
{
    /// <summary>1-based attempt number (already reserved by the journal for this attempt's log dir).</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC attempt start time.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The action's exit code (0 on the success path).</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>The prompt attempt's total cost in USD (null for a script or an unreported prompt cost).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// The prompt attempt's token volume (#475, SSOT §7 <c>usage</c> / DoR §12.4); null for a script or a
    /// runner that reported none.
    /// <para><b>This member is what makes the tokens axis reach a real run, and its absence is what kept
    /// the field dead.</b> The worktree settle (<c>Scheduler.RecordSucceededSettle</c>) builds its OWN
    /// <see cref="Journal.AttemptRecord"/> from this object and never consults the journaller — so a value
    /// the journaller sets but this record does not carry reaches SERIAL runs only, and worktree is the
    /// DEFAULT mode. <see cref="CostUsd"/> survives that path for exactly one reason: it is declared here.
    /// Its sibling now is too.</para>
    /// </summary>
    public Journal.AttemptUsage? Usage { get; init; }

    /// <summary>
    /// The attempt's turn count (plan 30 §3.4), on the same terms as <see cref="Usage"/> immediately
    /// above: <see cref="Journal.AttemptRecord.Turns"/> is declared but this record was not, so a value
    /// the journaller would set reaches SERIAL runs only — <c>Scheduler.RecordSucceededSettle</c> builds
    /// its OWN <see cref="Journal.AttemptRecord"/> from THIS object and never consults the journaller,
    /// and worktree is the DEFAULT mode. This member's counterpart at the next hop is
    /// <see cref="Journal.AttemptRecord.Turns"/>.
    /// </summary>
    public int? Turns { get; init; }

    /// <summary>
    /// The attempt's segmented wall-clock duration (plan 30 §3.4), on the same terms as
    /// <see cref="Turns"/> immediately above and <see cref="Usage"/> before it: the worktree settle
    /// builds its own <see cref="Journal.AttemptRecord"/> from this object without consulting the
    /// journaller, so this is the fact's only route to a worktree-mode <c>run.json</c>. This member's
    /// counterpart at the next hop is <see cref="Journal.AttemptRecord.Segments"/>.
    /// </summary>
    public Journal.AttemptSegments? Segments { get; init; }

    /// <summary>
    /// The task's fingerprint bucket (plan 30 §3.2), on the same "settle builds its own record and never
    /// consults the journaller" terms as <see cref="Turns"/> and <see cref="Segments"/> above — but TASK
    /// grain, not attempt grain: this member's counterpart at the next hop is
    /// <see cref="Journal.TaskJournalEntry.Bucket"/>, NOT a member of <see cref="Journal.AttemptRecord"/>.
    /// It rides this attempt-grained record anyway because the worktree settle
    /// (<c>Scheduler.RecordSucceededSettle</c>) is the only place the scheduler learns anything about the
    /// task at settle time.
    /// </summary>
    public string? Bucket { get; init; }

    /// <summary>This attempt's log dir, relative to the plan dir (SSOT §7/§8).</summary>
    public required string LogDir { get; init; }

    /// <summary>
    /// The #198 provenance the harness knew at attempt launch (resolved model, segment worktree,
    /// base commit). Never null in worktree mode (the segment always exists); the model is null for
    /// a script task.
    /// </summary>
    public Journal.AttemptProvenance? Provenance { get; init; }
}

/// <summary>
/// The forensic signal a <see cref="TaskResult"/> carries when a class-(b) transient (429/503/529, overloaded,
/// a rate/session/usage limit) PAUSED at least once and then CLEARED within the per-task pause budget, letting
/// the attempt succeed (issue #115 / doc 12 §4.2). The executor's <see cref="TransientBackoff"/> already
/// resolved it — no further wait is needed; this only records HOW MANY pauses and HOW LONG was waited so the
/// autonomous layer can append the <c>blocker-retried</c> forensic entry (doc 12 §6.2).
/// </summary>
public sealed record ResolvedTransient
{
    /// <summary>How many backoff pauses were taken before the paused attempt succeeded (≥ 1 when this signal is present).</summary>
    public required int Pauses { get; init; }

    /// <summary>Cumulative scheduled wall-clock time spent paused before the transient cleared.</summary>
    public required TimeSpan Waited { get; init; }
}

/// <summary>The aggregate result of an entire run.</summary>
public sealed record RunReport
{
    /// <summary>Per-task results in plan order.</summary>
    public required IReadOnlyList<TaskResult> Tasks { get; init; }

    /// <summary>True when the run was cancelled (Ctrl+C) before quiescence.</summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// True when every task is green (succeeded this run or skipped as already-succeeded) AND the run did
    /// not HALT at a run-level boundary. The halt guards matter for a WAVED plan whose halt leaves no
    /// non-green task in the report — e.g. an unauthored next wave (SSOT §14.4) contributes zero tasks, so
    /// a plain per-task check would read "all succeeded" for a run that actually stopped. A halted /
    /// aborted / definition-drifted / definition-diverged run is never "all succeeded".
    ///
    /// <para><b>The SINGLE predicate that gates delivery, the green summary and the exit code</b>, which is
    /// why <see cref="ExecutedDefinitionDivergence"/> (issue #556) lands here as ONE added conjunct rather
    /// than as a second gate of its own: no new delivery path is introduced, so the blast radius of a
    /// delivery-gate change stays one expression — the lesson of #457, where a SECOND gate that ran AFTER
    /// delivery was the defect. Every consumer inherits the term for free: the Scheduler's <c>deliverable</c>
    /// and <see cref="WhollyGreenButUndelivered"/>, the legacy in-Scheduler terminal integration gate, the
    /// CLI's terminal plan-guardrail phase, <see cref="DeliveryPendingTerminalGate"/>, worktree retention,
    /// and the exit-code and summary rendering.</para>
    /// </summary>
    public bool AllSucceeded => !HasDefinitionDrift && !HasWaveHalt && !Aborted
                             && !HasExecutedDefinitionDivergence && Tasks.All(t => t.IsGreen);

    /// <summary>True when at least one task failed or was blocked.</summary>
    public bool AnyFailed => Tasks.Any(t => !t.IsGreen);

    /// <summary>
    /// The outcome of the end-of-run merge-on-success delivery (plan 08 SSOT §5.3).
    /// Null when <c>mergeOnSuccess</c> is false or the run was not wholly green.
    /// Implemented by task 22.
    /// </summary>
    public MergeOnSuccessResult? MergeOnSuccessOutcome { get; init; }

    /// <summary>
    /// Free-text detail for the merge-on-success outcome when it carries one; null otherwise. The CLI
    /// renders it so the user sees exactly what refused the delivery. Three carriers:
    /// <list type="bullet">
    ///   <item><see cref="MergeOnSuccessResult.HookRejected"/> — the git hook's stderr, verbatim
    ///     (issues #149/#150).</item>
    ///   <item><see cref="MergeOnSuccessResult.DirtyWorkingTree"/> — the newline-separated, ordinal-sorted
    ///     TRACKED paths whose uncommitted changes blocked the merge (issue #448).</item>
    ///   <item><see cref="MergeOnSuccessResult.BranchMoved"/> — the branch the run started on and the one
    ///     HEAD is on now (issue #588).</item>
    /// </list>
    /// </summary>
    public string? MergeOnSuccessDetail { get; init; }

    /// <summary>
    /// The user's original branch name a wholly-green run's work was DELIVERED to (issue #340), set by the
    /// Scheduler's <c>Finalize</c> only when the end-of-run merge-back actually ran and succeeded
    /// (<see cref="MergeOnSuccessOutcome"/> is <see cref="MergeOnSuccessResult.FastForwarded"/> or
    /// <see cref="MergeOnSuccessResult.Merged"/>); null otherwise. Purely descriptive — it does NOT change
    /// the delivery gate or the exit code. The CLI uses it to NAME the branch in the one-time
    /// "delivered by default" notice printed when delivery fired purely because of the new #340 default.
    /// </summary>
    public string? DeliveredToBranch { get; init; }

    /// <summary>
    /// True when this run drained WHOLLY GREEN (the DAG) but the completed work was NOT delivered to the
    /// user's branch because <c>mergeOnSuccess</c> resolved <b>false</b> (issue #340). The verified work
    /// is sitting on the plan branch <c>guardrails/&lt;plan-name&gt;</c>, undelivered — one
    /// <c>--fresh</c>/<c>reset -y</c> away from destruction. Set by the Scheduler's <c>Finalize</c> ONLY
    /// when a real, SEPARATE plan branch exists (worktree mode: a worktree provider AND an integration
    /// handle are present) so it is HONEST: in serial mode there is no plan branch (<c>integ == null</c>),
    /// so the work is already in the shared workspace — nothing is undelivered — and this stays false.
    /// It is NOT suppressed for <c>runOnCurrentBranch</c> (#345 review, finding 1c): that flag is currently
    /// an UNWIRED STUB, so a worktree-mode opt-out run still creates a separate plan branch and DOES strand
    /// verified work — the warning must fire (the exact #340 incident, otherwise uncovered). It is also false
    /// whenever delivery actually RAN (that requires <c>mergeOnSuccess</c> true, so
    /// <see cref="MergeOnSuccessOutcome"/> is then non-null and this false — never both). The CLI renders a
    /// loud, unmissable warning when this is true AND the terminal gate also passed (the warning belongs
    /// behind the CLI seam, SSOT §7).
    /// </summary>
    public bool WhollyGreenButUndelivered { get; init; }

    /// <summary>
    /// True when this run drained wholly green with delivery resolved ON, but the delivery was HELD BACK
    /// because the plan declares a terminal gate (<c>&lt;plan&gt;/guardrails/</c>) whose verdict the
    /// Scheduler does not have (issue #457).
    /// <para>
    /// <b>The defect this closes.</b> Delivery used to fire inside the Scheduler on
    /// <see cref="AllSucceeded"/> — which is TASKS ONLY. The CLI's terminal plan-guardrail phase runs
    /// AFTER the Scheduler returns, so a run could merge to the user's branch and only then discover
    /// that the whole-repo soundness check failed: observed on one run as a delivery at 21:50:36 and a
    /// terminal-gate FAILURE at 21:55:26, with the CLI printing "terminal halt" for work that had
    /// already shipped. A gate that runs after delivery is not a gate.
    /// </para>
    /// <para>
    /// When true the run is NOT finished: the CLI evaluates the terminal gate and, only if it PASSES,
    /// calls <see cref="Scheduler.CompleteDeferredDelivery"/> to perform the merge and stamp
    /// <see cref="MergeOnSuccessOutcome"/> / <see cref="MergeOnSuccessDetail"/> /
    /// <see cref="DeliveredToBranch"/>. A FAILED gate simply never calls it, so nothing is delivered
    /// and the verified work stays on the plan branch. It is false whenever delivery already ran
    /// in-Scheduler (a plan with no terminal-gate folder, whose legacy in-Scheduler gate has ALREADY
    /// been folded into <see cref="AllSucceeded"/> before delivery is considered) — so the two are
    /// never both set, and the #340 delivered-by-default behaviour is unchanged for every genuinely
    /// green run.
    /// </para>
    /// </summary>
    public bool DeliveryPendingTerminalGate { get; init; }

    /// <summary>
    /// How many waves this run PROCEEDED THROUGH UNREVIEWED (issue #361 Phase 4, doc 12 §5.2 Option P / §7.1):
    /// the count of <see cref="DecisionTokens.ProceededUnreviewed"/> decisions the run recorded, derived by
    /// <see cref="RunOutcomePolicy.ProceededUnreviewedWaveCount"/> over the durable <c>decisions[]</c> stream
    /// and stamped here by the Scheduler's <c>Finalize</c>. Zero for a run that never advanced past an
    /// unreviewed wave (including one that only best-guessed). A positive count PERMANENTLY flags the run
    /// "ran with N unreviewed waves" so an automated firstmate consumer can never read it as clean green — the
    /// CLI rendering and the distinct non-zero exit code that consume it are task 10 (this only carries the
    /// number). Purely descriptive here; it does not change the delivery gate.
    /// </summary>
    public int UnreviewedWaveCount { get; init; }

    /// <summary>
    /// Non-null when the run was ABORTED by an unexpected infrastructure fault (a task executor or an
    /// integration step threw — e.g. an offline git hook failing an INTERNAL commit, or git itself
    /// being unavailable). Rather than propagating an unhandled exception out of the scheduler
    /// (issue #150), the run terminates the worker pool, runs the end-of-run cleanup sweep, and
    /// returns a report carrying this reason. The CLI renders a one-line diagnostic + remedy, writes
    /// the FULL exception to the run logs, and exits non-zero — an honest halt, never a raw stack
    /// trace as the headline. When set, treat the run as failed regardless of per-task outcomes.
    /// </summary>
    public RunAbort? Abort { get; init; }

    /// <summary>True when the run was aborted by an infrastructure fault (see <see cref="Abort"/>).</summary>
    public bool Aborted => Abort is not null;

    /// <summary>
    /// Non-null when the resume pre-pass HALTED the run because at least one already-<c>succeeded</c>
    /// task's current <c>TaskDefinitionHash</c> no longer matches the hash recorded at its last
    /// successful settle (SSOT §7.2, issue #274 Part A). The harness scheduled NOTHING — it neither
    /// silently reused the stale cached segment nor silently re-ran the changed task. A pre-DAG halt, a
    /// sibling of <see cref="Abort"/>; the CLI renders it where <see cref="Abort"/> renders and exits
    /// <b>2</b> (actionable/needs-human, matching planPreflights/planGuardrails), NOT 1. When set, treat
    /// the run as halted regardless of per-task outcomes.
    /// </summary>
    public DefinitionDriftReport? DefinitionDrift { get; init; }

    /// <summary>True when the run halted on a definition-drift (see <see cref="DefinitionDrift"/>).</summary>
    public bool HasDefinitionDrift => DefinitionDrift is not null;

    /// <summary>
    /// Non-null when the pre-DAG gate recorded an autonomy-policy decision this run (SSOT §2.1/§7): in M1
    /// this is a Part C safe definition-drift AUTO-RESOLVED (§7.2) — the plan branch was rewound past the
    /// safe drifted suffix and its tasks journal-reset to re-run. This is NOT a halt — the run proceeds and
    /// returns the normal exit code (0 green / 2 needs-human); it carries the <c>drift</c>-boundary
    /// <see cref="DecisionEntry"/> for the end-of-run summary, mirroring the durable <c>decisions[]</c>
    /// journal section.
    /// </summary>
    public DecisionEntry? Decision { get; init; }

    /// <summary>
    /// The entries this run merely NOTICED — in M1 the <c>plan-edit</c> observations
    /// <see cref="LivePlanEditWatch"/> raised when the operator edited the plan folder mid-run (plan 31
    /// §5.4, issue #545 part 3). A sibling of <see cref="Decision"/> rather than a widening of it, because
    /// the two mean different things: <see cref="Decision"/> is something the harness <b>decided</b> (and is
    /// singular — the pre-DAG drift decision this run took), <see cref="Observations"/> are things it
    /// <b>noticed</b>, of which a run can produce N. Additive and defaulted, so no existing consumer changes
    /// and the shipped drift renderer is untouched by a reason unrelated to drift.
    /// <para>These are ALSO appended to the durable <c>decisions[]</c> in <c>run.json</c> as they happen;
    /// this list is the end-of-run report's copy, for the terminal advisory the CLI renders.</para>
    /// </summary>
    public IReadOnlyList<DecisionEntry> Observations { get; init; } = [];

    /// <summary>
    /// Non-null when a WAVED run HALTED at a wave boundary (SSOT §14, #254 M2b) other than a per-task
    /// needs-human (which is reported via the ordinary task outcomes + later-wave Blocked entries): the next
    /// wave is unauthored/empty (the JIT checkpoint, §14.4), a wave's entry or exit gate failed, or a
    /// completed wave DRIFTED under a <c>halt</c>/unconfirmed-<c>prompt</c> policy (§14.6). The CLI renders
    /// it and exits <b>2</b> (actionable), like <see cref="DefinitionDrift"/>. When set, treat the run as
    /// halted regardless of per-task outcomes.
    /// </summary>
    public WaveHalt? WaveHalt { get; init; }

    /// <summary>True when the run halted at a wave boundary (see <see cref="WaveHalt"/>).</summary>
    public bool HasWaveHalt => WaveHalt is not null;

    /// <summary>
    /// Non-null when at least one task SETTLED against a definition that had moved on disk since this run
    /// loaded it (SSOT §7.2, issue #556): the settle-time executed-definition divergence gate. A sibling of
    /// <see cref="DefinitionDrift"/> rather than a widening of it — drift is a PRE-DAG resume finding about a
    /// task that already succeeded in an EARLIER run, this is an IN-RUN finding about work this run just did.
    ///
    /// <para><b>Why an in-run gate exists at all.</b> Stamping the load-time pin makes the NEXT RESUME
    /// honest, and <i>a run that goes green to completion never resumes</i> — so with <c>mergeOnSuccess</c>
    /// defaulting ON (#340) an unattended run with a mid-run plan-folder edit would deliver the
    /// stale-definition work and print a green summary, with the correctly-pinned hash never read by
    /// anybody.</para>
    ///
    /// <para><b>What it never does.</b> It never refuses the settle, never cancels in-flight work and never
    /// stops dispatch. The attempt ran, the guardrails passed, the fragment merged: discarding that would
    /// repeat #554's defect, and in worktree mode the integration commit lands BEFORE the journal settle, so
    /// a commit carrying a <c>Guardrails-Task:</c> trailer whose journal said "not succeeded" is exactly the
    /// present-but-uncorroborated state Part C rule 3 refuses to rewind past — a remediation strictly worse
    /// than the bug. The run drains to completion (every later task carries its own pin and its own check)
    /// and only <see cref="AllSucceeded"/> goes false, which blocks DELIVERY.</para>
    /// </summary>
    public ExecutedDefinitionDivergenceReport? ExecutedDefinitionDivergence { get; init; }

    /// <summary>True when a task settled against a definition that had moved (see <see cref="ExecutedDefinitionDivergence"/>).</summary>
    public bool HasExecutedDefinitionDivergence => ExecutedDefinitionDivergence is not null;
}

/// <summary>The kind of wave-boundary halt a WAVED run stopped at (SSOT §14, #254 M2b).</summary>
public enum WaveHaltKind
{
    /// <summary>The next wave folder is present but has no authored tasks (or is unauthored) — the human JIT-breakdown checkpoint (§14.4).</summary>
    NextWaveUnauthored,

    /// <summary>A completed wave's <c>WaveDefinitionHash</c> drifted and the policy did not authorize an auto-resolve (§14.6).</summary>
    WaveDrift,

    /// <summary>A wave's ENTRY preflight gate failed (§14.3) — the prior wave's outputs were not materialized as expected.</summary>
    EntryGateFailed,

    /// <summary>A wave's EXIT/terminal gate failed (§14.3) on the merged HEAD-so-far.</summary>
    ExitGateFailed,

    /// <summary>
    /// Between-wave auto-breakdown (#360 Phase 1, SSOT §14.4/§14.10; doc 11 §9) against a wave's
    /// <c>brief.md</c> was invoked (autonomyPolicy <c>auto</c>, or a <c>prompt</c> approval) and its output
    /// PASSED the deterministic <c>guardrails validate</c> gate; the run HALTS for the human review gate
    /// (<c>/guardrails-review</c>) before advancing — the review gate is NEVER auto-satisfied at any policy
    /// (doc 11 §9.6). <see cref="WaveHalt.Detail"/> carries the review instructions.
    /// </summary>
    BreakdownComplete,

    /// <summary>
    /// Between-wave auto-breakdown (#360 Phase 1) was invoked but its output FAILED <c>guardrails validate</c>.
    /// The partial invalid <c>tasks/</c> is QUARANTINED (to <c>logs/&lt;runId&gt;/&lt;wave-dir&gt;/breakdown/
    /// rejected/</c>) so the plan stays loadable and the JIT checkpoint cleanly re-fires on resume; the run
    /// halts carrying the full validation errors (in <see cref="WaveHalt.Detail"/>) for manual repair.
    /// </summary>
    BreakdownFailed,

    /// <summary>
    /// The breakdown session was CUT OFF (timeout / turn cap / output cap / cancellation) but left a VALID
    /// PREFIX, and the wave's <c>state/breakdown-intent.json</c> says how much is still owed (SSOT §14.11,
    /// issues #385/#402). The prefix is PRESERVED — not quarantined — and the JIT checkpoint re-fires to
    /// RESUME the remainder rather than re-paying for the tasks already on disk.
    /// <para>Distinct from <see cref="BreakdownComplete"/> and never collapsible into it: a cut-off session
    /// can never be reported complete whatever <c>validate</c> says, because a valid prefix that reads as a
    /// finished wave is strictly worse than a loud quarantine — it sends a human to review 11 tasks with no
    /// signal that 3 are missing.</para>
    /// </summary>
    BreakdownIncomplete
}

/// <summary>
/// A WAVED run's wave-boundary halt (SSOT §14, #254 M2b) — the wave-level analogue of
/// <see cref="DefinitionDriftReport"/>/<see cref="RunAbort"/>. Carries what the CLI renders + the exit-2
/// actionable next step.
/// </summary>
public sealed record WaveHalt
{
    /// <summary>The wave directory the run halted at (e.g. <c>wave-02-build</c>).</summary>
    public required string WaveDir { get; init; }

    /// <summary>Which kind of wave-boundary halt this is.</summary>
    public required WaveHaltKind Kind { get; init; }

    /// <summary>One-line, human-readable headline for the console.</summary>
    public required string Headline { get; init; }

    /// <summary>Fuller detail / remediation (may be empty).</summary>
    public string Detail { get; init; } = "";

    /// <summary>The integration worktree path a human breaks the next wave down against (JIT checkpoint, §14.4/decision D); null when N/A.</summary>
    public string? IntegrationWorktreePath { get; init; }

    /// <summary>
    /// Absolute path to the wave folder (e.g. the full OS path to <c>wave-02-build</c>).
    /// Populated for <see cref="WaveHaltKind.NextWaveUnauthored"/> (the JIT checkpoint); null for
    /// other halt kinds. Used by the CLI to render a focused wave diagram at the checkpoint
    /// (issue #359).
    /// </summary>
    public string? WaveDirectory { get; init; }

    /// <summary>For a wave-drift halt: this wave + its downstream waves that would re-run on resolve; empty otherwise.</summary>
    public IReadOnlyList<string> AffectedWaves { get; init; } = [];

    /// <summary>For a wave-drift halt: the recorded → current <c>WaveDefinitionHash</c>; null otherwise.</summary>
    public string? OldHash { get; init; }

    /// <summary>For a wave-drift halt: the current <c>WaveDefinitionHash</c>; null otherwise.</summary>
    public string? NewHash { get; init; }

    /// <summary>For a gate-failure halt: the failing gate checks (name + reason); empty otherwise.</summary>
    public IReadOnlyList<GuardrailResult> FailedGates { get; init; } = [];
}

/// <summary>
/// The issue #274 Part A definition-drift halt (SSOT §7.2): every already-succeeded task whose
/// definition changed since it last succeeded, reported for the human's decision rather than silently
/// re-executed (auto-invalidating a fan-in descendant would fork it from a base still carrying its own
/// stale commit — the exact bug one level down — so auto-invalidation is unsound; that soundness limit
/// is why Part A halts).
/// </summary>
public sealed record DefinitionDriftReport
{
    /// <summary>The drifted tasks, in plan order.</summary>
    public required IReadOnlyList<DriftedTask> Tasks { get; init; }

    /// <summary>
    /// Whether the drift COULD be auto-resolved (issue #274 Part C): <c>true</c> when the drifted set forms
    /// a provably-safe trailing suffix (so the halt is a policy/consent choice — the operator can re-run
    /// interactively or with <c>--reprocess-drift</c>); <c>false</c> when the rewind was REFUSED as unsound
    /// (a non-suffix / uncontained fan-in / trailer-less commit — no flag authorizes it, steer to the full
    /// <c>reset -y</c> rebuild). Lets the CLI print the RIGHT remediation instead of leading with a flag
    /// that would just re-halt. Defaults <c>true</c> (the Part A halt, before Part C evaluated safety, is a
    /// "human decides" halt).
    /// </summary>
    public bool SafeToAutoResolve { get; init; } = true;

    /// <summary>When <see cref="SafeToAutoResolve"/> is false, WHY the rewind was refused (the <see cref="SafeSuffixDecision.Refusal"/>); null otherwise.</summary>
    public string? RewindRefusal { get; init; }

    /// <summary>When <see cref="SafeToAutoResolve"/> is false, the out-of-set task that blocked the rewind (the <see cref="SafeSuffixDecision.BlockingTask"/>); null otherwise.</summary>
    public string? RewindBlockingTask { get; init; }
}

/// <summary>One task whose <c>TaskDefinitionHash</c> drifted since its last successful settle (§7.2).</summary>
public sealed record DriftedTask
{
    /// <summary>The drifted task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>The <c>sha256:</c>-prefixed definition hash recorded at the last successful settle.</summary>
    public required string OldHash { get; init; }

    /// <summary>The <c>sha256:</c>-prefixed definition hash of the current on-disk definition.</summary>
    public required string NewHash { get; init; }

    /// <summary>
    /// The plan-branch commit bearing this task's <c>Guardrails-Task-Hash:</c> trailer (§5.3) — the
    /// anchor the Tier-2 per-file breakdown recovers old bytes from. Null when unavailable (serial mode,
    /// a journal-only success with no plan-branch commit) — Tier 2 then degrades, Tier 1 stands.
    /// </summary>
    public string? OldCommit { get; init; }

    /// <summary>
    /// The Tier-2 per-file breakdown of which definition files drifted (best-effort). Empty when the
    /// old bytes were not recoverable from <see cref="OldCommit"/> — see <see cref="Note"/>.
    /// </summary>
    public IReadOnlyList<ChangedDefinitionFile> ChangedFiles { get; init; } = [];

    /// <summary>The reference command <c>git diff &lt;oldCommit&gt;..HEAD -- &lt;task paths&gt;</c> for full content.</summary>
    public required string DiffCommand { get; init; }

    /// <summary>
    /// The task's transitive-descendant set (<c>DependencyGraph.TransitiveDependentsOf</c>, full DAG
    /// closure) — a changed producer can change a consumer's inputs. Reported for the human's decision,
    /// not re-executed.
    /// </summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];

    /// <summary>
    /// A Tier-2 degradation note when the prior file bytes were not recoverable from git (e.g. the plan
    /// folder was uncommitted at <see cref="OldCommit"/>, or there is no plan-branch commit at all);
    /// null when the full per-file breakdown is present. Tier 1 (the aggregate hash drift) never depends
    /// on git recovery.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// The issue #556 settle-time executed-definition divergence halt (SSOT §7.2): every task this run settled
/// whose definition files had MOVED ON DISK since the run loaded them, so the certified work was verified
/// against a definition that is no longer there.
///
/// <para><b>The comparison surface is deliberately NARROWER than the recorded hash.</b>
/// <c>HashText.EnumerateFolderFiles</c> globs <c>"*"</c> and filters nothing, so an editor or OS artifact IS
/// part of a task's recorded definition — and must stay that way, since changing that file set would move
/// every recorded definition hash in every plan. The GATE instead compares the ignore-list-filtered surface
/// (the one <see cref="LivePlanEditWatch"/> already speaks to humans through), so it fires only when a REAL
/// definition file moved: <c>task.json</c>, the action file, a guardrail or preflight script or sidecar.
/// A stray artifact leaves the run green and delivering, and remains what it is today: a resume-time drift
/// condition §7.2 already owns. A delivery gate that blocked an overnight run on a stray editor file would
/// be disabled within a week, and then the real signal would be gone too (#229).</para>
/// </summary>
public sealed record ExecutedDefinitionDivergenceReport
{
    /// <summary>The diverged tasks, in settle order.</summary>
    public required IReadOnlyList<DivergedTask> Tasks { get; init; }
}

/// <summary>
/// One task that settled against a definition that had already moved on disk (§7.2, #556). Carries BOTH
/// hashes — what the attempt EXECUTED and what is on disk now — plus which definition files moved, so the
/// halt is actionable without a git round-trip and names the same set the next resume's
/// <see cref="DefinitionDriftReport"/> will.
/// </summary>
public sealed record DivergedTask
{
    /// <summary>The diverged task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// The <c>sha256:</c>-prefixed definition hash captured at PLAN LOAD — the bytes the attempt actually
    /// executed against, and the value the journal entry records for this settle.
    /// </summary>
    public required string HashAtLoad { get; init; }

    /// <summary>
    /// The <c>sha256:</c>-prefixed definition hash of the FULL on-disk surface at the moment of the settle —
    /// the same file set <see cref="HashAtLoad"/> covers, read again. Durably recorded as the journal
    /// entry's <c>definitionHashAtSettle</c>.
    /// </summary>
    public required string HashAtSettle { get; init; }

    /// <summary>
    /// Which definition files moved, over the IGNORE-LIST-FILTERED surface: the gate's whole verdict, not a
    /// decoration on it. Never empty — an empty diff is the gate staying silent.
    /// </summary>
    public IReadOnlyList<ChangedDefinitionFile> MovedFiles { get; init; } = [];
}

/// <summary>
/// One definition file that moved: the Tier-2 breakdown of a <see cref="DriftedTask"/> (§7.2) and the
/// per-file verdict of a <see cref="DivergedTask"/> (#556). One shape for both, deliberately — §6.6's
/// "C is A's finding delivered one run earlier" is only true if the two halts speak the same vocabulary.
/// </summary>
public sealed record ChangedDefinitionFile
{
    /// <summary>The file's path relative to the task folder (e.g. <c>guardrails/03-covers.ps1</c>, <c>action.prompt.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>How it drifted: <c>added</c>, <c>removed</c>, or <c>modified</c>.</summary>
    public required string Change { get; init; }

    /// <summary>Lines added (approximate line-multiset delta); null when not a modification/addition.</summary>
    public int? Added { get; init; }

    /// <summary>Lines removed (approximate line-multiset delta); null when not a modification/removal.</summary>
    public int? Removed { get; init; }
}

/// <summary>
/// Carries why a run was aborted by an unexpected infrastructure fault (issue #150). The
/// <see cref="Headline"/> is the one-line human diagnostic the CLI shows; <see cref="Detail"/>
/// is the full exception text written to the run logs (a dev tool keeps the detail — just not as
/// the headline). <see cref="Remedy"/> is an actionable next step.
/// </summary>
public sealed record RunAbort
{
    /// <summary>One-line, human-readable summary of what went wrong (the console headline).</summary>
    public required string Headline { get; init; }

    /// <summary>An actionable next step for the human (e.g. "run git online", "fix the hook").</summary>
    public required string Remedy { get; init; }

    /// <summary>The full fault text (typically the exception's ToString()) for the run logs.</summary>
    public required string Detail { get; init; }
}
