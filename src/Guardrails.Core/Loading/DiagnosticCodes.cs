namespace Guardrails.Core.Loading;

/// <summary>
/// Stable diagnostic codes emitted by <see cref="PlanLoader"/> and
/// <see cref="PlanValidator"/>. Codes are part of the tool's contract — tests assert
/// on them, so do not renumber. Loading errors are GR10xx; validation errors GR20xx.
/// </summary>
public static class DiagnosticCodes
{
    // --- Loading (structural / parse) -------------------------------------------------
    /// <summary>The plan folder or a required file does not exist.</summary>
    public const string MissingFile = "GR1001";

    /// <summary>A JSON manifest failed to parse.</summary>
    public const string InvalidJson = "GR1002";

    /// <summary>A required manifest field is missing or empty.</summary>
    public const string MissingRequiredField = "GR1003";

    /// <summary>A task folder has no <c>action.*</c> file (and no explicit path).</summary>
    public const string NoActionFile = "GR1004";

    /// <summary>A task folder has more than one <c>action.*</c> file and no explicit path.</summary>
    public const string AmbiguousActionFile = "GR1005";

    /// <summary>An explicit <c>action.path</c> points at a file that does not exist.</summary>
    public const string ActionPathNotFound = "GR1006";

    /// <summary>A guardrail directory contains a bare <c>.json</c> with no sibling script (orphan sidecar).</summary>
    public const string OrphanGuardrailMetadata = "GR1007";

    /// <summary>An unknown value was supplied for an enum-valued field (e.g. guardrailMode).</summary>
    public const string InvalidFieldValue = "GR1008";

    /// <summary>The <c>tasks</c> directory exists but contains no task folders (an empty plan).</summary>
    public const string NoTasks = "GR1009";

    // --- Validation (semantic) --------------------------------------------------------
    /// <summary>A <c>dependsOn</c> entry references a task id that does not exist.</summary>
    public const string UnknownDependency = "GR2001";

    /// <summary>Two tasks share the same id (should be impossible by folder, guarded anyway).</summary>
    public const string DuplicateTaskId = "GR2002";

    /// <summary>A task has zero guardrails.</summary>
    public const string NoGuardrails = "GR2003";

    /// <summary>A task references a prompt runner name not declared in <c>promptRunners</c>.</summary>
    public const string UnknownPromptRunner = "GR2004";

    /// <summary>An extension used by the plan has no resolvable interpreter on PATH.</summary>
    public const string UnresolvableInterpreter = "GR2005";

    /// <summary>An extension is only valid on a different operating system (e.g. .cmd off Windows).</summary>
    public const string InterpreterWrongPlatform = "GR2006";

    /// <summary>The <c>dependsOn</c> graph contains a cycle.</summary>
    public const string DependencyCycle = "GR2007";

    /// <summary>The plan has prompt actions or prompt guardrails but no <c>promptRunners</c> config to run them.</summary>
    public const string NoPromptRunners = "GR2008";

    /// <summary>
    /// A declared prompt runner's <c>command</c> is not resolvable on PATH. WARNING, not
    /// error — the plan may run on another machine where the runner is installed.
    /// </summary>
    public const string PromptRunnerNotOnPath = "GR2009";

    /// <summary>
    /// Two tasks declare the same <c>stableId</c> (SSOT §3/§11). The regeneration merge keys
    /// task identity on <c>stableId</c>, so a duplicate would make two tasks indistinguishable —
    /// almost always a copy-paste slip. Only declared (non-null) ids are checked.
    /// </summary>
    public const string DuplicateStableId = "GR2010";

    /// <summary>
    /// A declared <c>stableId</c> is not in the allowed format <c>^[a-z0-9][a-z0-9._-]*$</c>
    /// (SSOT §3/§11). The regeneration merge derives a synthetic identity (<c>folder:&lt;name&gt;</c>)
    /// for tasks without a stableId; reserving the format keeps a real stableId from ever colliding
    /// with that synthetic key, and keeps ids stable across path/JSON handling.
    /// </summary>
    public const string InvalidStableId = "GR2011";

    /// <summary>
    /// A present <c>maxCostUsd</c> (SSOT §2) is zero or negative. A non-positive cap would halt the
    /// run before any work runs — a configuration mistake — so it is an ERROR. (Plan 04 reserved
    /// "GR2010", but GR2010/GR2011 were taken by the stableId checks, which landed after that slice
    /// was planned; this uses the next free validation code.)
    /// </summary>
    public const string CostCapNonPositive = "GR2012";

    /// <summary>
    /// The plan workspace is not inside a git repository (plan 08 M2, SSOT §1). Emitted ONLY in
    /// worktree mode (<c>maxParallelism &gt; 1</c>, the PO decision): parallel tasks need per-segment
    /// worktree isolation (plan branch, segment worktrees), which requires the workspace to reside
    /// within a git repository. An ERROR — the harness cannot create worktrees without git. A SERIAL
    /// run (<c>maxParallelism == 1</c>) uses the shared workspace and does NOT require git, so this is
    /// not emitted there.
    /// </summary>
    public const string WorkspaceNotGitRoot = "GR2015";

    /// <summary>
    /// The configured <c>worktreeRoot</c> path is long enough that harness-managed paths may
    /// exceed the Windows MAX_PATH limit of 260 characters (plan 08 M2, SSOT §2). A WARNING —
    /// the plan may work but is at risk; enable long-path support with
    /// <c>git config --system core.longpaths true</c>.
    /// </summary>
    public const string MaxPathRisk = "GR2016";

    // ─── RETIRED CODES — GR2017 / GR2018 (do NOT re-wire) ────────────────────────────────────────
    // These two constants are EMITTED NOWHERE. Their rules were retired/re-homed by the four-folder
    // preflights/guardrails model (design-of-record 09-preflight-first-class, SSOT §3.3):
    //   • GR2017 (MissingIntegrationGate)   — the "a multi-leaf/fan-in plan must declare exactly one
    //       integrationGate:true sink" rule is RETIRED. The terminal gate is now the <plan>/guardrails/
    //       FOLDER; a plan that still declares the legacy key is rejected by GR2029 instead.
    //   • GR2018 (IntegrationGateEmpty)     — the "that sink must carry ≥1 scope:'integration'
    //       guardrail" content teeth were RE-HOMED onto the folder as GR2028 (see below), not deleted.
    // They are kept here (not deleted) ONLY so their code numbers stay reserved and are never
    // re-allocated to a new rule. Do NOT wire either back up — the migration that retired them is
    // complete and every consumer moved to GR2028/GR2029.

    /// <summary>RETIRED (see the block above): the old GR2017 "missing integrationGate sink" rule — superseded by GR2029.</summary>
    public const string MissingIntegrationGate = "GR2017";

    /// <summary>RETIRED (see the block above): the old GR2018 "empty integration gate" content rule — re-homed onto the folder as GR2028.</summary>
    public const string IntegrationGateEmpty = "GR2018";

    /// <summary>
    /// A <c>writeScope</c> entry is an absolute path or contains <c>..</c> segments that could
    /// reference files outside the workspace root (plan 08 §2/§3.4, SSOT §3.4). Such an entry can
    /// never match a git-diff path (which is always relative to the repo root) and is almost always
    /// a configuration mistake — an ERROR.
    /// </summary>
    public const string WriteScopeEscapesWorkspace = "GR2019";

    /// <summary>
    /// A <c>writeScope</c> entry is vacuous or over-broad (e.g. <c>**</c> or <c>*</c>) and provides
    /// no meaningful constraint (plan 08 §2/§3.4, SSOT §3.4). A scope that matches everything defeats
    /// the purpose of write-scope isolation — a WARNING (may still be intentional during migration).
    /// </summary>
    public const string WriteScopeVacuous = "GR2020";

    /// <summary>
    /// A guardrail <c>scope</c> value is not one of the recognised values <c>integration</c> or
    /// <c>local</c> (plan 08 M2, SSOT §4.3). An unrecognised scope silently degrades to <c>local</c>
    /// at runtime, dropping the guardrail from the integration union re-verify set — a deterministic
    /// gate quietly stops re-running without any warning. Validation must FAIL so the typo is caught
    /// at validate time, never at silent runtime — an ERROR.
    /// </summary>
    public const string InvalidGuardrailScopeValue = "GR2021";

    /// <summary>
    /// A guardrail or script-action body reads another task's state namespace in the canonical
    /// state-access form (<c>$state.'&lt;task-id&gt;'</c> / <c>state["&lt;task-id&gt;"]</c>) but the
    /// referenced producer is not a transitive <c>dependsOn</c> ancestor of the referencing task and
    /// is not satisfied by a <c>seed.json</c> top-level key (SSOT §6.2, issue #121). The scheduler
    /// orders only on <c>dependsOn</c>, so the consumer can run before the producer and the read
    /// returns null — failing at runtime as <c>needs-human</c> for a reason knowable at load time.
    /// An ERROR: turns the runtime cascade into a deterministic load-time catch.
    /// </summary>
    public const string CrossTaskStateReferenceWithoutDependency = "GR2022";

    /// <summary>
    /// A prompt runner's <c>maxOutputTokens</c> (SSOT §2/§9, issue #114) — or its
    /// <c>guardrailOverrides.maxOutputTokens</c> — is zero or negative. The value caps the runner's
    /// per-response output budget and is translated into the CLI's output-token env var; a
    /// non-positive cap would make every prompt response fail, so it is an ERROR.
    /// </summary>
    public const string MaxOutputTokensNonPositive = "GR2023";

    /// <summary>
    /// A <c>stagingOutputs</c> entry is malformed (SSOT §3.5, issue #130): the array is present but
    /// empty; an entry has a missing/empty <c>from</c> or <c>to</c>; a <c>to</c> does not normalize to
    /// a path under <c>.claude/</c>; a <c>to</c> escapes the workspace (absolute or <c>..</c> climbing
    /// out, the same family as <see cref="WriteScopeEscapesWorkspace"/>); or a <c>from</c> escapes the
    /// staging root. <c>stagingOutputs</c> exists only to land <c>.claude/</c> deliverables; a
    /// malformed contract would produce a task that runs, moves nothing (or the wrong thing), and then
    /// fails its <c>.claude/</c> guardrail for a reason that was knowable at validate time — so it is an
    /// ERROR, turning a knowable runtime cascade into a load-time catch.
    /// </summary>
    public const string StagingOutputsInvalid = "GR2024";

    /// <summary>
    /// The plan folder has not been through <c>/guardrails-review</c> (no <c>state/guardrails-review.json</c>
    /// marker), or it has changed since the last review (the marker's <c>planHash</c> no longer matches the
    /// plan, SSOT §13, issue #79). A WARNING, never an error: the review is a token-costing Claude skill and
    /// a quick/intentional run is legitimate, so this is an honest nudge — run <c>/guardrails-review</c>, or
    /// pass <c>--skip-review-check</c> on <c>run</c> to proceed. (GR2024 is reserved elsewhere.)
    /// </summary>
    public const string ReviewMarkerMissingOrStale = "GR2025";

    /// <summary>
    /// A task's <c>covers-key-behaviors</c>-style guardrail requires a coverage token that the SAME
    /// task's action prompt never mentions (SSOT §3/§4, issue #157 §1). When an action prompt is
    /// edited (a scenario removed, scope narrowed) but its coverage guardrail is not updated to match,
    /// the guardrail keeps requiring the now-removed token — a correct implementation following the
    /// prompt can never satisfy it, so the task dead-ends at <c>needs-human</c> on every attempt. A
    /// WARNING, never an error: this is a HEURISTIC (case-insensitive keyword-presence cross-reference),
    /// only emitted when the covers-key-behaviors archetype and a clear literal token are both
    /// confidently identified — when in doubt it stays silent (zero-false-positive spirit, even for a
    /// warning).
    /// </summary>
    public const string StaleCoverageToken = "GR2026";

    // --- Four-folder preflights/guardrails model (preflights-impl deliverable 2) -------
    // Next-free allocation confirmed at authoring time: GR2026 (StaleCoverageToken) is the last
    // taken; GR2013/GR2014 are historical gaps but GR2015–GR2026 are contiguous above them, so
    // GR2027 is the next free code. The three codes below are a CONTIGUOUS block (GR2027–GR2029)
    // for the two-scope preflights/guardrails feature (design-of-record 09-preflight-first-class,
    // SSOT §1/§3.3/§4). Deliverable 2 (the loader/validator) READS these constants from this file —
    // it is the source-of-truth allocation, so do not renumber.

    /// <summary>
    /// A guardrail file in one of the four folders (<c>&lt;plan&gt;/preflights/</c>,
    /// <c>&lt;plan&gt;/guardrails/</c>, <c>tasks/&lt;id&gt;/preflights/</c>, or
    /// <c>tasks/&lt;id&gt;/guardrails/</c>) does not open with the required <c>catches:</c> comment
    /// (script) or frontmatter field (prompt) — SSOT §4. A guardrail whose author cannot state what
    /// wrong implementation it catches is decorative; the loader rejects the malformed declaration
    /// rather than run a check nobody can justify. The canonical per-folder "malformed declaration"
    /// diagnostic for the four-folder model.
    /// </summary>
    public const string GuardrailMissingCatches = "GR2027";

    /// <summary>
    /// A multi-leaf or fan-in plan's terminal <c>&lt;plan&gt;/guardrails/</c> folder does not carry
    /// at least one deterministic check that actually RE-RUNS the integration set (the whole-repo
    /// build / full suite / a union invariant) — SSOT §3.3. This is the RE-HOMED GR2018 rule: the
    /// terminal-sink obligation moved off the retired <c>integrationGate</c> task and onto the folder,
    /// with its CONTENT teeth preserved. An empty terminal folder fails; so does a folder carrying only
    /// a tautological <c>exit 0</c> file (a present-but-verifies-nothing gate) — the check is content,
    /// not mere non-emptiness. An ERROR: a parallel plan whose terminal gate verifies nothing is not a
    /// sound whole-repo soundness boundary. (The §4.3 <c>scope:"integration"</c> per-union tag is
    /// unchanged and independent — only the terminal-sink obligation re-homed here.)
    /// </summary>
    public const string PlanGuardrailsMissingIntegrationReRun = "GR2028";

    /// <summary>
    /// A task still declares the retired <c>integrationGate: true</c> task kind (SSOT §3.3). Under the
    /// four-folder model the terminal checks live in <c>&lt;plan&gt;/guardrails/</c>; the
    /// <c>integrationGate</c> task kind and its GR2017 presence rule are RETIRED with no coexistence
    /// window. A plan that still carries the key gets a HARD validation ERROR (honest-over-silent,
    /// lead decision) so the stale declaration is caught at validate time instead of silently
    /// ignored — every committed consumer of the old behavior is migrated in the same feature.
    /// </summary>
    public const string RetiredIntegrationGateKey = "GR2029";

    // Historical: as of issue #200, GR2029 (RetiredIntegrationGateKey) was the last taken code, so GR2030
    // was next-free at that point. (Current next-free is at the END of this file — issue #320.)

    /// <summary>
    /// A <c>model</c> value (SSOT §2/§3, issue #200) is present but empty, whitespace-only, or contains
    /// leading/trailing whitespace or a control character — at any of the three sites it can be
    /// declared: <c>promptRunners.&lt;name&gt;.model</c>, <c>promptRunners.&lt;name&gt;.guardrailOverrides.model</c>,
    /// or a task's <c>task.json action.model</c>. There is no enumerable list of valid Claude model
    /// names to check against, but a value that is empty/blank/malformed like this can never be a real
    /// model identifier — it is always a configuration mistake (an empty string left by templating, a
    /// stray quoted space) that would otherwise reach the runner's <c>--model</c> flag verbatim and fail
    /// every attempt. A <c>null</c>/absent <c>model</c> is fine (means "no override") and is not flagged.
    /// An ERROR: turns a runtime prompt-invocation failure into a load-time catch.
    /// </summary>
    public const string ModelInvalid = "GR2030";

    // Historical: as of issue #274 Part C, GR2030 (ModelInvalid) was the last taken code, so GR2031 was
    // next-free at that point.

    /// <summary>
    /// An <c>autonomyPolicy</c> value (SSOT §2.1/§7.2, #254/#269/#274) is present but not one of the three
    /// recognised values <c>prompt</c> (default), <c>halt</c>, or <c>auto</c>. The field is the unified
    /// autonomy knob governing every prompt/halt/auto decision boundary; an unrecognised value can never map
    /// to a policy and is always a configuration mistake (a typo, a stale value — including the pre-fold
    /// <c>reprocess</c>) that would otherwise silently degrade to a default a CI-strict user did not intend.
    /// An ERROR: turns a silent-wrong-policy footgun into a load-time catch. A <c>null</c>/absent value is
    /// fine (means the default, <c>prompt</c>). (Generalised from the #274 Part C <c>driftPolicy</c> check —
    /// same code, one check.)
    /// </summary>
    public const string InvalidAutonomyPolicy = "GR2031";

    // --- Multi-wave plans (nested layout, #254 / SSOT §14) ----------------------------
    // Next-free allocation confirmed at authoring time: GR2031 (InvalidAutonomyPolicy) is the last taken
    // code above, so GR2032–GR2034 are the next free CONTIGUOUS block for the multi-wave feature
    // (design-of-record 10-multi-wave-plans, SSOT §14.1). Do not renumber.

    /// <summary>
    /// A plan folder has a MIXED layout: both a root <c>tasks/</c> directory AND one or more
    /// <c>wave-*/</c> wave subdirectories (SSOT §14.1). A plan is either FLAT (a root <c>tasks/</c>) or
    /// WAVED (no root <c>tasks/</c>, ≥1 <c>wave-NN-slug/</c> subdir) — never both. A mixed layout is
    /// ambiguous (would the root tasks run before, after, or interleaved with the waves?) and is always an
    /// authoring mistake. An ERROR.
    /// </summary>
    public const string MixedWaveLayout = "GR2032";

    /// <summary>
    /// A waved plan's wave numbering is malformed (SSOT §14.1, Open Decision F): two wave dirs share the
    /// same numeric prefix <c>NN</c> (a duplicate — the strict total order is then ambiguous), OR a
    /// subdirectory sitting alongside the wave dirs does not conform to the wave-dir pattern
    /// <c>^wave-([0-9]+)-[a-z0-9-]+$</c> and is not a recognised plan-root folder (a typo'd wave dir, e.g.
    /// <c>wave-scaffold</c> with no number). Both are ERRORS — the numeric prefix is load-bearing (it drives
    /// the wave order, there is no <c>dependsOnWave</c> edge). A numbering GAP (e.g. wave-01 then wave-03,
    /// no wave-02) is a WARNING, not an error — the order is still unambiguous.
    /// </summary>
    public const string WaveNumbering = "GR2033";

    /// <summary>
    /// A task in a waved plan declares a <c>dependsOn</c> edge that names a task in ANOTHER wave (SSOT
    /// §14.1/§14.2). Cross-wave ordering is the job of the wave barrier (a wave never starts until the
    /// prior wave fully drained), NOT a task edge, so each wave's DAG must be self-contained ("no DAG of
    /// waves"). A <c>dependsOn</c> references siblings within the SAME wave by plain folder name; a
    /// wave-qualified reference (<c>&lt;otherWave&gt;/&lt;task&gt;</c>) or a plain name that resolves to a
    /// task in a different wave is an ERROR.
    /// </summary>
    public const string CrossWaveDependency = "GR2034";

    // --- Per-folder check-name uniqueness (#332) --------------------------------------
    // Historical: as of issue #332, GR2034 (CrossWaveDependency) was the last taken code, so GR2035 was
    // next-free at that point.

    /// <summary>
    /// Two checks in the SAME folder share a <c>Name</c> (SSOT §4.5, issue #332). A guardrail's
    /// <see cref="Model.GuardrailDefinition.Name"/> is its filename with the final extension dropped
    /// (<c>PlanLoader.GuardrailName</c>), so a portable pair like <c>01-build.ps1</c> + <c>01-build.sh</c>
    /// in ONE folder both collapse to Name <c>"01-build"</c>. Every surface that keys a check by
    /// <c>(taskId, Name)</c> or bare <c>Name</c> — the #219 live-status badges, the journal's
    /// <c>FailedGuardrail.Name</c>, the resume seed — then silently collapses the two distinct checks into
    /// one entry: the second overwrites the first, one node is unbadgeable, and a result is misattributed
    /// to the wrong box. An ERROR: the ambiguity is knowable at load time, and rejecting it makes the
    /// <c>(taskId, Name)</c> key provably unique. Applied per folder to every folder in the four-folder
    /// model — each task's <c>guardrails/</c> and <c>preflights/</c>, each wave's <c>guardrails/</c> and
    /// <c>preflights/</c> (SSOT §14.3), and the plan-level <c>preflights/</c> and <c>guardrails/</c>.
    /// Remedy: rename one of the colliding files so the two Names differ.
    /// </summary>
    public const string DuplicateCheckName = "GR2035";

    // CURRENT next-free code: GR2036. GR2035 (DuplicateCheckName) is the last taken code above.
    // When allocating a new code, take GR2036 and update this line (issue #320).
}
