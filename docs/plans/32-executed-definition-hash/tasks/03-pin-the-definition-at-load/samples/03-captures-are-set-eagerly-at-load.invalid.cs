// The ONE defect 03-captures-are-set-eagerly-at-load.ps1 exists to catch, and section 5.2 names it the
// cheapest wrong implementation of the entire plan: a coalescing tail on the capture. It reads like
// defensive coding, it compiles, it passes every behavioural pin - and it restores the defect for any
// node the loader did not build. Identical to the .valid half apart from the two assignments.
using System.Collections.Generic;
using System.Linq;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

public sealed class PlanLoader
{
    private TaskNode? LoadTask(string taskDir, string planDir, List<Diagnostic> diagnostics)
    {
        ActionDefinition? action = ResolveAction(taskDir, diagnostics);
        if (action is null)
        {
            return null;
        }

        var node = new TaskNode
        {
            Id = Path.GetFileName(taskDir),
            Directory = taskDir,
            Description = ReadDescription(taskDir),
            DependsOn = ReadDependsOn(taskDir),
            Action = action,
            Guardrails = LoadGuardrails(taskDir),
            Preflights = LoadPreflights(taskDir),
        };

        return node with
        {
            DefinitionHashAtLoad  = node.DefinitionHashAtLoad ?? TaskDefinitionHash.Compute(node),
            DefinitionFilesAtLoad = SnapshotDefinitionFiles(node),
        };
    }

    private static IReadOnlyDictionary<string, string> SnapshotDefinitionFiles(TaskNode task)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string label, string absolutePath) in TaskDefinitionFiles.Enumerate(task))
        {
            string? hash = HashText.TryOfFile(absolutePath);
            if (hash is not null)
            {
                map[label] = hash;
            }
        }

        return map;
    }

    private static IReadOnlyList<WaveNode> QualifyWaveDependencies(IReadOnlyList<WaveNode> waves)
    {
        var rebuilt = new List<WaveNode>(waves.Count);
        foreach (WaveNode wave in waves)
        {
            var qualifiedTasks = new List<TaskNode>(wave.Tasks.Count);
            foreach (TaskNode task in wave.Tasks)
            {
                IReadOnlyList<string> qualified = Qualify(wave, task.DependsOn);
                qualifiedTasks.Add(task with { DependsOn = qualified });
            }

            rebuilt.Add(wave with { Tasks = qualifiedTasks });
        }

        return rebuilt;
    }
}
