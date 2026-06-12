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
}
