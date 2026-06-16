using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// Semantic validation of a loaded <see cref="PlanDefinition"/> (SSOT §3–§5): DAG
/// reference integrity, the at-least-one-guardrail rule, prompt-runner name
/// references, and interpreter resolvability for every script extension the plan
/// actually uses. Structural/parse problems are caught earlier in <see cref="PlanLoader"/>.
/// </summary>
public sealed class PlanValidator
{
    private readonly IExecutableProbe _probe;

    public PlanValidator(IExecutableProbe probe) => _probe = probe;

    /// <summary>Validate with the real PATH probe.</summary>
    public PlanValidator() : this(new PathExecutableProbe()) { }

    /// <summary>Run every semantic check and return all diagnostics (errors and warnings).</summary>
    public IReadOnlyList<Diagnostic> Validate(PlanDefinition plan)
    {
        var diagnostics = new List<Diagnostic>();

        ValidateTaskIdsUnique(plan, diagnostics);
        ValidateStableIdsUnique(plan, diagnostics);
        ValidateStableIdFormat(plan, diagnostics);
        ValidateCostCap(plan, diagnostics);
        ValidateCaptureHashPaths(plan, diagnostics);
        ValidateDependencies(plan, diagnostics);
        ValidateNoCycles(plan, diagnostics);
        ValidateGuardrailsPresent(plan, diagnostics);
        ValidatePromptRunners(plan, diagnostics);
        ValidatePromptRunnerCommands(plan, diagnostics);
        ValidateInterpreters(plan, diagnostics);

        return diagnostics;
    }

    private static bool HasAnyPrompt(PlanDefinition plan) =>
        plan.Tasks.Any(t =>
            t.Action.Kind == ActionKind.Prompt ||
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt));

    private static void ValidateNoCycles(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (new Graph.DependencyGraph(plan.Tasks).FindCycle() is { } cycle)
        {
            diagnostics.Add(Error(DiagnosticCodes.DependencyCycle, plan.PlanDirectory,
                $"Dependency cycle: {string.Join(" -> ", cycle)}."));
        }
    }

    private static void ValidateTaskIdsUnique(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (!seen.Add(task.Id))
            {
                diagnostics.Add(Error(DiagnosticCodes.DuplicateTaskId, task.Directory,
                    $"Duplicate task id '{task.Id}'."));
            }
        }
    }

    /// <summary>
    /// A declared <c>stableId</c> must be unique across tasks (SSOT §3/§11). The regeneration
    /// merge keys task identity on <c>stableId</c>, so two tasks sharing one would be
    /// indistinguishable to it — a duplicate is almost always a copy-paste slip. Tasks without a
    /// stableId are skipped (it is optional; absent ⇒ identity falls back to the folder name).
    /// </summary>
    private static void ValidateStableIdsUnique(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.StableId is { } stableId && !seen.Add(stableId))
            {
                diagnostics.Add(Error(DiagnosticCodes.DuplicateStableId, task.Directory,
                    $"Task '{task.Id}' declares stableId '{stableId}', which is already used by another task."));
            }
        }
    }

    /// <summary>
    /// A declared <c>stableId</c> must match <c>^[a-z0-9][a-z0-9._-]*$</c> (SSOT §3/§11): lowercase
    /// alphanumerics, optionally with <c>. _ -</c>, starting alphanumeric. This reserves the format
    /// so a real stableId can never collide with the merge's synthetic <c>folder:&lt;name&gt;</c>
    /// identity (a colon is disallowed), and keeps ids stable across path/JSON handling. Tasks
    /// without a stableId are skipped (it is optional).
    /// </summary>
    private static void ValidateStableIdFormat(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.StableId is { } stableId && !StableIdPattern.IsMatch(stableId))
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidStableId, task.Directory,
                    $"Task '{task.Id}' declares stableId '{stableId}', which is not in the allowed format " +
                    "'^[a-z0-9][a-z0-9._-]*$' (lowercase alphanumerics, optionally with '.', '_' or '-')."));
            }
        }
    }

    private static readonly Regex StableIdPattern =
        new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The per-run cost cap (<c>maxCostUsd</c>, SSOT §2) must be positive when present. A zero or
    /// negative cap would trip before any work could run — almost always a configuration mistake —
    /// so it is an ERROR (GR2012). An absent cap is the no-cap default and is fine.
    /// </summary>
    private static void ValidateCostCap(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (plan.Config.MaxCostUsd is { } cap && cap <= 0m)
        {
            diagnostics.Add(Error(DiagnosticCodes.CostCapNonPositive, plan.PlanDirectory,
                $"maxCostUsd is {cap}, but a cost cap must be positive; a zero or negative cap would " +
                "halt the run before any work could run."));
        }
    }

    /// <summary>
    /// Every <c>captureHashes</c> entry (SSOT §3.1) must be a safe workspace-relative path. The
    /// harness resolves each as <c>Path.GetFullPath(Path.Combine(workspace, entry))</c> and then
    /// hashes/reads the file, so an absolute path, a drive- or root-rooted path, or a path that
    /// normalizes outside the workspace (e.g. <c>../../etc/passwd</c>) would reach outside the
    /// workspace — a GR2013 ERROR naming the offending task and path. Resolution uses the plan's
    /// configured workspace root (the action's per-task <c>workingDirectory</c> is the rare override
    /// that only ever narrows the cwd; the workspace root is the canonical containment boundary).
    /// </summary>
    private static void ValidateCaptureHashPaths(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        string workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Workspace));

        foreach (TaskNode task in plan.Tasks)
        {
            foreach (string entry in task.CaptureHashes)
            {
                if (EscapesWorkspace(workspaceRoot, entry))
                {
                    diagnostics.Add(Error(DiagnosticCodes.CaptureHashEscapesWorkspace, task.Directory,
                        $"Task '{task.Id}' declares captureHashes entry '{entry}', which is not a " +
                        "workspace-relative path: it is absolute/rooted or escapes the workspace root. " +
                        "captureHashes paths must stay inside the workspace."));
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="entry"/> is not safely contained by <paramref name="workspaceRoot"/>:
    /// it is rooted (absolute or drive-rooted such as <c>/etc</c> or <c>C:\x</c>), or its normalized
    /// resolution against the workspace leaves the workspace root. Comparison is on a directory
    /// boundary so a sibling like <c>workspace-evil</c> never counts as inside <c>workspace</c>.
    /// </summary>
    private static bool EscapesWorkspace(string workspaceRoot, string entry)
    {
        // A rooted entry ignores the workspace base entirely under Path.Combine — reject outright.
        if (Path.IsPathRooted(entry))
        {
            return true;
        }

        string resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(workspaceRoot, entry)));

        if (string.Equals(resolved, workspaceRoot, PathComparison))
        {
            // Resolves to the workspace root itself (e.g. "." or "sub/.."). Not a file inside it,
            // but it does not escape — leave that to the harness's missing-file path; not GR2013.
            return false;
        }

        string prefix = workspaceRoot + Path.DirectorySeparatorChar;
        return !resolved.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateDependencies(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var ids = plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (string dependency in task.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    diagnostics.Add(Error(DiagnosticCodes.UnknownDependency, task.Directory,
                        $"Task '{task.Id}' dependsOn '{dependency}', which is not a known task id."));
                }
            }
        }
    }

    private static void ValidateGuardrailsPresent(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Guardrails.Count == 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.NoGuardrails, task.Directory,
                    $"Task '{task.Id}' has zero guardrails; a task that cannot be verified is not allowed."));
            }
        }
    }

    /// <summary>
    /// Prompt-runner integrity (SSOT §2/§9). A plan with ANY prompt action or prompt
    /// guardrail must declare at least one runner under <c>promptRunners</c> (GR2008,
    /// because nothing could run those prompts). A prompt action that names a runner
    /// (<c>action.runner</c>) must name a declared one (GR2004). A prompt action/guardrail
    /// that relies on the default must have a usable default — either <c>promptRunners.default</c>
    /// resolves to a config, or there is exactly one declared runner to fall back to (GR2004).
    /// </summary>
    private static void ValidatePromptRunners(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        bool hasPrompts = HasAnyPrompt(plan);
        if (!hasPrompts)
        {
            return;
        }

        if (plan.Config.PromptRunners.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoPromptRunners, plan.PlanDirectory,
                "Plan has prompt action(s)/guardrail(s) but no 'promptRunners' configuration to run them. " +
                "Add a promptRunners block to guardrails.json (SSOT §2)."));
            return;
        }

        // Explicit runner references on prompt actions must resolve.
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind == ActionKind.Prompt && task.Action.Runner is not null &&
                !plan.Config.PromptRunnerNames.Contains(task.Action.Runner))
            {
                diagnostics.Add(Error(DiagnosticCodes.UnknownPromptRunner, task.Action.Path,
                    $"Task '{task.Id}' references prompt runner '{task.Action.Runner}', which is not declared in promptRunners."));
            }
        }

        // A prompt that relies on the default needs a resolvable default. The default is
        // promptRunners.default, falling back to the sole declared runner if exactly one.
        bool anyReliesOnDefault = plan.Tasks.Any(t =>
            (t.Action.Kind == ActionKind.Prompt && t.Action.Runner is null) ||
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt));

        if (anyReliesOnDefault && ResolveDefaultRunner(plan.Config) is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.UnknownPromptRunner, plan.PlanDirectory,
                "A prompt action/guardrail relies on the default prompt runner, but no default is resolvable. " +
                "Set promptRunners.default to a declared runner (or declare exactly one runner)."));
        }
    }

    /// <summary>The default runner name: <c>promptRunners.default</c> if it resolves, else the sole declared runner.</summary>
    private static string? ResolveDefaultRunner(RunConfig config)
    {
        if (config.DefaultPromptRunner is { } named && config.PromptRunnerNames.Contains(named))
        {
            return named;
        }

        return config.PromptRunnerNames.Count == 1 ? config.PromptRunnerNames.Single() : null;
    }

    /// <summary>
    /// Probe each DECLARED prompt runner's <c>command</c> on PATH (reusing the same
    /// <see cref="IExecutableProbe"/> as interpreter resolution). An unresolvable command is a
    /// WARNING (GR2009), not an error: the plan may have been authored to run on another
    /// machine where the runner is installed. Every declared runner is probed even if no task
    /// currently references it — a stale runner config is worth surfacing. Runs only after the
    /// GR2008 error path (no runners at all) has been handled by <see cref="ValidatePromptRunners"/>.
    /// </summary>
    private void ValidatePromptRunnerCommands(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values
                     .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (!_probe.Exists(runner.Command))
            {
                diagnostics.Add(Warning(DiagnosticCodes.PromptRunnerNotOnPath, plan.PlanDirectory,
                    $"Prompt runner '{runner.Name}' command '{runner.Command}' is not resolvable on PATH. " +
                    "Prompt tasks using this runner will fail unless it is installed on the machine that runs the plan."));
            }
        }
    }

    /// <summary>
    /// For every distinct extension used by a *script* action or guardrail, probe the
    /// interpreter. A used deterministic extension with no resolvable interpreter is an
    /// ERROR in M2 (we cannot run it). Prompt actions/guardrails validate fine here — they
    /// are run by a prompt runner, not the interpreter map (M5).
    /// </summary>
    private void ValidateInterpreters(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var interpreterMap = new InterpreterMap(_probe, plan.Config.Interpreters);

        // Distinct (extension, first-seen file) so each extension is reported once with a
        // concrete example path.
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string scriptPath in ScriptFiles(plan))
        {
            string extension = Path.GetExtension(scriptPath).ToLowerInvariant();
            if (!seenExtensions.Add(extension))
            {
                continue;
            }

            InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, []);
            switch (resolution.Status)
            {
                case InterpreterMap.Status.WrongPlatform:
                    diagnostics.Add(Error(DiagnosticCodes.InterpreterWrongPlatform, scriptPath,
                        $"Extension '{extension}' is only supported on Windows."));
                    break;
                case InterpreterMap.Status.NotOnPath:
                    string probed = string.Join("' / '", resolution.ProbedExecutables);
                    diagnostics.Add(Error(DiagnosticCodes.UnresolvableInterpreter, scriptPath,
                        $"No interpreter for extension '{extension}' is resolvable on PATH (tried '{probed}')."));
                    break;
            }
        }
    }

    private static IEnumerable<string> ScriptFiles(PlanDefinition plan)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind == ActionKind.Script)
            {
                yield return task.Action.Path;
            }

            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                if (guardrail.Kind == ActionKind.Script)
                {
                    yield return guardrail.Path;
                }
            }
        }
    }

    private static Diagnostic Error(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Path = path,
        Message = message
    };

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };
}
