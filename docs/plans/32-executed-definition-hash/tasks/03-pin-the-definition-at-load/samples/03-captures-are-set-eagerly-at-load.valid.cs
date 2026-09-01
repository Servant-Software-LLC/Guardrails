// A COMPLETE, representative CORRECT artifact for 03-captures-are-set-eagerly-at-load.ps1 (#468/#302):
// the loader's task-construction path after stage 3. One construction site, both captures assigned
// eagerly from the same enumeration, no fallback, no deferral, and a clone that rebinds only DependsOn.
// Kept complete rather than a fragment - an incomplete valid sample fails for a DIFFERENT reason and
// masks the real one. This header quotes none of the banned tokens (taxonomy 13).
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

        // Compute needs a FULLY-BUILT node - it reads Directory and the resolved Action.Path - so the
        // captures cannot sit inside the object initializer above. Both are taken here, eagerly, from
        // the bytes this loader has just read; nothing recomputes them later and nothing falls back to
        // disk if they are null.
        return node with
        {
            DefinitionHashAtLoad  = TaskDefinitionHash.Compute(node),
            DefinitionFilesAtLoad = SnapshotDefinitionFiles(node),
        };
    }

    /// <summary>
    /// The per-file definition surface, folded over the SAME enumeration TaskDefinitionHash uses and
    /// keyed by that enumeration's own labels, so the two surfaces can never disagree about what
    /// defines a task. Unreadable entries are skipped: the loader must stay total.
    /// </summary>
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
