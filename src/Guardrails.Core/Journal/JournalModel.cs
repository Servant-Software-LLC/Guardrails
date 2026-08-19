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

    // NOTE (issue #419): the Windows short-junction root is NO LONGER journaled. It was the field that made
    // the junction durable RUN STATE (forcing a resume onto the same .a..z letter and a sweep as the only
    // reclaim), which is the leak #407/#419 chased. The junction is now a process-scoped cwd alias
    // (WorktreeJunction/WorktreeJunctionLifetime): fresh per run, released on every recoverable exit, and
    // re-derived by the deterministic segment subpath on resume. An OLD run.json that still carries a
    // "worktreeJunctionRoot" key deserializes clean under this model (JournalJson has no
    // JsonUnmappedMemberHandling.Disallow → the unknown member is skipped) and is simply ignored.
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

    /// <summary>Attempt records in attempt order (1-based).</summary>
    public IReadOnlyList<AttemptRecord> Attempts { get; init; } = [];
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
    /// OPTIONAL agent-asserted classification of a <c>needs-human</c> attempt (SSOT §7/§9, issue #485):
    /// <c>blocked-work</c> ("I cannot complete this work") or <c>defective-guardrail</c> ("this check is
    /// itself wrong"). Absent means UNCLASSIFIED — the harness invents no default. Journaled because
    /// <c>guardrails status</c> and the static log-site export read ONLY the journal; without it, a halt's
    /// claim would survive the run in <c>action-out-fragment.json</c> alone. Additive and backward-compatible:
    /// omitted (never <c>null</c> noise) for every non-needs-human attempt and in every pre-#485 journal.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NeedsHumanKind { get; init; }
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
}

/// <summary>A guardrail that failed, with its one-line reason (SSOT §7 <c>failedGuardrails</c>).</summary>
public sealed record FailedGuardrail
{
    /// <summary>The guardrail's name (filename minus extension).</summary>
    public required string Name { get; init; }

    /// <summary>The actionable failure reason (guardrail stdout, or a timeout/crash note).</summary>
    public required string Reason { get; init; }
}
