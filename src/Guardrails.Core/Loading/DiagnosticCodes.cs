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

    /// <summary>
    /// A multi-leaf or fan-in plan has no <c>integrationGate:true</c> sink (plan 08 M2, SSOT §3.3).
    /// The terminal gate is the whole-repo soundness boundary for parallel execution; omitting it
    /// leaves parallel branches unverified at the integration level — an ERROR.
    /// </summary>
    public const string MissingIntegrationGate = "GR2017";

    /// <summary>
    /// An <c>integrationGate:true</c> sink carries no guardrail with <c>scope:"integration"</c>
    /// (plan 08 M2, SSOT §3.3/§4.3). A terminal gate with an empty integration-guardrail set
    /// verifies nothing — an ERROR; the gate task must have at least one integration-scoped guardrail.
    /// </summary>
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
}
