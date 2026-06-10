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
        ValidateDependencies(plan, diagnostics);
        ValidateNoCycles(plan, diagnostics);
        ValidateGuardrailsPresent(plan, diagnostics);
        ValidatePromptRunners(plan, diagnostics);
        ValidateInterpreters(plan, diagnostics);

        return diagnostics;
    }

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

    private static void ValidatePromptRunners(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind != ActionKind.Prompt || task.Action.Runner is null)
            {
                continue;
            }

            if (!plan.Config.PromptRunnerNames.Contains(task.Action.Runner))
            {
                diagnostics.Add(Error(DiagnosticCodes.UnknownPromptRunner, task.Action.Path,
                    $"Task '{task.Id}' references prompt runner '{task.Action.Runner}', which is not declared in promptRunners."));
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
}
