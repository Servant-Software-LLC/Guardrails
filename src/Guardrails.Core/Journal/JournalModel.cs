using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// The on-disk shape of <c>state/run.json</c> (SSOT §7). Serialized with camelCase
/// property names and the SSOT's kebab-case status/outcome strings (via the converters in
/// <see cref="JournalJson"/>). All fields are present so the journal round-trips losslessly.
/// </summary>
public sealed record JournalDocument
{
    /// <summary>Schema version of <c>run.json</c> (SSOT §7: 1).</summary>
    public int Version { get; init; } = 1;

    /// <summary>Stable id for this run, e.g. <c>2026-06-10T16-22-31Z-a1b2</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>SHA-256 over guardrails.json + all task.json, prefixed <c>sha256:</c>. Mismatch on resume ⇒ warning.</summary>
    public required string PlanHash { get; init; }

    /// <summary>The next merge sequence to hand out (monotonic; SSOT §6.3 / §7).</summary>
    public long NextMergeSequence { get; init; } = 1;

    /// <summary>Per-task records, keyed by task id.</summary>
    public IReadOnlyDictionary<string, TaskJournalEntry> Tasks { get; init; } =
        new Dictionary<string, TaskJournalEntry>();

    /// <summary>
    /// OPTIONAL top-level pre-DAG preflight phase result (SSOT §7, the two-scope preflights F9 split). The
    /// pre-DAG phase runs BEFORE any task is scheduled; a failure halts the run (exit 2). Additive and
    /// backward-compatible: a plan WITHOUT the feature OMITS this section (an older reader ignores it), so
    /// it is written only when present — never serialized as <c>null</c> noise (see the
    /// <see cref="JsonIgnoreAttribute"/>). The existing <see cref="Tasks"/> shape is untouched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanPreflightsSection? PlanPreflights { get; init; }

    /// <summary>
    /// OPTIONAL top-level terminal plan-guardrail gate result (SSOT §7, F9): the terminal
    /// <c>&lt;plan&gt;/guardrails/</c> gate evaluated on the merged plan-branch HEAD; a failure halts the
    /// run (exit 2). Additive and backward-compatible on the same terms as <see cref="PlanPreflights"/> —
    /// absent (not null) on a plan without the feature.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanGuardrailsSection? PlanGuardrails { get; init; }

    /// <summary>
    /// OPTIONAL, append-only, unified autonomy-policy decision log (SSOT §2.1/§7): one entry per decision
    /// boundary, <c>boundary</c>-discriminated (M1 emits only <c>drift</c> — a Part C safe-drift rewind's
    /// audit, whether prompted-<c>y</c>, <c>--autonomy auto</c>-authorized, or via the manual scoped
    /// <c>reset</c>; the <c>wave</c>/<c>task</c> boundaries append here in M2/M3). This is the canonical
    /// durable store (it replaces the pre-fold <c>driftResolutions[]</c> section). Additive and
    /// backward-compatible on the same terms as <see cref="PlanPreflights"/> — absent (not <c>null</c>
    /// noise) on a run that recorded no decision.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Execution.DecisionEntry>? Decisions { get; init; }

    /// <summary>
    /// OPTIONAL per-wave completion + phase record for a WAVED plan (SSOT §7/§14.5/§14.6), keyed by wave
    /// dir. Each entry records the wave's completion <see cref="WaveJournalEntry.Status"/>, its
    /// <c>WaveDefinitionHash</c> at completion (for the wave-drift check on resume, §14.6), and its
    /// entry/exit phase markers (which mirror <see cref="PlanPreflights"/>/<see cref="PlanGuardrails"/>
    /// exactly, one instance per wave). Additive and backward-compatible: a FLAT plan OMITS it entirely
    /// (absent, never <c>null</c> noise), so an older reader ignores it and the <see cref="Tasks"/> shape
    /// is untouched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, WaveJournalEntry>? Waves { get; init; }

    /// <summary>
    /// OPTIONAL cumulative OVERHEAD prompt spend (SSOT §7/§9.2, issues #269/#314) that is NOT a task
    /// attempt — the three harness-internal prompt-spend sources that fire BETWEEN (or outside) a task's
    /// attempts, so charging them as synthetic <see cref="AttemptRecord"/>s would corrupt attempt
    /// numbering: (1) the overwatcher's diagnose prompts (#269), (2) the AI-merge worker at each union
    /// (#314), and (3) the terminal needs-human triage (#314). It is folded into the run's cumulative cost
    /// by <see cref="JournalCost.Total"/> so it BOTH counts toward the <c>maxCostUsd</c> gate
    /// (<see cref="RunJournal.CurrentCostUsd"/>) AND appears in the reported total. Additive and
    /// backward-compatible: absent (not <c>null</c> noise) until the first overhead spend.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OverheadCostUsd { get; init; }

    /// <summary>
    /// OPTIONAL machine-readable reason the run STOPPED at a deterministic GATE (SSOT §7, issue #432): the
    /// pre-DAG Full Flight Checks, a wave ENTRY/EXIT gate, or the terminal plan gate. A gate halt settles no
    /// task, so without this section a halted run's <c>tasks{}</c> is a wall of silent <c>pending</c>
    /// entries with the cause recorded only on the operator's terminal. Additive and backward-compatible:
    /// absent (not <c>null</c> noise) on a run that did not halt at a gate, and CLEARED on resume by
    /// <see cref="RunJournal.LoadOrCreate"/> so a stale halt is never mistaken for the current run's.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunHalt? Halt { get; init; }

    /// <summary>
    /// OPTIONAL record of the end-of-run DELIVERY decision and its outcome (SSOT §7, issue #542) — whether
    /// this run's verified work reached the user's branch, and if not, why not.
    /// <para>
    /// <b>The gap this closes.</b> Everything else about a run is durable here — every task, attempt, cost,
    /// gate and decision — but the one outcome that determines whether the work is ANYWHERE was recorded
    /// only on the operator's terminal, in the <c>*** WORK NOT DELIVERED ***</c> banner (#340). Close the
    /// terminal and nothing on disk answered "did this run deliver?"; the only remaining signal was noticing
    /// later that a plan branch was unmerged. That bit me exactly as described: a wholly-green run was read
    /// as shipped, and two issues were closed against a branch that had never been merged.
    /// </para>
    /// <para>
    /// This does NOT replace the banner, which is the right operator surface and is working. It makes the
    /// same fact machine-readable and durable — for post-mortem, and for #496's unattended pipeline, which
    /// has no console for a banner to print to.
    /// </para>
    /// Additive and backward-compatible on the same terms as <see cref="PlanPreflights"/>: absent (never
    /// <c>null</c> noise) on a run that ended before delivery was ever considered.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeliverySection? Delivery { get; init; }

    /// <summary>
    /// OPTIONAL machine, concurrency and version profile probed ONCE for the whole run (plan 30 §3.4) —
    /// host, OS, CPU count, total memory, resolved parallelism, and the harness/skill versions the run
    /// executed under. DOCUMENT grain, not per-task or per-attempt: every one of these facts is identical
    /// for every task this run touches, so it is recorded once here rather than repeated on every
    /// <see cref="TaskJournalEntry"/> or <see cref="AttemptRecord"/>. Additive and backward-compatible on
    /// the same terms as <see cref="PlanPreflights"/> — absent (never <c>null</c> noise) on a run that
    /// probed none of this.
    /// <para>
    /// <b>Mechanical hazard.</b> This member's name shadows <c>System.Environment</c> within
    /// <see cref="JournalDocument"/>'s scope. That is safe today — this file uses
    /// <c>System.Environment</c> nowhere — but do not "fix" the shadow by renaming the member, and do not
    /// introduce a <c>System.Environment</c> use into this record.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunEnvironment? Environment { get; init; }

    // NOTE (issue #419): the Windows short-junction root is NO LONGER journaled. It was the field that made
    // the junction durable RUN STATE (forcing a resume onto the same .a..z letter and a sweep as the only
    // reclaim), which is the leak #407/#419 chased. The junction is now a process-scoped cwd alias
    // (WorktreeJunction/WorktreeJunctionLifetime): fresh per run, released on every recoverable exit, and
    // re-derived by the deterministic segment subpath on resume. An OLD run.json that still carries a
    // "worktreeJunctionRoot" key deserializes clean under this model (JournalJson has no
    // JsonUnmappedMemberHandling.Disallow → the unknown member is skipped) and is simply ignored.
}

/// <summary>
/// The end-of-run DELIVERY record (SSOT §7 <c>delivery</c>, issue #542): did this run's verified work reach
/// the user's branch, and if not, why not.
/// <para>
/// <see cref="Delivered"/> is the one field a consumer needs, and it is deliberately a plain boolean rather
/// than something derived from <see cref="Outcome"/> — "did the work ship?" must be answerable without a
/// reader knowing which outcome tokens count as success. The rest is the detail an operator or a
/// post-mortem needs to act.
/// </para>
/// </summary>
public sealed record DeliverySection
{
    /// <summary>
    /// True IFF this run's work reached the user's branch (a fast-forward or a merge commit). False for
    /// every other case — delivery off, a refused merge, a failed terminal gate, a non-green run.
    /// </summary>
    public required bool Delivered { get; init; }

    /// <summary>
    /// The SSOT §7 token for what happened: <c>fast-forwarded</c> | <c>merged</c> | <c>conflict</c> |
    /// <c>dirty-working-tree</c> | <c>hook-rejected</c> when the merge-back actually ran, or
    /// <c>not-attempted</c> when it never did.
    /// </summary>
    public required DeliveryOutcome Outcome { get; init; }

    /// <summary>
    /// Why delivery did not happen, in words, when <see cref="Delivered"/> is false — the durable form of
    /// what the console banner says. Null on a delivered run. This is the field that answers the question
    /// the banner answered only in scrollback.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    /// The branch the verified work is sitting on when it was NOT delivered (e.g.
    /// <c>guardrails/27-operator-visibility</c>) — the thing a later reader has to merge by hand, and the
    /// thing a <c>--fresh</c> would destroy. Null when there is no separate plan branch (serial mode) or
    /// when the work was delivered.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanBranch { get; init; }

    /// <summary>
    /// The user's branch the work was delivered TO, when it was (mirrors
    /// <see cref="Execution.RunReport.DeliveredToBranch"/>). Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeliveredToBranch { get; init; }

    /// <summary>
    /// Free-text detail carried by a refusing outcome (mirrors
    /// <see cref="Execution.RunReport.MergeOnSuccessDetail"/>): a hook's stderr for
    /// <see cref="DeliveryOutcome.HookRejected"/>, the blocking tracked paths for
    /// <see cref="DeliveryOutcome.DirtyWorkingTree"/>, or the two branch names for
    /// <see cref="DeliveryOutcome.BranchMoved"/>. Null when the outcome carries none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    /// <summary>
    /// Present ONLY when an operator override (<c>--merge-on-success</c>, SSOT §5.3) delivered this run's
    /// work PAST the autonomous-mode interlock — the machine decision that would otherwise have held it on
    /// the plan branch (issue #597). Absent (never <c>null</c> noise) on every run where the override was
    /// not needed, which is nearly all of them.
    /// <para>
    /// <b>The gap this closes.</b> The override reached <see cref="Execution.RunReport"/> and the console
    /// banner and STOPPED there: nothing under <c>Journal/</c> persisted it. Console output is ephemeral
    /// unless someone thought to redirect it, so a week later a forced delivery was indistinguishable from
    /// a delivery that was never suppressed at all — for the one action in the system that deliberately
    /// bypasses a safety interlock. That is this repo's recurring defect class (a mechanism whose evidence
    /// exists only where nobody kept it) sitting on its own audit trail.
    /// </para>
    /// <para>
    /// It is recorded whenever the override was in force at the delivery attempt, INCLUDING an attempt the
    /// merge then refused (<see cref="DeliveryOutcome.Conflict"/> and friends): "the operator overrode the
    /// interlock and the merge then conflicted" is a true and useful thing for a post-mortem to be able to
    /// read.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ForcedDeliveryRecord? ForcedPastDecision { get; init; }
}

/// <summary>
/// WHICH machine decision an operator override delivered past (SSOT §7 <c>delivery.forcedPastDecision</c>,
/// issue #597). Carries the same pair the console banner names — the decision token and the SUBJECT, the
/// task or wave the machine decided at — plus the boundary, so the entry can be found in the document's own
/// <c>decisions[]</c> without re-deriving anything from prose.
/// </summary>
public sealed record ForcedDeliveryRecord
{
    /// <summary>
    /// The overridden decision's token: <c>proceeded-best-guess</c> or <c>proceeded-unreviewed</c> — the
    /// two <c>decisions[]</c> tokens that suppress delivery (<c>RunOutcomePolicy.SuppressingDecision</c>).
    /// </summary>
    public required string Decision { get; init; }

    /// <summary>
    /// The unit the decision concerned — the task id or wave dir. This is the half a reader acts on: it is
    /// what tells them WHERE the machine judged, so they can go and check that judgment after the fact.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// The decision's boundary discriminator (<c>task</c> / <c>wave</c> / <c>drift</c>), so the matching
    /// entry in the document's <c>decisions[]</c> is locatable without guessing.
    /// </summary>
    public required string Boundary { get; init; }
}

/// <summary>
/// What the end-of-run delivery did (SSOT §7 <c>delivery.outcome</c>, issue #542). Mirrors
/// <see cref="Execution.MergeOnSuccessResult"/> and adds the case that enum cannot express — the merge-back
/// never ran at all, which is by far the most common reason work is undelivered and was previously
/// represented only by a null nobody journaled.
/// </summary>
public enum DeliveryOutcome
{
    /// <summary>The merge-back never ran (delivery off, a failed terminal gate, or a non-green run).</summary>
    NotAttempted,

    /// <summary>The user's branch was fast-forwarded to the plan branch tip.</summary>
    FastForwarded,

    /// <summary>A merge commit combined the plan branch into the user's branch.</summary>
    Merged,

    /// <summary>The merge conflicted; the user's branch was not modified.</summary>
    Conflict,

    /// <summary>Uncommitted changes to tracked files the merge would update refused it (issue #448).</summary>
    DirtyWorkingTree,

    /// <summary>One of the user's git hooks rejected the merge commit (issues #149/#150).</summary>
    HookRejected,

    /// <summary>
    /// The checkout was no longer on the branch the run pinned as its delivery target, so nothing was
    /// merged (issue #588). Includes a detached HEAD and an unreadable HEAD.
    /// </summary>
    BranchMoved
}

/// <summary>
/// One wave's journal record (SSOT §7 <c>waves.&lt;waveDir&gt;</c> / §14.5) — the wave-level analogue of
/// <see cref="TaskJournalEntry"/>. The entry/exit phase markers reuse the plan-phase section shapes so the
/// wave gates are byte-identical to the whole-plan phases they mirror (design §14.6 "mirror planPreflights /
/// planGuardrails exactly").
/// </summary>
public sealed record WaveJournalEntry
{
    /// <summary>Current wave status (SSOT §14.5).</summary>
    public required WaveStatus Status { get; init; }

    /// <summary>
    /// The wave's <c>WaveDefinitionHash</c> (SSOT §7.2/§14.5) stamped when it settled Completed. On resume
    /// the harness recomputes the current hash and, if it no longer matches, treats a COMPLETED wave as
    /// drifted (halt/resolve per <c>autonomyPolicy</c>, §14.6). OPTIONAL/additive — an entry predating this
    /// field omits it (absent ⇒ "unknown — assume unchanged", never forces a re-run storm).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; init; }

    /// <summary>
    /// The plan-branch sha of this wave's <c>Guardrails-Wave:</c> marker commit (SSOT §14.5, decision E),
    /// stamped when the wave settled Completed. It is the wave-scoped-rewind anchor: rewinding wave N (+
    /// downstream) resets the plan branch to wave (N-1)'s marker sha. OPTIONAL/additive — absent in serial
    /// mode (no plan branch) and on an entry predating this field. The durable <c>Guardrails-Wave:</c>
    /// trailer on the branch is the backstop when the journal is lost (§14.5).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MarkerSha { get; init; }

    /// <summary>
    /// The wave ENTRY preflight phase marker (SSOT §14.6) — the plan-preflight phase scoped to this wave,
    /// skip-once-per-hash. Reuses <see cref="PlanPreflightsSection"/> (status/planHash/evaluatedAt/checks).
    /// Absent until the entry gate has run for this wave.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanPreflightsSection? Entry { get; init; }

    /// <summary>
    /// The wave EXIT / terminal gate marker (SSOT §14.6) — the plan-guardrail phase scoped to this wave,
    /// always re-evaluated on the current HEAD. Reuses <see cref="PlanGuardrailsSection"/>
    /// (status/planHash/failedChecks). Absent until the exit gate has run for this wave.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanGuardrailsSection? Exit { get; init; }
}

/// <summary>
/// The pre-DAG preflight phase result (SSOT §7 top-level <c>planPreflights</c>, two-scope preflights F9
/// split). <c>planHash</c>-keyed so it self-scopes to the plan it evaluated.
/// </summary>
public sealed record PlanPreflightsSection
{
    /// <summary>The phase status (<c>passed</c> or <c>plan-preflight-failed</c>).</summary>
    public required PlanPhaseStatus Status { get; init; }

    /// <summary>The plan hash the preflight phase evaluated against (SSOT §7; mirrors <see cref="JournalDocument.PlanHash"/>).</summary>
    public required string PlanHash { get; init; }

    /// <summary>UTC time the preflight phase was evaluated (ISO-8601).</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>The individual preflight checks that ran (name + pass/fail + optional reason).</summary>
    public IReadOnlyList<PlanPreflightCheck> Checks { get; init; } = [];

    /// <summary>
    /// OPTIONAL plan-relative, forward-slash path to this phase's captured per-check output (SSOT §8, issue
    /// #432) — <c>logs/&lt;runId&gt;/preflights</c> for the plan-level phase, or
    /// <c>logs/&lt;runId&gt;/&lt;waveDir&gt;/preflights</c> for a wave ENTRY gate. Each check's
    /// <c>stdout.log</c>/<c>stderr.log</c>/<c>result.json</c> sits in a <c>&lt;check-name&gt;/</c>
    /// subdirectory. Additive — absent on a marker written before #432 (or when no run id was available).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogDir { get; init; }
}

/// <summary>One pre-DAG preflight check result (SSOT §7 <c>planPreflights.checks[]</c>).</summary>
public sealed record PlanPreflightCheck
{
    /// <summary>The check's name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the check passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>The actionable failure reason; omitted when the check passed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>
/// The terminal plan-guardrail gate result on the merged plan-branch HEAD (SSOT §7 top-level
/// <c>planGuardrails</c>, two-scope preflights F9 split). <c>planHash</c>-keyed.
/// </summary>
public sealed record PlanGuardrailsSection
{
    /// <summary>The gate status (<c>passed</c> or <c>plan-guardrail-failed</c>).</summary>
    public required PlanPhaseStatus Status { get; init; }

    /// <summary>The plan hash the terminal gate evaluated against (SSOT §7).</summary>
    public required string PlanHash { get; init; }

    /// <summary>The guardrail checks that failed (name + reason); empty unless <see cref="Status"/> is plan-guardrail-failed.</summary>
    public IReadOnlyList<FailedGuardrail> FailedChecks { get; init; } = [];

    /// <summary>
    /// OPTIONAL UTC time the gate was evaluated (ISO-8601), mirroring
    /// <see cref="PlanPreflightsSection.EvaluatedAt"/> (issue #432): a halted run's journal must say WHEN
    /// the gate ran, not only that it did. Additive — absent on a marker written before #432.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EvaluatedAt { get; init; }

    /// <summary>
    /// OPTIONAL per-check results for EVERY check this gate ran — passing ones included — in the same
    /// <c>{ name, passed, reason? }</c> shape as <see cref="PlanPreflightsSection.Checks"/> (issue #432).
    /// <see cref="FailedChecks"/> (kept verbatim for existing readers) names only the failures, which
    /// cannot distinguish "the gate ran three checks and the third failed" from "the gate ran one check";
    /// this list can. Additive — empty on a marker written before #432.
    /// </summary>
    public IReadOnlyList<PlanPreflightCheck> Checks { get; init; } = [];

    /// <summary>
    /// OPTIONAL plan-relative, forward-slash path to this gate's captured per-check output (SSOT §8, issue
    /// #432) — <c>logs/&lt;runId&gt;/guardrails</c> for the terminal plan gate, or
    /// <c>logs/&lt;runId&gt;/&lt;waveDir&gt;/guardrails</c> for a wave EXIT gate. Additive — absent on a
    /// marker written before #432 (or when no run id was available).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogDir { get; init; }

    /// <summary>
    /// The #175 merge-collision advisory (SSOT §3.3, issue #205): when the terminal gate fails and ≥2
    /// tasks have OVERLAPPING <c>writeScope</c> on a shared file, this names the offending task pair(s) +
    /// the shared path(s) so a human sees <i>"this looks like a merge collision between task A and task B
    /// on &lt;file&gt;"</i> rather than a bare build error. Structural + advisory (derived purely from the
    /// writeScope-overlap topology, never the compiler error text). OPTIONAL and additive — omitted (not
    /// null noise) when the gate passed or no two writeScopes overlap.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollisionHint { get; init; }
}

/// <summary>One task's journal record (SSOT §7 <c>tasks.&lt;id&gt;</c>).</summary>
public sealed record TaskJournalEntry
{
    /// <summary>Current status.</summary>
    public required TaskStatus Status { get; init; }

    /// <summary>The merge sequence assigned when this task's fragment merged; null until then.</summary>
    public long? MergeSequence { get; init; }

    /// <summary>
    /// The task's <c>TaskDefinitionHash</c> (SSOT §7.2, issue #274 Part A) stamped at its most recent
    /// SUCCESSFUL settle: a <c>sha256:</c>-prefixed hash of <c>task.json</c> + the resolved action file +
    /// <c>guardrails/**</c> + <c>preflights/**</c>. On resume the harness recomputes the current hash and,
    /// if it no longer matches this recorded one, halts with a definition-drift report instead of silently
    /// reusing the stale cached segment. OPTIONAL and additive — a journal entry predating this field OMITS
    /// it (serialized only when non-null via <see cref="JsonIgnoreAttribute"/>); an absent recorded hash is
    /// treated as "unknown — assume unchanged" on resume, so an upgrade never forces a re-run storm.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHash { get; init; }

    /// <summary>
    /// The task's full-surface definition hash recomputed at settle-time by the executed-definition
    /// divergence gate (SSOT §7.2, plan 32 §6.3), present ONLY when that gate FIRED on this settle — never
    /// merely because it differs from <see cref="DefinitionHash"/>. OPTIONAL and additive: an unedited run
    /// OMITS it (serialized only when non-null via <see cref="JsonIgnoreAttribute"/>), so a settle the gate
    /// had nothing to say about gains no new key at all.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionHashAtSettle { get; init; }

    /// <summary>
    /// The task's fingerprint bucket (plan 30 §3.2) — <c>test-authoring</c> | <c>implementation</c> |
    /// <c>structural</c> | <c>code+tests</c> | <c>documentation</c> | <c>no-write</c>, derived from two
    /// things the harness already holds at attempt time: the task's <c>writeScope</c> roots and its
    /// guardrail archetypes. TASK grain, not attempt grain: both inputs are constant across the task's own
    /// retries within one run, so this hangs off <see cref="TaskJournalEntry"/> beside
    /// <see cref="DefinitionHash"/> rather than being repeated on every <see cref="AttemptRecord"/>.
    /// <para>
    /// The report's own legend states the constraint this field exists to satisfy: <i>"a bucket is a fact
    /// about a task, never one read off its name."</i> It is never derived from the task's id or
    /// description. OPTIONAL and additive — absent (never <c>null</c> noise) until the harness computes
    /// one, and on every journal written before this field existed.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bucket { get; init; }

    /// <summary>Attempt records in attempt order (1-based).</summary>
    public IReadOnlyList<AttemptRecord> Attempts { get; init; } = [];

    /// <summary>
    /// OPTIONAL, append-only log of the class-(b) transient PAUSES this task took (SSOT §7
    /// <c>transientPauses[]</c>, issue #515) — one entry per <see cref="Execution.TransientBackoff"/> backoff,
    /// written AT THE PAUSE rather than at any settle.
    /// <para>
    /// <b>The gap this closes.</b> A transient that paused and then RESOLVED — the #115 happy path, and the
    /// entire point of the feature — left no durable trace whatever: only the EXHAUSTED path
    /// (<c>AttemptJournaler.RateLimitExhausted</c>) recorded anything, and the resolving pause reached the
    /// <see cref="Execution.IRunObserver"/> and nothing else. So "did this run hit provider trouble?" was
    /// unanswerable once the console scrolled away, which is the difference between "the model is flaky
    /// today" and "my plan is wrong". It also defeats the feature's own justification: #115 pauses WITHOUT
    /// consuming retry budget because a provider stall is not the task's fault, and that trade is only
    /// auditable if the pauses are counted. A task that quietly paused six times was, in every durable
    /// record, identical to one that ran clean.
    /// </para>
    /// <para>
    /// <b>TASK grain, not attempt grain, and that is mechanical.</b> A pause happens BETWEEN attempt
    /// launches — the paused attempt re-runs under the SAME attempt number, and no
    /// <see cref="AttemptRecord"/> exists for it yet — so there is no attempt record to hang it off at the
    /// moment it happens. The budget it spends is per-task too (one
    /// <see cref="Execution.TransientBackoff"/> per task), which is the same grain. Each entry names the
    /// attempt number it paused, so the association is not lost.
    /// </para>
    /// <para>
    /// Additive and backward-compatible: absent (never <c>null</c> noise) on the overwhelming majority of
    /// tasks, which never pause, and in every journal written before this field existed.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TransientPauseRecord>? TransientPauses { get; init; }
}

/// <summary>
/// ONE class-(b) transient pause (SSOT §7 <c>tasks.&lt;id&gt;.transientPauses[]</c>, issues #115/#515): the
/// harness met a retryable provider condition (429/503/529, "overloaded", a rate/session/usage limit), backed
/// off, and re-ran the SAME attempt without consuming the retry budget.
/// <para>
/// Written BEFORE the wait, not after it. A run killed mid-pause must still say it was pausing and why —
/// recording only on the far side of the delay would lose exactly the pauses that were long enough to matter.
/// </para>
/// </summary>
public sealed record TransientPauseRecord
{
    /// <summary>1-based ordinal of this pause within the task's own pause budget.</summary>
    public required int Pause { get; init; }

    /// <summary>
    /// The attempt number that was paused and is about to be re-run. NOT a new attempt: the #115 contract is
    /// that a transient pause does not consume the retry budget, so the re-run reuses this number and this
    /// attempt's log dir.
    /// </summary>
    public required int Attempt { get; init; }

    /// <summary>UTC time the pause BEGAN (ISO-8601) — stamped before the wait, see the type remarks.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// The operator-facing cause, as the runner reported it — the same text
    /// <see cref="Execution.IRunObserver.PromptPaused"/> receives.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The backoff this pause waited, in seconds — the <see cref="Execution.TransientBackoff.NextDelay"/>
    /// value, already clamped to the task's remaining pause budget. Seconds as a number rather than a
    /// <c>TimeSpan</c> because <c>run.json</c> is a wire format read by humans and by tooling that never
    /// links against this assembly.
    /// </summary>
    public required double WaitSeconds { get; init; }

    /// <summary>
    /// The reset hint the runner parsed out of the provider's message ("3pm", "in 2 hours"), when it gave
    /// one. Absent (never <c>null</c> noise) when it did not. It is ALSO folded into
    /// <see cref="Reason"/>'s prose — this field is the machine-readable half, so a reader does not have to
    /// re-parse the sentence the harness already parsed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetHint { get; init; }
}

/// <summary>One attempt of one task (SSOT §7 attempt record).</summary>
public sealed record AttemptRecord
{
    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC start time (ISO-8601).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC end time (ISO-8601).</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>The action's exit code (null if the action never ran).</summary>
    public int? ActionExitCode { get; init; }

    /// <summary>The attempt outcome.</summary>
    public required AttemptOutcome Outcome { get; init; }

    /// <summary>Failed guardrails (name + actionable reason); empty unless <see cref="Outcome"/> is guardrail-failed.</summary>
    public IReadOnlyList<FailedGuardrail> FailedGuardrails { get; init; } = [];

    /// <summary>Prompt-attempt total cost in USD; null for deterministic attempts (and all of M3).</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Path to this attempt's log dir, relative to the plan dir (SSOT §7/§8).</summary>
    public required string LogDir { get; init; }

    /// <summary>
    /// Per-attempt provenance the harness knows when it launches the attempt (issue #198): the resolved
    /// model the agent ran on, the segment worktree it wrote in, and the base commit it forked from.
    /// OPTIONAL and additive — a script attempt, a serial-mode attempt, or an older journal simply
    /// OMITS this section (serialized only when present via <see cref="JsonIgnoreAttribute"/>), so it is
    /// backward-compatible and never adds <c>null</c> noise to <c>run.json</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptProvenance? Provenance { get; init; }

    /// <summary>
    /// OPTIONAL per-attempt token volume (SSOT §7 / DoR §12.4, model tiering #201 / #230-lite): the
    /// tokens-only accounting surface, so a COSTLESS provider — a local endpoint, a flat-rate
    /// subscription — still shows how much work an attempt actually did. <see cref="CostUsd"/> answers
    /// "what did this cost"; on such a provider the honest answer is <c>0</c>, which is why spend alone
    /// cannot carry the per-tier report. Additive and backward-compatible: absent (never <c>null</c>
    /// noise) for a script attempt, for a runner that reports no usage, and in every older journal.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptUsage? Usage { get; init; }

    /// <summary>
    /// OPTIONAL count of agent turns this attempt used (plan 30 §3.4) — the figure the plan names as
    /// "computed, printed and discarded today". Attempt-grain envelope fact, so it hangs DIRECTLY off
    /// <see cref="AttemptRecord"/> rather than off <see cref="Provenance"/> — the exposed case, unlike
    /// <see cref="AttemptProvenance.ModelDigest"/>/<see cref="AttemptProvenance.RouteWarm"/> above: a member
    /// declared only here reaches the serial <c>AttemptJournaler</c> path but silently VANISHES from
    /// <c>Scheduler.RecordSucceededSettle</c> (the DEFAULT worktree mode) unless
    /// <c>Execution.PendingAttempt</c> also carries a matching member — see
    /// <see cref="Execution.PendingAttempt.Usage"/>'s doc comment for the worked example of exactly this
    /// defect and its fix. Task <c>04-extend-the-transport-record-shape</c> adds the
    /// <c>PendingAttempt.Turns</c>/<c>Segments</c> carriers and task
    /// <c>16-carry-phase1-facts-through-the-worktree-settle</c> wires them; until then, a value recorded
    /// here in worktree mode does not reach the journal. Additive and backward-compatible: absent (never
    /// <c>null</c> noise) for a script attempt, a runner that reports no turn count, and in every older
    /// journal.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Turns { get; init; }

    /// <summary>
    /// OPTIONAL segmented wall-clock duration for this attempt (plan 30 §3.4) — how much of the elapsed
    /// time the action itself ran versus the guardrail suite that graded it (<see cref="AttemptSegments"/>).
    /// Attempt-grain envelope fact; hangs directly off <see cref="AttemptRecord"/> on the same terms, and
    /// carries the same worktree-mode exposure, as <see cref="Turns"/> — see that member's comment.
    /// Additive and backward-compatible: absent (never <c>null</c> noise) for a script attempt, a runner
    /// that reports no segment timings, and in every older journal.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptSegments? Segments { get; init; }

    /// <summary>
    /// OPTIONAL agent-asserted classification of a <c>needs-human</c> attempt (SSOT §7/§9, issue #485):
    /// <c>blocked-work</c> ("I cannot complete this work") or <c>defective-guardrail</c> ("this check is
    /// itself wrong"). Absent means UNCLASSIFIED — the harness invents no default. Journaled because
    /// <c>guardrails status</c> and the static log-site export read ONLY the journal; without it, a halt's
    /// claim would survive the run in <c>action-out-fragment.json</c> alone. Additive and backward-compatible:
    /// omitted (never <c>null</c> noise) for every non-needs-human attempt and in every pre-#485 journal.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NeedsHumanKind { get; init; }

    /// <summary>
    /// OPTIONAL record of the <c>needsHarnessWrite</c> escape hatch this attempt asked for (SSOT §7/§9,
    /// issue #532 gap 1): what was requested, how much of it landed, and — when nothing did — why.
    /// <para>
    /// <b>The gap this closes.</b> The dispositions already existed as first-class values
    /// (<c>HarnessWriteOutcome.Rejected</c> / <c>.Denied</c> / <c>.NotApplied</c> / <c>.Failed</c>, each
    /// carrying a reason, plus the applied paths) — they were computed, spent on retry feedback, and then
    /// DROPPED. So a task that requested harness writes on three consecutive attempts and had all three
    /// silently ignored (#531) left NOTHING in <c>run.json</c> saying a write had ever been requested, let
    /// alone what became of it. Diagnosing it meant reading raw <c>action-out-fragment.json</c> out of the
    /// log dir and then reading harness SOURCE to learn where the key is looked up. That is archaeology,
    /// and it is exactly what a self-healing agent (#529) cannot do cheaply.
    /// </para>
    /// <para>
    /// Follows the <see cref="NeedsHumanKind"/> precedent one column over: a fragment-derived
    /// classification, canonicalized at the journal boundary and recorded on the attempt, because
    /// <c>guardrails status</c> and the static log-site export read ONLY the journal.
    /// </para>
    /// <para>
    /// Additive and backward-compatible: absent (never <c>null</c> noise) on every attempt that requested
    /// no harness write — which is nearly all of them — and in every journal written before this field
    /// existed. It rides <see cref="Execution.PendingAttempt"/> as well, or it would land in serial mode
    /// and silently vanish in the DEFAULT worktree mode; see that record's own doc comments for the worked
    /// example of that exact defect.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HarnessWriteRecord? HarnessWrite { get; init; }
}

/// <summary>
/// What happened to ONE <c>needsHarnessWrite</c> batch (SSOT §7 <c>harnessWrite.disposition</c>, issue
/// #532). Every value has exactly one producer in <c>Execution.HarnessWrite.ValidateAndApply</c>, so this
/// is a record of what the harness DID, never a guess reconstructed from a message afterwards.
/// </summary>
public enum HarnessWriteDisposition
{
    /// <summary>Every entry validated and was written.</summary>
    Applied,

    /// <summary>Refused by a containment/scope check — a workspace escape, or a path outside <c>writeScope</c>.</summary>
    Rejected,

    /// <summary>Refused by the #321 permission-file carve-out: the harness never writes <c>.claude/settings*.json</c> on an agent's behalf.</summary>
    Denied,

    /// <summary>
    /// In bounds and permitted, but not applicable as written (#437/#445): an unusable payload, an anchor
    /// that matched zero or several times, <c>edits</c> against a file that does not exist, full-content
    /// mode against a target too large for it, or two entries targeting one file. Nothing was written and
    /// every target is byte-identical; the agent can fix it by re-emitting.
    /// </summary>
    NotApplied,

    /// <summary>Validated and permitted, but the write itself hit an IO fault.</summary>
    Failed
}

/// <summary>
/// The <c>needsHarnessWrite</c> disposition recorded on an attempt (SSOT §7 <c>harnessWrite</c>, issue
/// #532 gap 1) — the durable answer to "was a harness write requested here, and what became of it?".
/// </summary>
public sealed record HarnessWriteRecord
{
    /// <summary>How many file entries the request named (a single-object payload is a batch of one).</summary>
    public required int Requested { get; init; }

    /// <summary>
    /// How many were actually written. Either <see cref="Requested"/> or <c>0</c> and never anything
    /// between, because the batch is ATOMIC (#445) — but it is recorded as a COUNT rather than a boolean
    /// so the pair reads as the evidence it is: <c>"requested": 3, "applied": 0</c> says at a glance both
    /// that work was asked for and that none of it happened.
    /// </summary>
    public required int Applied { get; init; }

    /// <summary>What happened to the batch as a whole.</summary>
    public required HarnessWriteDisposition Disposition { get; init; }

    /// <summary>
    /// The actionable reason the batch was not applied, verbatim as the agent was told it. Absent when
    /// <see cref="Disposition"/> is <see cref="HarnessWriteDisposition.Applied"/>.
    /// <para>
    /// BATCH grain, deliberately, even though the request may name several files: the batch is atomic, so
    /// one reason governs every entry, and copying that one string onto each of them would be a second
    /// copy of a single fact — the very thing this file refuses a <c>resolvedModel</c> key for. For a
    /// multi-entry batch the reason already names the offending array index.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    /// One entry per path the request named, in the order the agent listed them — the half a reader ACTS
    /// on, because it says WHICH files were at stake. Empty only when the payload was so malformed it
    /// named no path at all.
    /// </summary>
    public IReadOnlyList<HarnessWriteEntry> Entries { get; init; } = [];
}

/// <summary>One file named by a <c>needsHarnessWrite</c> request (SSOT §7 <c>harnessWrite.entries[]</c>).</summary>
public sealed record HarnessWriteEntry
{
    /// <summary>The destination exactly as the agent spelled it.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// This entry's disposition. Equal to the batch's under today's atomic semantics — recorded per entry
    /// anyway so a reader scanning a multi-file request never has to infer a per-file outcome from a
    /// batch-level one, and so the shape survives if partial application is ever introduced.
    /// </summary>
    public required HarnessWriteDisposition Disposition { get; init; }
}

/// <summary>
/// Token volume for one attempt (SSOT §7 <c>usage</c> / DoR §12.4): the <c>{ inputTokens, outputTokens }</c>
/// pair the per-tier spend line (#230-lite) aggregates alongside cost, so a provider that charges nothing
/// still reports volume.
/// </summary>
public sealed record AttemptUsage
{
    /// <summary>Input (prompt) tokens the attempt consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output (completion) tokens the attempt produced.</summary>
    public int OutputTokens { get; init; }
}

/// <summary>
/// Segmented wall-clock duration for one attempt (plan 30 §3.4) — how much of the attempt's elapsed time
/// the ACTION itself ran versus the GUARDRAILS that graded it, so a slow attempt can be read as "the
/// action was slow" or "the guardrail suite was slow" instead of one undifferentiated span.
/// <para>
/// Every member is nullable for the §15.2 null-versus-zero reason <see cref="AttemptRecord.CostUsd"/>
/// already draws: a runner that reported nothing must not make the journal assert the segment took no
/// time. <c>0</c> is a measurement; absent is an absence.
/// </para>
/// </summary>
public sealed record AttemptSegments
{
    /// <summary>Milliseconds the action itself ran, when measured.</summary>
    public long? ActionMs { get; init; }

    /// <summary>Milliseconds the guardrail suite ran, when measured.</summary>
    public long? GuardrailMs { get; init; }
}

/// <summary>
/// The machine, concurrency and version profile probed once for a whole run (plan 30 §3.4,
/// <see cref="JournalDocument.Environment"/>): the host and OS the run executed on, its CPU/memory
/// envelope, the parallelism the harness actually resolved, and the harness/skill versions in play —
/// context a per-attempt or per-task figure cannot carry on its own, since it is identical across every
/// task in the run.
/// <para>
/// Every member is nullable for the same §15.2 reason as <see cref="AttemptSegments"/>: a runner that
/// probed nothing must not make the journal assert a zeroed machine.
/// </para>
/// </summary>
public sealed record RunEnvironment
{
    /// <summary>The machine's hostname, when probed.</summary>
    public string? Host { get; init; }

    /// <summary>The operating system description, when probed.</summary>
    public string? Os { get; init; }

    /// <summary>The machine's logical CPU count, when probed.</summary>
    public int? CpuCount { get; init; }

    /// <summary>The machine's total physical memory in bytes, when probed.</summary>
    public long? TotalMemoryBytes { get; init; }

    /// <summary>The concurrency the harness actually resolved for this run, when applicable.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>The Guardrails harness version this run executed under, when known.</summary>
    public string? HarnessVersion { get; init; }

    /// <summary>The skill version (plan-breakdown / guardrails-review) this run executed under, when known.</summary>
    public string? SkillVersion { get; init; }
}

/// <summary>
/// WHICH SITE supplied the rung an attempt resolved on (SSOT §7 <c>provenance.tierSource</c> /
/// DoR §12.4, D31). Every v1 value has EXACTLY ONE producer — the enum is a record of what the harness
/// did, not a guess reconstructed afterwards, which is precisely the mistake
/// <c>PlanValidator</c>'s <c>tier != tiering.defaultTier</c> comparison makes (it misattributes a task
/// whose own tier spells the same token as the plan default).
///
/// <para>There is deliberately NO value for the legacy fallback path (DoR §6.1 item 3, D30): a
/// legacy-fallback attempt carries no <c>tierSource</c> AT ALL — nothing resolved and nothing was
/// overridden, so recording a source would be inventing one. (The v2 ladder adds <c>escalated</c>,
/// DoR §12.7.)</para>
/// </summary>
public enum TierSource
{
    /// <summary>
    /// The task's OWN <c>action.tier</c> (or a judge guardrail's frontmatter <c>tier</c>) supplied the
    /// rung — <see cref="Model.TierOrigin.Task"/> at load time.
    /// </summary>
    Task,

    /// <summary>
    /// The task declared no tier of its own and the plan-wide <c>tiering.defaultTier</c> supplied the
    /// rung — <see cref="Model.TierOrigin.PlanDefault"/> at load time.
    /// </summary>
    PlanDefault,

    /// <summary>
    /// A full <c>action.runner</c>/<c>action.model</c> pin bypassed tier resolution entirely (DoR §6.1
    /// item 1, D31). "Bypasses resolution" governs what is SELECTED, not what is LOGGED: the attempt
    /// still records why it took the route it took, and <c>provenance.tier</c> is absent because no rung
    /// resolved.
    /// </summary>
    Override
}

/// <summary>
/// Per-attempt provenance recorded in <c>run.json</c> and mirrored to
/// <c>&lt;attempt&gt;/attempt-provenance.json</c> (SSOT §7/§8, issue #198): the facts the harness already
/// knows at attempt launch. Every field is optional so a script attempt (no model, serial mode with no
/// segment) records only what applies. It records WHAT ran WHERE without re-deriving it from logs —
/// and, since #382, under WHICH PERMISSIONS, split into what the plan declared and what the harness
/// injected on top so the effective set is never an unattributable merged list.
/// </summary>
public sealed record AttemptProvenance
{
    /// <summary>
    /// The model the agent ran on — the FULLY RESOLVED <c>--model</c> (issue #200): the task.json
    /// <c>action.model</c> override when the task declares one, else the prompt-runner config's own
    /// <c>model</c>, else the sentinel <c>"(cli default)"</c> when neither is set. Null for a script
    /// attempt (no model).
    ///
    /// <para><b>Stage 2 (model tiering #201) makes this the RESOLVED ROUTE's model</b> — the model of the
    /// block <c>TierResolver</c> selected for this attempt's rung, on top of the pin/legacy sources above.
    /// It stays the ONE resolved-model field: DoR §12.4's provenance delta adds
    /// <see cref="Runner"/>/<see cref="Kind"/>/<see cref="Tier"/>/<see cref="TierSource"/>/<see cref="Effort"/>
    /// AROUND it and deliberately no second <c>resolvedModel</c> key — two fields claiming the same fact
    /// is how they drift.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>
    /// The model the ROUTE ASKED FOR, written ONLY when it DIFFERS from <see cref="Model"/> (#349) — and
    /// absent entirely, on every ordinary attempt, when the two agree.
    ///
    /// <para><b>The one fact <see cref="Model"/> can no longer carry.</b> Once <see cref="Model"/> is
    /// best-known-actual — the model the runner reported itself running on, else the resolved route, else
    /// the sentinel — the request stops being derivable from it. It is not disposable: the request is what
    /// the operator's <c>promptRunners</c> block and <c>tiering</c> config actually selected, so it is the
    /// only evidence that separates "the provider served something else" from "my routing is misconfigured".</para>
    ///
    /// <para><b>Its PRESENCE is the mismatch signal, so there is no separate flag beside it</b> — and no
    /// key at all in the agreeing case. An always-written copy of <see cref="Model"/> destroys the signal
    /// (every attempt then looks like a disagreement) and reinstates exactly what the note above rejects:
    /// two fields claiming the same fact is how they drift. This second field earns its place by carrying
    /// the DISAGREEMENT rather than a duplicate.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedModel { get; init; }

    /// <summary>
    /// The provider-reported model fingerprint (plan 30 §3.3) — a DIFFERENT fact from the model TAG beside
    /// it. Its entire purpose is that a re-quantized local model under a stable tag is a different subject
    /// and must not be pooled with the original as one sample (charter §5 model drift); the tag alone
    /// cannot distinguish them.
    /// <para>
    /// <b>Provider reality, and null does not mean the harness lost it.</b> A Claude row's digest is
    /// PERMANENTLY null: the Claude CLI stream carries a model TAG and no fingerprint at all —
    /// <see cref="Prompts.ClaudeStreamParser"/> extracts <c>num_turns</c>, usage, cost and <c>model</c>,
    /// and nothing else. This is a provider fact, not a gap awaiting a fix. An <c>openai-compat</c> row
    /// carries a digest only where the engine volunteers <c>system_fingerprint</c>; many engines do not.
    /// Therefore null means <i>"the provider exposed none"</i>, never <i>"the harness lost it"</i>.
    /// </para>
    /// <para>
    /// Register copied from <see cref="RequestedModel"/> (#349) — a second field beside <see cref="Model"/>
    /// earning its place by carrying a fact <see cref="Model"/> cannot: the request there, the fingerprint
    /// here.
    /// </para>
    /// <para>
    /// <b>Placement is mechanical, not cosmetic (D32).</b> This rides <see cref="AttemptProvenance"/>
    /// rather than <see cref="AttemptRecord"/> for the same reason <see cref="AttemptJudge"/> does — see
    /// the placement note on <see cref="Judge"/> below. <see cref="AttemptRecord.Provenance"/> is the only
    /// member that already rides <c>Execution.PendingAttempt</c>, and therefore reaches BOTH
    /// record-construction paths — the serial <c>AttemptJournaler</c> AND
    /// <c>Scheduler.RecordSucceededSettle</c>, the DEFAULT worktree mode. A member hung directly off the
    /// attempt record lands in serial mode and silently vanishes in worktree mode.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelDigest { get; init; }

    /// <summary>
    /// Whether the attempt's resolved route was already WARM (plan 30 §3.4) when it launched. <c>bool?</c>
    /// rather than <c>bool</c> for the same class of reason <see cref="TierSource"/> is nullable: "not
    /// known" is not "cold", and a script action resolved no route at all, so there is nothing to report.
    /// Absent — never <c>false</c> by default — on any attempt where warm/cold was not determined.
    /// <para>
    /// Placement is mechanical, on the same D32 terms as <see cref="ModelDigest"/> immediately above: it
    /// rides <see cref="AttemptProvenance"/> because that is the shape that reaches both the serial
    /// journaler and the worktree-mode settle path — see <see cref="ModelDigest"/>'s comment for the full
    /// argument.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RouteWarm { get; init; }

    /// <summary>
    /// The name of the <c>promptRunners</c> block the attempt resolved to (SSOT §7 / DoR §12.4) — the
    /// registry KEY, so a reader can go straight to the block that served this attempt instead of
    /// guessing it back from <see cref="Model"/>. Absent (never <c>null</c> noise) for a script attempt
    /// and in every journal written before model tiering.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runner { get; init; }

    /// <summary>
    /// The resolved block's <c>kind</c> as its WIRE TOKEN (e.g. <c>claude</c>, <c>openai-compat</c>) —
    /// the same spelling <c>PromptRunnerKinds.Token</c> emits, not the C# enum name and not its ordinal.
    /// The journal is a wire format read by humans and by tooling that never links against this assembly,
    /// so the token is the contract. Absent on the same attempts as <see cref="Runner"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>
    /// The rung that actually RESOLVED (<c>easy</c>|<c>medium</c>|<c>hard</c>) — the tier served, which is
    /// the requested one unless §6.2's climb moved it. Absent when no rung resolved: a legacy-fallback
    /// attempt (D30) and a pinned attempt (D31 — the pin bypassed resolution, so
    /// <see cref="TierSource"/> is <see cref="Journal.TierSource.Override"/> while this stays absent).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tier { get; init; }

    /// <summary>
    /// WHICH SITE supplied the rung (DoR §12.4, D31) — see <see cref="Journal.TierSource"/> for the one
    /// producer of each value. Absent for a legacy-fallback attempt, which has no source to name.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TierSource? TierSource { get; init; }

    /// <summary>
    /// The reasoning effort the attempt ran at — the resolved route's own <c>effort</c>, with the task's
    /// <c>action.effort</c> override applied on top. Absent when neither the block nor the action named
    /// one (the runner CLI's own default).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    /// <summary>
    /// The segment worktree's git branch name (e.g. <c>guardrails/&lt;runId&gt;/&lt;task&gt;/attempt-1</c>).
    /// Null in serial mode (no per-task segment).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SegmentBranch { get; init; }

    /// <summary>The absolute segment worktree path this attempt wrote in. Null in serial mode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorktreePath { get; init; }

    /// <summary>The base commit sha the segment forked from (<c>taskBase</c>). Null in serial mode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseCommit { get; init; }

    /// <summary>
    /// The tool grants the PLAN DECLARED for this attempt — the prompt runner's <c>allowedTools</c>
    /// exactly as authored, order preserved. The "before" half of the injection story: it is what a
    /// reader would otherwise have to reconstruct from <c>guardrails.json</c> to know whether the
    /// effective set matched the declaration. Null when the question does not apply (a script attempt,
    /// or a prompt task whose runner cannot be resolved); an EMPTY list is a real answer — the plan
    /// declared no grants at all.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DeclaredToolGrants { get; init; }

    /// <summary>
    /// ONLY what the HARNESS INJECTED on top of <see cref="DeclaredToolGrants"/> (issue #382) — the
    /// read-only grants the harness provisions for its own retry-salvage protocol rather than hoping
    /// the plan author already declared them. Held apart from the declared list, never folded into it:
    /// injection buys determinism at the cost of transparency, and this field is the repayment — the
    /// effective set is <c>declaredToolGrants</c> + <c>injectedToolGrants</c>, attributable to whoever
    /// contributed each entry. Null on the same "does not apply" attempts as
    /// <see cref="DeclaredToolGrants"/>; an EMPTY list is likewise a real answer — the plan already
    /// declared everything the harness needs, so nothing was added.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InjectedToolGrants { get; init; }

    /// <summary>
    /// OPTIONAL verifier route that graded this attempt (SSOT §7 <c>provenance.judge</c> / DoR §12.4 +
    /// §6.5, model tiering #201) — see <see cref="AttemptJudge"/>. Absent entirely when no judge resolved
    /// through routing (§6.5 Invariant 7): a deterministic-only guardrail set runs no model, so there is
    /// no verifier to name.
    ///
    /// <para><b>Placement is D32, and it is mechanical rather than cosmetic.</b> The judge hangs HERE
    /// rather than off <see cref="AttemptRecord"/> because <see cref="AttemptRecord.Provenance"/> is the
    /// only member that already rides <c>PendingAttempt</c>, and therefore reaches BOTH
    /// record-construction paths — the serial <c>AttemptJournaler</c> AND
    /// <c>Scheduler.RecordSucceededSettle</c>, which is the DEFAULT worktree mode. A member hung directly
    /// off the attempt record lands in serial mode and silently vanishes in worktree mode. "The facts the
    /// harness knows at attempt launch" describes when this provenance is CONSTRUCTED, not what may be
    /// recorded on it before the record is written: the judge is folded in with a <c>with</c> expression
    /// at settle time, and reaches both paths for free.</para>
    ///
    /// <para><b>Absent, never <c>null</c></b> — the ignore condition below is load-bearing, not tidiness.
    /// <see cref="JournalJson"/> sets <c>DefaultIgnoreCondition = Never</c>, so without it EVERY attempt
    /// carrying a provenance would grow a <c>"judge": null</c> key, including the script attempts of runs
    /// whose author opted into none of this. The two shapes deserialize identically, so the cost is paid
    /// entirely by the humans and the tooling reading <c>run.json</c>. Same discipline as every optional
    /// member beside it.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptJudge? Judge { get; init; }
}

/// <summary>
/// The VERIFIER route that graded one attempt (SSOT §7 <c>provenance.judge</c> / DoR §12.4, resolved per
/// §6.5): the block, model and effort a judge guardrail resolved to at attempt launch, recorded beside
/// the ACTOR's own route on the enclosing <see cref="AttemptProvenance"/>. It answers "who vouched for
/// this work, and were they strong enough to" — §6.5 exists because a weak actor graded by an equally
/// weak judge is two blind spots agreeing, and the run goes green over broken work.
///
/// <para>Every member is optional EXCEPT <see cref="Bumped"/>, which records a real <c>false</c> rather
/// than an absence: "the bump did not fire" is a measurement, not a gap, and it is the datum #230-lite
/// reads to answer whether a bumped judge is worth what it costs.</para>
/// </summary>
public sealed record AttemptJudge
{
    /// <summary>
    /// The name of the <c>promptRunners</c> block the JUDGE resolved to — the registry KEY, exactly as
    /// <see cref="AttemptProvenance.Runner"/> records it for the actor. Read separately from the actor's
    /// on purpose: when §6.5 rule 3's bump fires the two names differ, and that difference is the record.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runner { get; init; }

    /// <summary>
    /// The judge block's <c>kind</c> as its WIRE TOKEN (e.g. <c>claude</c>, <c>openai-compat</c>) — the
    /// spelling <c>PromptRunnerKinds.Token</c> emits, never the C# enum name or its ordinal. §6.5 rule 4's
    /// verifier-only fallback reads <c>kind != "claude"</c> ⇒ weak-unless-declared, so the kind is part of
    /// the evidence behind <see cref="Advisory"/> rather than incidental colour.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>
    /// The fully resolved model the judge ran on — the verifier-side counterpart of
    /// <see cref="AttemptProvenance.Model"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>
    /// The reasoning effort the judge ran at — the resolved judge block's own <c>effort</c>, with a judge
    /// frontmatter override applied on top. Absent when neither named one (the runner CLI's own default).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    /// <summary>
    /// The rung the judge resolved on (<c>easy</c>|<c>medium</c>|<c>hard</c>). By §6.5 rule 2 this is the
    /// ACTOR's effective rung unless the judge's own frontmatter <c>tier</c> pinned one (rule 1) or
    /// §6.5.1's <c>verifier.minTier</c> floor lifted it — rule 3's bump moves STRENGTH, never the tier.
    /// Absent when no rung resolved (a pinned judge, or the legacy-fallback route).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tier { get; init; }

    /// <summary>
    /// The resolved judge block's declared <c>strength</c> — the axis §6.5 rule 3 actually bumps along,
    /// and the number a reader compares against the actor's block to check "equal-or-stronger" without
    /// re-resolving anything. Absent when the block declares no strength (rule 4 then decides weakness by
    /// the provider-kind fallback instead).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Strength { get; init; }

    /// <summary>
    /// Whether the §6.5 rule 3 weak-actor STRENGTH bump fired for this attempt — <c>true</c> when the
    /// judge was lifted to the weakest candidate at the actor's rung whose strength strictly exceeds the
    /// actor's.
    ///
    /// <para>NOT optional, on purpose. Recording <c>false</c> is a measurement — "a judge resolved and no
    /// bump was needed" — where an absent key would be indistinguishable from "no judge resolved at all".
    /// #230-lite aggregates exactly this datum to answer whether a bumped judge earns what it costs, and a
    /// denominator that silently drops its zeroes is not an answer.</para>
    /// </summary>
    public bool Bumped { get; init; }

    /// <summary>
    /// The §6.5 weak / equal-and-weak ADVISORY finding this attempt's JIT re-check produced, in the text a
    /// human reads. Recorded on EVERY attempt that resolved a weak judge (the de-duplication ruling's
    /// "provenance always" half — the quieter log line is a separate surface, and the run summary
    /// aggregates from here), and absent when the judge is not weak.
    ///
    /// <para>Independent of <see cref="Bumped"/>, and in both directions. A judge can be advisory-flagged
    /// with no bump having fired: §6.5 rule 5 degrades rather than overspends, so when the only stronger
    /// block is <c>costly: true</c> the judge STAYS on the actor's route and this is the field that says
    /// so. It is an advisory, never a halt and never a GR code (charter Decision 1) — a GR code is a thing
    /// that can fail a build, and the harness does not block on a model-quality opinion.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Advisory { get; init; }

    /// <summary>
    /// The judge invocation's own cost in USD (plan 28 §11 finding 3) — the VERIFIER's spend, recorded
    /// beside <see cref="AttemptRecord.CostUsd"/> and never folded into it. A verifier is overhead
    /// against the run, not part of the task's own cost, and quietly adding it to the actor's total
    /// would inflate every per-tier and per-model figure the #533 evidence arc depends on and move
    /// <c>maxCostUsd</c>/<c>--autonomous</c>'s liveness floor. Null when the judge runner reports no
    /// cost (a costless local provider) — never <c>0</c>, which would claim a measurement never taken.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// The judge invocation's own token volume — the verifier-side counterpart of
    /// <see cref="AttemptRecord.Usage"/>, so a costless judge provider still shows how much work it did.
    /// Absent (never a zeroed record) when the judge runner reports no usage.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttemptUsage? Usage { get; init; }
}

/// <summary>A guardrail that failed, with its one-line reason (SSOT §7 <c>failedGuardrails</c>).</summary>
public sealed record FailedGuardrail
{
    /// <summary>The guardrail's name (filename minus extension).</summary>
    public required string Name { get; init; }

    /// <summary>The actionable failure reason (guardrail stdout, or a timeout/crash note).</summary>
    public required string Reason { get; init; }
}
