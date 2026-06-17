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
    /// A <c>captureHashes</c> entry (SSOT §3.1) is not a safe workspace-relative path: it is
    /// absolute, drive- or root-rooted, or its normalized resolution escapes the workspace root
    /// (e.g. <c>../../etc/passwd</c>). The harness resolves each entry against the workspace and
    /// hashes/reads the file, so an escaping path could reach outside the workspace — an ERROR.
    /// </summary>
    public const string CaptureHashEscapesWorkspace = "GR2013";

    /// <summary>
    /// A task declares <c>restoreOnRetry: true</c> but has an empty/absent <c>captureHashes</c>
    /// (SSOT §3.1 / issue #51). Restore-on-retry acts ONLY on captured files, so opting in without
    /// any captured file is a no-op the author almost certainly did not intend — an ERROR naming the task.
    /// </summary>
    public const string RestoreOnRetryWithoutCaptureHashes = "GR2014";
}
