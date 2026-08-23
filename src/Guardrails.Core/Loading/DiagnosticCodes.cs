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

    /// <summary>
    /// The target is a WAVE folder of a nested (waved) plan, not a loadable plan (issue #472). A wave holds
    /// <c>preflights/</c> + <c>guardrails/</c> + <c>tasks/</c> but no <c>guardrails.json</c> BY DESIGN
    /// (SSOT §14.1 — ONE shared run config), so loading it as a plan can only ever produce
    /// <see cref="MissingFile"/>. Still an ERROR — a wave is not independently loadable, and silently
    /// validating the parent plan instead of what was asked would be worse — but the message names the
    /// parent plan root, the wave-aware `validate <plan>` invocation, and the two verbs
    /// (<c>plan-hash</c> / <c>mark-reviewed</c>) that DO accept a wave folder and resolve it through its
    /// parent plan (SSOT §13).
    /// </summary>
    public const string WaveFolderIsNotALoadablePlan = "GR1010";

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
    ///
    /// <para>The remediation is SURFACE-SPECIFIC (<see cref="Review.ReviewNudgeSurface"/>, issue #410):
    /// <c>--skip-review-check</c> exists only on <c>run</c>, so the <c>validate</c> wording points at
    /// <c>guardrails mark-reviewed</c> instead of a flag that command would reject.</para>
    ///
    /// <para>On a WAVED plan (§14) this is emitted <b>per wave</b> and NOT at plan level (issues
    /// #471/#472/#488): each wave carries its own marker keyed on its <c>WaveDefinitionHash</c>, so
    /// authoring a downstream wave — which every JIT breakdown does — no longer de-attests an already
    /// reviewed, stamped, run, green upstream wave. Un-authored JIT stubs are silent (nothing to attest).
    /// A warning that fires on every healthy run is noise, and noise is how a real post-review guardrail
    /// weakening gets waved through later.</para>
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

    // Historical: as of issue #331, GR2035 (DuplicateCheckName) was the last taken code, so GR2036 was
    // next-free at that point.

    /// <summary>
    /// A guardrail's optional <c>expectedDurationSeconds</c> metadata (SSOT §4.1.1, issue #331) is present
    /// but not a positive integer (zero or negative). The field is a read-only progress hint — the
    /// running-guardrail heartbeat surfaces it as "expected ~Xm" — so a non-positive value can never be a
    /// real duration and would render nonsensically ("expected ~0m"); it is always an authoring mistake. An
    /// ERROR, mirroring the other optional-positive checks (cf. GR2012 <c>maxCostUsd</c>, GR2023
    /// <c>maxOutputTokens</c>). A <c>null</c>/absent value is fine (no hint) and is not flagged. Validated
    /// across all four guardrail-shaped folders (task guardrails/preflights, <c>&lt;plan&gt;/preflights/</c>,
    /// <c>&lt;plan&gt;/guardrails/</c>), like the guardrail <c>scope</c> check (GR2021).
    /// </summary>
    public const string ExpectedDurationNonPositive = "GR2036";

    // Historical: as of issue #346, GR2036 (ExpectedDurationNonPositive) was the last taken code, so
    // GR2037 was next-free at that point.

    /// <summary>
    /// A generated guardrail SCRIPT contains a KNOWN-BAD regex construction listed in the data-driven
    /// banned-pattern registry (SSOT §4.6, issue #346). <c>guardrails validate</c> scans every
    /// four-folder script guardrail's comment-stripped body (task <c>guardrails/</c>+<c>preflights/</c>,
    /// wave <c>guardrails/</c>+<c>preflights/</c>, plan <c>guardrails/</c>+<c>preflights/</c>) against
    /// each registry entry's <c>badPattern</c> and emits ONE GR2037 per match, citing the entry
    /// <c>id</c> + <c>reason</c> + <c>goodPatternHint</c>. A CURATED set of three, grown only by reviewed
    /// addition: <c>#73</c> (the hollow-assertion AVOID construction), <c>#187a</c> (the unanchored
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>/<c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> conflict-marker
    /// construction — the exact #346 regression; the bare <c>=======</c> was the design's deferred #187b
    /// and is NOT banned, to avoid a setext-underline / banner false-positive), and <c>#462</c> (a
    /// <c>dotnet test</c> carrying <c>-v q</c> in the same script as a grep for the failure-detail block,
    /// which the flag suppresses — a dead #179 re-emit, so the retry sees WHAT failed and never WHY). An ERROR: correct SKILL.md text does not guarantee an
    /// LLM applies it every generation, so a fixed-spelling catalogue lesson is enforced
    /// deterministically here — complementing, not replacing, the #302 smoke-test and
    /// <c>/guardrails-review</c>. The comment-strip-before-scan is itself the #97 lesson: a
    /// <c>catches:</c>/header comment that DESCRIBES the banned construction must not false-fire.
    /// </summary>
    public const string BannedGuardrailPattern = "GR2037";

    // Historical: as of issue #383, GR2037 (BannedGuardrailPattern) was the last taken code, so GR2038
    // was next-free at that point.

    /// <summary>
    /// A WORKTREE-mode run's segment path would exceed the Windows MAX_PATH limit of 260 characters
    /// (issue #383, SSOT §2). Unlike <see cref="MaxPathRisk"/> (GR2016 — a validate-time WARNING against a
    /// deep <em>configured</em> <c>worktreeRoot</c>), this is the RUN-START authoritative check: it is
    /// computed against the machine's ACTUAL worktree root (the <c>GUARDRAILS_WORKTREE_ROOT</c>-aware
    /// <see cref="SchedulerFactory.WorktreeRootFor"/>), so it cannot live in <c>guardrails validate</c>
    /// alone. For each task the harness measures the segment base
    /// <c>&lt;root&gt;/&lt;runId&gt;/&lt;taskId&gt;/attempt-1</c> and adds a reserved build-output budget
    /// (<see cref="Execution.WorktreePathPreflight.BuildOutputReserve"/>, ~90 chars, sized for
    /// <c>\bin\Debug\net8.0\&lt;assembly&gt;.exe</c>); if the total exceeds 260 the run FAILS FAST before
    /// any task executes. The real #383 case: a built test-exe hit 264 chars and CreateProcessW failed with
    /// Win32 206 (ERROR_FILENAME_EXCED_RANGE) — which Windows LongPathsEnabled does NOT prevent (it does not
    /// lift CreateProcess's application-name ceiling). An ERROR, Windows-only + worktree-only; the remedy is
    /// to point <c>GUARDRAILS_WORKTREE_ROOT</c> at a short path (e.g. <c>C:\gw</c>).
    /// </summary>
    public const string WorktreePathTooLong = "GR2038";

    // --- Autonomy criticality dial (the OPTIONAL `autonomy` block, issue #361 / doc 12 §3.4/§3.5/§5.2,
    //     decided §10 M) ---------------------------------------------------------------------------------
    // Historical: GR2038 (WorktreePathTooLong, #383/#384) is the last taken code above; the two
    // autonomy-block checks below take the next codes — the value check (GR2039) and the compound-config
    // gate (GR2040). (The #361 branch had provisionally reserved GR2038 for design-360 and placed these at
    // GR2039/GR2040; #384 landed WorktreePathTooLong at GR2038 first, so that reservation is void and the
    // GR2039/GR2040 numbering stands unchanged.)

    /// <summary>
    /// A value in the OPTIONAL <c>autonomy</c> criticality-dial block (issue #361, doc 12 §3.4/§3.5;
    /// decided §10 M) is not one of its recognised tokens: an <c>escalationThreshold</c> that is not
    /// <c>low</c>/<c>moderate</c>/<c>high</c>/<c>critical</c>, a <c>gateThresholds.needs-human</c> or
    /// <c>gateThresholds.wave-checkpoint</c> that is not a criticality level, or a
    /// <c>gateThresholds.review-gate</c> that is neither <c>escalate</c> nor <c>proceed-unreviewed</c>. The
    /// parse falls an unrecognised value back to the dial/default rather than failing (so the block still
    /// loads), which means the typo would otherwise silently degrade to a policy the operator never
    /// intended — GR2039 catches it at validate time. An ERROR, mirroring GR2031
    /// (<see cref="InvalidAutonomyPolicy"/>) for the orthogonal <c>autonomyPolicy</c> field. A
    /// <c>null</c>/absent value is the default and is not flagged.
    /// </summary>
    public const string InvalidAutonomyDialValue = "GR2039";

    /// <summary>
    /// The <c>autonomy</c> block declares the FORBIDDEN compound configuration (issue #361, doc 12
    /// §5.2/§3.4; decided §10 M/A): <c>gateThresholds.review-gate == "proceed-unreviewed"</c> AND the
    /// reachable end-state best-guesses a hard call — <c>escalationThreshold == "critical"</c> OR any
    /// in-wave <c>gateThresholds</c> criticality value (<c>needs-human</c> / <c>wave-checkpoint</c>)
    /// <c>== "critical"</c>. Keyed on the REACHABLE END-STATE (Finding 3), so a per-gate override like
    /// <c>{ "needs-human": "critical", "review-gate": "proceed-unreviewed" }</c> under
    /// <c>escalationThreshold: "high"</c> cannot route around it. Auto-best-guessing a critical hard call
    /// while ALSO skipping review is "Guardrails with no guardrails" (self-defeating), so this is a
    /// load-time ERROR, not merely discouraged. <c>proceed-unreviewed</c> stays a valid named opt-in at
    /// the cautious / <c>high</c> dials (no reachable <c>critical</c>) and is NOT flagged there. The core
    /// is a reusable predicate re-checkable on the EFFECTIVE config after <c>--dial</c>/<c>--autonomous</c>
    /// mutate it post-load (implemented by a later task).
    /// </summary>
    public const string IncompatibleAutonomyCompoundConfig = "GR2040";

    // --- writeScope required on every task (issue #389, SSOT §3.4) ------------------------------------

    /// <summary>
    /// A task's <c>task.json</c> omits <c>writeScope</c> entirely — the field is absent/null (issue #389,
    /// SSOT §3.4). <c>writeScope</c> is REQUIRED on EVERY task; omitting it is the "lazy planning" this
    /// forbids, because an absent scope would skip the write-scope check and let the task write anywhere
    /// in the repo unbounded. The three states are: <c>"writeScope": ["src/Foo/"]</c> — writes those
    /// paths; <c>"writeScope": []</c> — a DELIBERATE "writes nothing to the repo" declaration, which is
    /// VALID and never flagged (the correct form for a verification/read-only check, a database-configure
    /// task, or a state-only task whose only output is <c>GUARDRAILS_STATE_OUT</c>, which is NOT a repo
    /// write and never appears in the segment diff); and the field ABSENT/null — this ERROR. Requiring a
    /// scope everywhere makes every write surface explicit and reviewable and closes the #375 Q2 loophole
    /// (a no-<c>writeScope</c> task could silently edit its own <c>guardrails/</c>). The runtime
    /// belt-and-suspenders is <see cref="Execution.WriteScopeCheck"/>, which fail-closes a null scope to
    /// an empty one (writes nothing allowed) rather than passing.
    /// </summary>
    public const string MissingWriteScope = "GR2041";

    // --- structural over-scope lint (issue #378, SSOT §3.4) ------------------------------------------

    /// <summary>
    /// A task's emitted <c>task.json</c> carries the co-occurring STRUCTURAL over-scope fingerprint of a
    /// fan-in / composition-root-wiring SINK (issue #378, SSOT §3.4). A WARNING, not an error: the signals
    /// are mechanically checkable from the task graph, but "over-scoped" is a judgement the author may have
    /// a defensible reason for, so this surfaces it for <c>/guardrails-review</c> to acknowledge or resolve
    /// rather than hard-failing <c>validate</c>. Fires when ANY of the following holds — each is a profile
    /// that thrashes and times out mid-run, re-running the whole oversized action on every guardrail miss:
    /// <list type="bullet">
    ///   <item>(i) <c>action.maxTurns &gt;= </c><see cref="Loading.PlanValidator.OverScopeTurnThreshold"/>
    ///     AND <c>writeScope.Count &gt;= 4</c> — the author's own turn-heavy budget bump co-occurring with a
    ///     multi-file surface is the exact thrash-and-timeout profile (the motivating task-15 shape);</item>
    ///   <item>(ii) <c>writeScope.Count &gt;= 6</c> regardless of budget — a wide blast radius whose retry
    ///     re-does the whole multi-file change;</item>
    ///   <item>(iii) <c>dependsOn.Count &gt;= 5</c> AND <c>writeScope.Count &gt;= 3</c> — a fan-in sink that
    ///     composes ≥5 upstream producers into a multi-file composition root.</item>
    /// </list>
    /// The message names the offending signals and the split remedy (one task per collaborator wiring, with
    /// the turn-expensive composition-root proof isolated to a thin sink). Post-#389 every task has a
    /// <c>writeScope</c>, so cardinality is always present; a non-writing task's <c>[]</c> (Count 0) never
    /// trips any clause. Keys on a NAMED turn threshold (≈60), NOT the current literal max (75), so the lint
    /// does not silently break when the #94 max budget bump moves.
    /// </summary>
    public const string StructuralOverScope = "GR2042";

    // --- difficulty tier (`action.tier` / `tiering.defaultTier`, issue #225, SSOT §2/§3) ---------------

    /// <summary>
    /// A declared difficulty tier is not one of the recognised tokens <c>easy</c>, <c>medium</c> or
    /// <c>hard</c> (SSOT §3, issue #225) — at either site a tier can be declared: a task's
    /// <c>task.json action.tier</c>, or the plan-wide <c>tiering.defaultTier</c> in <c>guardrails.json</c>.
    /// The tier is a CLOSED token set (unlike a model identifier, GR2030, which has no enumerable valid
    /// set), so an unrecognised value can only ever be a typo — and one that would otherwise fail silently:
    /// nothing routes on a tier in Stage 1, so a garbage value would sit in the plan undetected until the
    /// Stage 2 resolver could not match it. The plan-wide default is checked too, and is the more dangerous
    /// of the two: a typo there applies a garbage tier to EVERY untagged task in the plan. Matched
    /// VERBATIM — no trimming, no case-folding — so <c>"hard "</c> is reported rather than silently
    /// accepted (the GR2030 "preserve the malformed signal" doctrine). An ERROR; a <c>null</c>/absent tier
    /// at either site is fine (untagged) and is never flagged.
    /// </summary>
    public const string InvalidTierValue = "GR2043";

    // --- provider registry: the kind discriminator + the three per-model axes (issue #224, SSOT §9) ---
    //
    // GR2043 is deliberately SKIPPED here: it is allocated by the concurrent action-tier change in this same
    // model-tiering Stage 1 plan (InvalidTierValue). Taking it twice for two different meanings is the one
    // outcome a code registry must not produce, and a gap costs nothing — codes are opaque identifiers.
    // (Post-merge: that action-tier change has landed, so GR2043 = InvalidTierValue now sits directly above
    // and no gap remains; this block still starts at GR2044.)

    /// <summary>
    /// A <c>promptRunners.&lt;name&gt;.kind</c> value is present but is not one of the recognised runner
    /// kinds <c>claude</c> / <c>codex</c> / <c>openrouter</c> / <c>local</c> (SSOT §9, issue #224). The
    /// discriminator selects which runner IMPLEMENTATION serves the block; a value that names no
    /// implementation can only ever be a typo or a kind from a newer Guardrails, and the message NAMES the
    /// offending value so an operator with several blocks knows which one to fix. An ERROR — the loader
    /// falls the block back to the <c>claude</c> default only so the REST of validation still reports, never
    /// so the run proceeds (any error blocks it). Distinct from the RECOGNISED-but-unimplemented kind, which
    /// loads clean and fails at registry construction with an actionable message (charter §A.2 — the
    /// backstop, not the gate).
    /// </summary>
    public const string InvalidPromptRunnerKind = "GR2044";

    /// <summary>
    /// One of the three per-model AXES on a <c>promptRunners.&lt;name&gt;</c> block is malformed (SSOT §9,
    /// issue #224 / charter Decision 7): a <c>costly</c> that is not a boolean, a <c>strength</c> that is
    /// not an integer or is below 1 (higher = stronger, so there is no meaningful zeroth or negative
    /// capability), or a <c>specialization</c> outside
    /// <c>coding</c>/<c>planning-reasoning</c>/<c>general</c>/<c>unspecified</c>. All three axes are
    /// OPTIONAL and an absent axis is never flagged — but a PRESENT one that cannot be understood is an
    /// ERROR rather than a silent drop, because silently ignoring it would leave the operator believing they
    /// had expressed a routing preference the Stage 2 resolver (#226) will never see. The message names the
    /// offending AXIS (and its value), one diagnostic per malformed axis. The type checks live in
    /// <see cref="PlanLoader"/> (the only place holding the raw JSON); the <c>strength &gt;= 1</c> range
    /// check lives in <see cref="PlanValidator"/> alongside the other optional-positive checks (cf. GR2012
    /// <c>maxCostUsd</c>, GR2023 <c>maxOutputTokens</c>, GR2036 <c>expectedDurationSeconds</c>).
    /// </summary>
    public const string InvalidRunnerAxis = "GR2045";

    /// <summary>
    /// A <c>promptRunners.&lt;name&gt;.routing</c> block still carries the RETIRED <c>rank</c> key (SSOT §9,
    /// issue #224, settled OD-F). Ordering is ascending <c>strength</c> — the weakest model that can serve
    /// the tier goes first — and <c>rank</c> is not modelled anywhere, so the key is IGNORED. A WARNING, not
    /// an error: a config mid-migration must keep loading. Not silence either: accepting <c>rank</c> quietly
    /// is exactly how a migrated config's ordering would change without anyone being told, which is the one
    /// outcome this key's retirement must not produce.
    /// </summary>
    public const string RetiredRoutingRank = "GR2046";

    // --- model tiering Stage 1.5: the DoR reconciliation (issue #201, docs/plans/17-model-tiering.md
    //     §13.2) --------------------------------------------------------------------------------------
    //
    // Next-free allocation RE-CONFIRMED against this file at landing time, per §13's standing
    // instruction: GR2046 (RetiredRoutingRank) was the last taken code and the marker line read
    // "CURRENT next-free code: GR2047". The design-of-record reserved GR2043–GR2054 on 2026-08-12 after
    // its ORIGINAL GR2037–GR2045 reservation was overtaken by #346, #383, #361, #389 and #378 while the
    // design sat in draft; Stage 1 then allocated GR2043–GR2046 on its own numbering WITHOUT
    // re-verifying, so the remaining codes were re-reserved at GR2047–GR2054. This slice takes the first
    // four of that re-reservation — GR2047–GR2050 — at the numbers §13.2 names, which are the numbers the
    // file agrees with. The lesson §13 draws is the one this block exists to honour: a code reservation
    // held in an unmerged document is a wish; THE FILE WINS, so re-verify here, not there.
    //
    // Deliberately NOT taken by this slice: GR2051 (NonRoutableBlockIsDefault), GR2052
    // (CostlyBlockRoutingInert) and GR2053 (PinAndTierCoexist) — the three v1 WARNINGS — which
    // Stage 3 later ALLOCATED at exactly those numbers; they are shipped constants below, not gaps.
    // GR2054 (RoutingNumericNonPositive), the one v2 (#227 probes) code, is still RESERVED and free.

    /// <summary>
    /// A <c>promptRunners.&lt;name&gt;.routing</c> block is malformed (SSOT §9, issue #224 / DoR §4.2):
    /// <c>tiers</c> is missing, is not an array, is EMPTY, holds a non-string element, or holds a value
    /// that is not one of <c>easy</c>/<c>medium</c>/<c>hard</c>. <c>tiers</c> is the MACHINE-CONSUMED half
    /// of <c>routing</c> — it is what makes the block a candidate for a rung (DoR §6.2's candidacy
    /// predicate reads nothing else) — so a <c>routing</c> block without a usable <c>tiers</c> declares an
    /// eligibility it cannot express. An ERROR rather than a warning because the failure is silent
    /// otherwise: the block would simply never be selected, and its author would read the config as
    /// opting in. Matched VERBATIM — no trimming, no case-folding — so <c>"hard "</c> is reported rather
    /// than silently accepted (the GR2030/GR2043 "preserve the malformed signal" doctrine). The sibling
    /// keys <c>guidance</c>/<c>notes</c>/<c>tags</c> are prose/advisory and are never parsed for a routing
    /// decision (invariant 1), so nothing about them is checked here.
    /// </summary>
    public const string MalformedRoutingGuidance = "GR2047";

    /// <summary>
    /// A tier that the plan actually USES has no CANDIDATE block at or above it, in a plan where tiering
    /// is CONFIGURED (SSOT §9.6, DoR §6.2/§14.1 — settled OD-G). "Used" spans all three declaration
    /// sites: a task's <c>action.tier</c>, a judge guardrail's frontmatter <c>tier</c>, and the plan-wide
    /// <c>tiering.defaultTier</c>. "Candidate" is the ONE predicate the whole feature shares —
    /// <c>routing</c> present AND the rung ∈ <c>routing.tiers</c> AND <c>costly</c> is not <c>true</c>
    /// (an ABSENT <c>costly</c> is "not stated" and behaves as NOT-costly here, because an un-annotated
    /// registry must stay routable).
    ///
    /// <para>Two DIFFERENT configurations reach this error and the message MUST distinguish them, because
    /// they have different fixes: (a) no block declares the rung at all — register one, or widen an
    /// existing block's <c>routing.tiers</c>; (b) blocks DO declare it but every one of them is
    /// <c>costly: true</c>, which the harness may never auto-select — pin the task explicitly
    /// (<c>action.runner</c>/<c>action.model</c>, a costly model is reachable by the USER's assignment,
    /// just never by the harness's choice), or clear the flag, or add the rung to a non-costly block.</para>
    ///
    /// <para>An ERROR, and deliberately so: the actor route is LOAD-BEARING, so it HALTS rather than
    /// degrading (DoR invariant 5 — "degrade what is advisory, halt what is load-bearing"). The harness
    /// will not fall back to a weaker rung (that routes weaker than asked) and will not reach for the
    /// costly block (that is the floor, and the floor has no override). Static and config-only: no
    /// resolver is consulted, so this fires at <c>validate</c> time, before a token is spent. Gated on
    /// tiering being CONFIGURED (≥1 block declares <c>routing</c>) — a plan with tags and NO routing block
    /// anywhere is <see cref="TieringInert"/> (a warning) instead, never a cascade of these.</para>
    /// </summary>
    public const string UnservableTier = "GR2048";

    /// <summary>
    /// The plan carries difficulty tier tags but NO <c>promptRunners</c> block declares <c>routing</c>
    /// (SSOT §2/§9.6, DoR §4.2 — the configured-vs-active rule). Tiering is CONFIGURED iff at least one
    /// block declares <c>routing</c>; without one there is nothing for a tier to resolve against, so every
    /// tag is inert and the plan runs by LEGACY resolution (the runner's own model / the CLI default) —
    /// exactly as it does today.
    ///
    /// <para>A WARNING, not an error: the plan is completely runnable and its behaviour is today's
    /// behaviour, so failing it would break plans that tag ahead of registering providers. Not silence
    /// either — an author who wrote tags believes they are routing, and the gap between "I tagged this
    /// easy" and "it ran on the frontier model anyway" is precisely the kind of quiet no-op this repo
    /// refuses to ship. Reported ONCE per plan, at the config, rather than once per tagged task.</para>
    /// </summary>
    public const string TieringInert = "GR2049";

    /// <summary>
    /// A present <c>effort</c> value (SSOT §2/§3/§9, issue #201) fails the shape check: it is empty,
    /// whitespace-only, or carries leading/trailing/embedded whitespace or a control character. Checked
    /// at both sites <c>effort</c> can be declared — <c>promptRunners.&lt;name&gt;.effort</c> and a task's
    /// <c>task.json action.effort</c>.
    ///
    /// <para><c>effort</c> is an OPAQUE per-block thinking-effort knob (<c>"low"</c>, <c>"xhigh"</c>, …),
    /// TRANSLATED by the runner CLASS into whatever its CLI/API exposes — the spelling is quarantined
    /// there exactly as <c>maxOutputTokens</c> → <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> is. There is
    /// therefore no enumerable set of legal values to check against, which is why this mirrors GR2030's
    /// <c>model</c> check exactly rather than being a membership test: a value shaped like this can never
    /// be a real effort token and is always a configuration mistake (an empty string left by templating, a
    /// stray quoted space) that would otherwise reach the runner verbatim. An ERROR: a load-time catch in
    /// place of a runtime invocation failure. A <c>null</c>/absent <c>effort</c> at either site is fine
    /// and is never flagged. Nothing CONSUMES <c>effort</c> yet — the Stage 2 resolver (#226) is its first
    /// reader — so this stage only proves it parses, validates, and round-trips.</para>
    /// </summary>
    public const string EffortInvalid = "GR2050";

    // --- model tiering Stage 3: the config-net slice (issue #201, docs/plans/17-model-tiering.md
    //     §13.2, §12.6) -------------------------------------------------------------------------------
    //
    // Allocation re-verified against this file at landing time, per §13's standing instruction that a
    // code reservation held in an unmerged document is a wish and THE FILE WINS. These three are not
    // drawn from the marker at the foot of this file: they are the GAPS below it that §13.2 held open
    // for this epic, so that marker stays at GR2065 — filling a gap advances no counter, and advancing
    // it would silently renumber somebody else's next code.
    // GR2054 (RoutingNumericNonPositive) stays RESERVED for the v2 probes work (#227).
    //
    // All three are WARNINGS, and DoR §12.6 gives the reason once for all of them: the plan still runs.
    // Each catches a config that LOADS CLEANLY and then does something other than what its author
    // wrote — the class of defect this repo refuses to leave silent.

    /// <summary>
    /// A NON-ROUTABLE block is named the registry <c>default</c> in a plan where tiering is CONFIGURED
    /// (SSOT §9.6, DoR §4.2 — review comment 7's back door). Non-routable spans BOTH reservation forms
    /// §4.2 declares, because they are not equivalent but have the same effect here: <c>costly: true</c>
    /// (the DECLARED form — "the harness may never choose this; only a human may assign it") and a block
    /// carrying no <c>routing</c> at all (the INCIDENTAL form — it simply has no place in the tier
    /// system). Either way the block sits outside <see cref="UnservableTier"/>'s candidacy predicate,
    /// and pointing <c>default</c> at it re-opens from behind exactly what the reservation closed in
    /// front: an UNTAGGED task with no <c>tiering.defaultTier</c> falls to LEGACY resolution — the
    /// default runner — which is the reserved model. The reservation evaporates through the back door.
    ///
    /// <para>A WARNING, not an error, and the boundary is precise. Naming a block <c>default</c> IS a
    /// user assignment — a plan-wide one — so it does not VIOLATE the costly floor: DoR §6.2's floor has
    /// no override and no dial, but it governs what the HARNESS chooses, and this is a human choosing
    /// (§6.2 lists the <c>default</c> pointer as one of the two sanctioned routes to a costly model).
    /// What earns the flag is that untagged work would then spend that model SILENTLY — this is the §5
    /// cost disclosure with a flag on it, not a refusal. DoR §12.6 is explicit that the plan still
    /// runs.</para>
    /// </summary>
    public const string NonRoutableBlockIsDefault = "GR2051";

    /// <summary>
    /// A <c>costly: true</c> block ALSO declares <c>routing</c> (SSOT §9.6, DoR §4.2/§6.2). The two keys
    /// state opposite things about the same block — <c>routing.tiers</c> says "consider me for these
    /// rungs", <c>costly</c> says "never choose me" — and <c>costly</c> wins: §6.2's ONE candidacy
    /// predicate excludes costly blocks at EVERY rung, their own included, and so does every consumer
    /// built on it (the resolver, the climb to a stronger rung, the §6.5 judge bump, and in v2 the
    /// ladder). The <c>routing</c> declaration is therefore INERT — its author registered an eligibility
    /// nothing will ever read.
    ///
    /// <para>A WARNING rather than an error, and deliberately so that <see cref="UnservableTier"/> can
    /// report the real consequence. The two codes divide the case between them: this one names the
    /// contradiction at the block, while GR2048 — an ERROR — fires only when the inert routing actually
    /// leaves a USED tier with no candidate, which is the outcome that stops a run. Failing here would
    /// reject configs that are merely redundant: a block annotated ahead of an un-reservation, or one
    /// whose <c>routing</c> outlived the day <c>costly</c> was set on it. Both route correctly today, so
    /// the plan still runs (DoR §12.6). Not silence either — the gap between "I declared these tiers"
    /// and "the block was never once considered" is precisely the quiet no-op this repo refuses.</para>
    /// </summary>
    public const string CostlyBlockRoutingInert = "GR2052";

    /// <summary>
    /// One action carries BOTH a full pin — <c>action.runner</c> or <c>action.model</c> — and
    /// <c>action.tier</c> (SSOT §2, DoR §6.1, devil's-advocate finding F3). §6.1's precedence chain puts
    /// the pin FIRST and has it bypass tier resolution ENTIRELY, so the tier is dead weight the pin
    /// overrides: it selects no block, it decides nothing, and it is usually an authoring mistake — a
    /// tag left behind when the task was pinned, or a pin added without the tag being cleared. Note the
    /// deliberate narrowness: a pin plus <c>action.effort</c> is NOT this code and is never flagged,
    /// because effort ALONE legitimately overrides the pinned route's effort (§6.1 item 2, DA F4).
    ///
    /// <para>A WARNING, not an error: the action is unambiguous — the pin runs, exactly as it does
    /// today — so refusing to load a plan over a redundant tag would fail work that behaves correctly.
    /// Not silence either, because the two readings of the same file diverge: an author who wrote
    /// <c>tier</c> believes the task is being ROUTED, and has no other way to learn that a pin is still
    /// deciding for it. DoR §12.6 is explicit that the plan still runs.</para>
    /// </summary>
    public const string PinAndTierCoexist = "GR2053";

    /// <summary>
    /// <b>GR2055 — a guardrail that CANNOT PASS for any input (issue #484).</b> A script guardrail
    /// builds a test <c>--filter</c> from a literal collection of N names and then guards on a
    /// zero-match floor demanding M, with <c>M &gt; N</c>. The filter can never select more than N
    /// tests, so the floor is unreachable and the guardrail exits non-zero on every possible attempt —
    /// the task dead-ends at <c>needs-human</c> with its implementation complete.
    /// <para>Measured instance: a filter naming SIX clauses guarded by <c>if ($ran -lt 14)</c>. The
    /// floor was correct for an earlier WHOLE-CLASS filter (nine + five); a later scoping fix narrowed
    /// the filter and left the floor behind. Each edit was individually sound, and the two numbers sit
    /// ~30 lines apart — which is exactly why a human review pass missed it twice.</para>
    /// <para>Sibling of GR2037's registry and of GR2057 (a clause requiring a token it also forbids, #470):
    /// the family is "unsatisfiable by construction". It is INVISIBLE to execution probes (#479) —
    /// such a guardrail is red before the task runs, which is correct, and red forever, which is not,
    /// and a baseline probe cannot tell those apart.</para>
    /// <para>Deliberately CONSERVATIVE: fires only when the counted collection is demonstrably the one
    /// feeding the filter, so an unrelated array and an unrelated threshold in the same script cannot
    /// collide into a false positive. A validator that cries wolf gets ignored.</para>
    /// </summary>
    public const string UnsatisfiableGuardrailFloor = "GR2055";

    /// <summary>
    /// <b>GR2056 — a guardrail SCRIPT that does not PARSE (issue #473).</b> It fails unconditionally:
    /// every attempt runs the action and then trips over a syntax error the agent cannot fix, because
    /// the guardrail script is not in its write scope. The task burns its whole retry budget and
    /// settles <c>needs-human</c>. Measured cost of one instance: two attempts plus a halt, for a
    /// stray backtick inside a double-quoted string.
    /// <para>Detected by asking the language's own interpreter to PARSE the file — never to run it —
    /// so <c>validate</c> remains read-only and safe for CI. Silence is not proof of validity: an
    /// absent interpreter or an unsupported language reports nothing.</para>
    /// </summary>
    public const string GuardrailScriptDoesNotParse = "GR2056";

    /// <summary>
    /// <b>GR2057 — a guardrail that REQUIRES a token it also FORBIDS (issue #470 ask 1).</b> One script
    /// guardrail carries a required-present clause and a forbidden-present clause over the SAME subject
    /// text, and the literal the first demands trips the pattern the second bans. No file satisfies both:
    /// removing the text fails the required clause, keeping it fails the forbidden one. Every attempt
    /// fails identically, the retry feedback is coherent, actionable and WRONG, and the task dead-ends at
    /// <c>needs-human</c> having never been achievable.
    /// <para>Measured instance: a required <c>[Trait("Category", "TierResolution")]</c> attribute whose own
    /// STRING LITERAL carries the token a clause 40 lines later forbids (<c>TierResolver|TierResolution</c>,
    /// a correctly-motivated #176 negative assertion). Each clause is individually correct, which is why
    /// reading the script top-to-bottom does not find it — it was found by EXECUTING the guardrail. Blast
    /// radius: the guardrail's task authored a wave's conformance suite that three downstream tasks
    /// depended on.</para>
    /// <para>Sibling of GR2055 (an arithmetic dead-end) and GR2056 (a script that does not parse): the
    /// family is "unsatisfiable by construction", the mirror image of the WEAKNESS <c>/guardrails-review</c>
    /// hunts. Like both siblings it is INVISIBLE to the #479 execution probes — such a guardrail is red
    /// before the task runs, which is correct, and red forever, which is not, and a baseline probe cannot
    /// tell those apart. Not to be confused with GR2026, which is the opposite polarity (the guardrail
    /// REQUIRES a token the PROMPT never mentions).</para>
    /// <para>Deliberately CONSERVATIVE: same-file, same-subject-variable clause pairs only, and only where
    /// the required pattern de-regexes to an exact literal. The cross-file variant (one guardrail requires
    /// what a sibling forbids) is strictly harder and out of scope by #470's own direction.</para>
    /// </summary>
    public const string GuardrailRequiresForbiddenToken = "GR2057";

    /// <summary>
    /// <b>GR2058 — the GR2037 banned-pattern scan could not reach a verdict for one (guardrail, entry) pair
    /// (issue #487).</b> A registry entry's matcher hit its bounded match timeout against one guardrail's
    /// body. The pair is SKIPPED and the rest of the scan continues.
    /// <para>WARNING, never an error, and never a crash. <c>validate</c> is read-only, fast, and run in CI;
    /// a timeout says the scan could not reach a verdict, not that the plan is invalid — the same class of
    /// event as GR2056's absent interpreter, which reports nothing because punishing the plan author for
    /// the operator's environment is wrong. It is louder than GR2056's silence for one reason: unlike a
    /// missing interpreter, a timeout is evidence of something genuinely odd in the registry entry or the
    /// script, it should never occur, and silence would leave a pathological entry undiagnosable.</para>
    /// <para>Distinct from GR2037 rather than a second severity on it, so that a consumer keying on GR2037
    /// still reads exactly one thing: a banned construction WAS FOUND. Not reachable by any realistic
    /// guardrail: the registry's costliest entry is strictly linear and would need roughly 7,000 candidate
    /// sites in a single script to reach the ceiling. It is a robustness fix, and must never be cited to
    /// justify weakening a registry entry.</para>
    /// </summary>
    public const string BannedPatternScanTimedOut = "GR2058";

    /// <summary>
    /// <b>GR2059 — a WAVE-ROOT guardrail declares <c>scope:"integration"</c>, where the tag is INERT
    /// (issue #459).</b> On a waved plan the per-union re-verify set is built from the task
    /// <c>&lt;task&gt;/guardrails/</c> folders plus the plan-root <c>&lt;plan&gt;/guardrails/</c> folder
    /// (SSOT §4.3, #451). A wave-root <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/</c> file is the wave's EXIT
    /// gate and is evaluated on a different contract (SSOT §14.3): exactly once, on the merged HEAD at
    /// wave end. Tagging it <c>integration</c> neither adds it to the union set nor is rejected — it
    /// simply does nothing.
    /// <para>Silence is the defect, not the behaviour. The author-facing promise of the tag is "this
    /// re-runs at every union point"; at the wave root that promise is false and nothing says so, so the
    /// plan LOOKS protected at the fan-in the check was most likely written for. That is the shape that
    /// produced #457 — a union-safe invariant (conflict-marker scan, duplicate-definition count) whose
    /// natural home is the wave that owns the colliding siblings, placed exactly there, never firing.</para>
    /// <para>A WARNING, deliberately, and deliberately not a fix. Making wave-root integration scope
    /// MEAN something changes the §14.3 wave-exit-gate contract — those files today have one evaluation
    /// point, and running them at every intra-wave union requires them to be UNION-SAFE (#125/#165): able
    /// to pass on a partial merge where downstream tasks have not run. A terminal postcondition tagged
    /// <c>integration</c> would start red-halting healthy partial merges. That is an architect call
    /// (#459 options 1 and 3), and this warning is the interim answer that is correct under every
    /// destination: whichever way the contract lands, telling the author today beats silence.</para>
    /// <para>Deliberately CONSERVATIVE: waved plans only, the wave-root <c>guardrails/</c> folder only,
    /// and only the exact recognised value <c>integration</c> — GR2021 already owns unrecognised spellings.
    /// It cannot fire on a flat plan, on a task guardrail, or on the plan root, all of which honour the tag.
    /// The adjacent question of whether <c>scope</c> means anything in ANY <c>preflights/</c> folder is a
    /// separate, unfiled one, left alone on purpose rather than folded in here.</para>
    /// </summary>
    public const string WaveIntegrationScopeInert = "GR2059";

    /// <summary>
    /// <b>GR2062 — a waved plan INTENDS more waves than it DECLARES, and no wave is left to author
    /// (issue #477, doc 19 §3.2, SSOT §2/§14.1).</b> The plan's <c>intendedWaves</c> disagrees with the
    /// number of <c>wave-*</c> folders on disk while <c>planIsClosed</c> holds — every declared wave has
    /// tasks — so the #365 one-ahead invariant is not merely PENDING, it is GONE.
    /// <para><b>The measured incident.</b> A charter settled THREE waves; the wave-2 brief carried the
    /// one-ahead step verbatim, warning and all; the JIT breakdown that owed the wave-3 stub TRUNCATED
    /// before reaching it. The hand-recovery restored the tasks and missed the stub, "because a stub leaves
    /// no forward reference to trip over the way a task does". <c>validate</c> was clean, <c>graph
    /// --check</c> was clean, two full review passes were clean — and the run drained 20 tasks and $115.32,
    /// whole suite passing, conformance 9/9, before failing at the terminal gate on a wave that was never
    /// authored. Wave intent was recorded NOWHERE machine-readable: <c>guardrails.json</c> carried no wave
    /// information, the SSOT recorded no count, <c>diagram.md</c> is regenerated FROM the folders so it can
    /// never disagree with them, and the charter is a sibling of the plan folder with no reference from
    /// inside it.</para>
    /// <para><b>WARNING, gated on <c>planIsClosed</c>.</b> The gate is what stops this becoming the warning
    /// that fires on every healthy JIT mid-plan state and is therefore ignored: while an un-authored wave
    /// stub is present a shortfall is EXPECTED — that is the one-ahead invariant working. A genuinely final
    /// wave has no successor and an author may legitimately collapse waves, so the value here is not
    /// enforcement; it is that a missing wave becomes NAMEABLE. Today nothing in the plan can be asked the
    /// question. The other polarity (<c>intendedWaves</c> BELOW the declared count — the plan grew past its
    /// stated intent) warns with the same code.</para>
    /// <para><b>Skipped entirely when <c>intendedWaves</c> is absent.</b> The field is optional and no
    /// existing plan is forced to migrate — the same rule GR2063 uses for its own manifest.</para>
    /// <para><b>Why the YAGNI objection does not carry, and the asymmetry worth preserving.</b> The obvious
    /// counter is that <c>intendedWaves</c> is a number the author can lower, and the author is the one who
    /// lost the wave. Doc 18 declined its own deferred lint partly because "the declaring agent is the agent
    /// the declaration grades". That does NOT transfer here, and the reason is TEMPORAL:
    /// <c>intendedWaves</c> is written at plan-folder creation (wave-1 authoring) and it grades a LATER,
    /// SEPARATE JIT-breakdown invocation — the one that truncated. <b>The declaration survives the event it
    /// guards.</b> And lowering it is a one-line diff in a reviewed config file, not a silent absence.</para>
    /// </summary>
    public const string IntendedWaveNotDeclared = "GR2062";

    /// <summary>
    /// <b>GR2063 — a wave's breakdown DECLARED more tasks than it AUTHORED (issue #402, SSOT §14.11).</b>
    /// The wave carries a <c>state/breakdown-intent.json</c> manifest and a declared <c>folder</c> has no
    /// complete task folder under that wave's <c>tasks/</c>. The message names the missing folders.
    /// <para><b>Silent when</b> the manifest is absent, unparseable, satisfied, or present-but-declaring
    /// nothing usable — that last case is GR2064's, not a second silence. Absent ⇒ skipped entirely — the
    /// same rule GR2062 uses for <c>intendedWaves</c>, and the same "silence is not proof of validity"
    /// discipline GR2056 set.</para>
    /// <para><b>WARNING, and the split is the point.</b> The HARNESS routes on the code (GR2063 present ⇒
    /// the wave is incomplete ⇒ it can never be reported <c>BreakdownComplete</c>, and the JIT checkpoint
    /// re-fires to resume it), so the automated path — where the risk actually lives — is fully gated. The
    /// human path gets a nudge, because a human who deliberately finishes a wave with 11 of 14 declared
    /// tasks has done nothing wrong; they have merely not updated the manifest. GR2025 is the shipped
    /// precedent for warn-at-validate / load-bearing-where-the-harness-reads-it.</para>
    /// <para><b>The false-positive claim is STRUCTURAL, not measured</b>, and is stated as the weaker claim
    /// it is: the manifest's lifetime is one breakdown attempt (the harness removes it when the wave
    /// settles), so no committed plan folder in the corpus can carry one — the zero rate is by
    /// construction, unlike GR2055/GR2056/GR2057's measured zero. The remedy named in the message is to
    /// correct or DELETE the manifest, i.e. to record the intent that actually holds.</para>
    /// </summary>
    public const string WaveBreakdownIncomplete = "GR2063";

    /// <summary>
    /// <b>GR2064 — a wave's breakdown-intent manifest EXISTS and PARSES but declares nothing usable
    /// (issue #402 follow-up, SSOT §14.11, doc 20 §4.6).</b> Every <c>tasks[].folder</c> was rejected —
    /// blank, carrying a path separator, or an ordinal duplicate — or there were no <c>tasks</c> entries at
    /// all, or the file's content is the JSON literal <c>null</c>. The message names the manifest path and
    /// lists each rejected entry with its reason.
    /// <para><b>Why this is not a fourth silence.</b> §14.11's three silent cases (absent, unparseable,
    /// satisfied) are each defensible; this fourth one was not, and it was silent only by accident. A
    /// manifest yielding zero folders read as <c>null</c> — byte-for-byte indistinguishable from ABSENT —
    /// so ONE typo bought the operator no GR2063, no prefix preservation, and no diagnostic naming either
    /// loss. A cut-off breakdown was then QUARANTINED rather than resumed, and the halt said "the wave
    /// carries no manifest" while the file sat on disk. That is a failure that fails in the direction that
    /// looks fine, over a mechanism whose entire purpose is salvage.</para>
    /// <para><b>WARNING, on GR2063's reasoning exactly.</b> Nothing here makes the plan invalid — the wave
    /// may be perfectly authored and the manifest merely stale junk — and <c>validate</c> must not fail for
    /// it. What the operator needs is to be TOLD, because the remedy is a one-line edit and the cost of not
    /// knowing is a quarantined wave. The remedy is named both ways: fix the <c>folder</c> values, or DELETE
    /// the manifest if the wave needs no declaration.</para>
    /// <para><b>False-positive rate: structurally zero, like GR2063.</b> The manifest's lifetime is one
    /// breakdown attempt (the harness removes it when the wave settles), so no committed plan folder can
    /// carry one. Measured across every committed plan folder in the corpus: zero. That is the weaker,
    /// by-construction claim, stated as weaker — not GR2055/GR2056/GR2057's measured-over-a-real-corpus
    /// zero.</para>
    /// <para><b>Deliberately NOT extended to the unparseable case.</b> Malformed JSON costs the same
    /// salvage and is arguably the same defect, but SSOT §14.11 and doc 20 §4.6 both record its silence as a
    /// deliberate call, and widening a documented silence is a contract move that belongs to its own
    /// decision, not to this fix.</para>
    /// </summary>
    public const string BreakdownIntentDeclaresNothing = "GR2064";

    // CURRENT next-free code: GR2065. GR2064 (BreakdownIntentDeclaresNothing) is the last taken code above —
    // GR2059 is the last CONTIGUOUS one; GR2060/GR2061 remain reserved-by-name gaps (GR2062 was TAKEN by
    // doc 19 Milestone B, #477; GR2063 by #402).
    // THREE codes remain RESERVED BY NAME in design documents and must not be re-used:
    //   GR2060 — docs/plans/19-producer-coverage.md §1 (a gate requires content nothing in the plan can produce)
    //   GR2061 — docs/plans/18-integration-proof-proximity.md §3.4 (the deferred seam-ledger lint, behind an evidence gate)
    //   GR2054 — docs/plans/17-model-tiering.md §13.2, RoutingNumericNonPositive, the v2 (#227 probes) code
    // GR2051–GR2053 were ALLOCATED by Stage 3 of the model-tiering epic (NonRoutableBlockIsDefault /
    // CostlyBlockRoutingInert / PinAndTierCoexist) and are shipped constants above, not gaps: those
    // three were the rest of §13.2's block. When allocating for anything ELSE, take GR2065 and update
    // this line rather than colliding with any of the three above (issue #320).
    //
    // GR10xx: next-free is GR1011 — GR1010 (WaveFolderIsNotALoadablePlan) was taken by the per-wave
    // review-marker change (#472). The GR10xx and GR20xx ladders advance independently; a doc that
    // states only one of them is half a fact, which is how the domain-knowledge skill came to claim
    // "GR1010 / GR2055" long after both were taken.
}
