// A COMPLETE, representative CORRECT artifact for 04-ignore-predicate-has-one-home.ps1 (#468/#302): the
// watch after stage 5's one-word promotion. Exactly one declaration, internal rather than private, all
// five patterns intact, and no behaviour change. Kept complete rather than a fragment - an incomplete
// valid sample fails for a DIFFERENT reason and masks the real one. This header quotes none of the
// tokens the clauses key on (taxonomy 13).
using System;
using System.Collections.Generic;
using System.IO;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

internal sealed class LivePlanEditWatch
{
    private static readonly string[] IgnoredFileNames = [".DS_Store", "Thumbs.db"];

    private static readonly string[] IgnoredSuffixes = [".swp", ".orig", ".rej"];

    private readonly PlanDefinition _plan;
    private readonly Dictionary<string, TaskSnapshot> _baseline = new(StringComparer.Ordinal);

    internal LivePlanEditWatch(PlanDefinition plan)
    {
        _plan = plan;
        foreach (TaskNode task in plan.Tasks)
        {
            _baseline[task.Id] = TaskSnapshot.Of(task);
        }
    }

    public IReadOnlyList<PlanEdit> Poll()
    {
        var edits = new List<PlanEdit>();
        foreach (TaskNode task in _plan.Tasks)
        {
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string label, string absolutePath) in Journal.TaskDefinitionFiles.Enumerate(task))
            {
                if (IsEditorArtifact(absolutePath))
                {
                    continue;
                }

                string? hash = Hashing.HashText.TryOfFile(absolutePath);
                if (hash is not null)
                {
                    current[label] = hash;
                }
            }

            PlanEdit? edit = Diff(task, current);
            if (edit is not null)
            {
                edits.Add(edit);
            }

            _baseline[task.Id] = new TaskSnapshot(current);
        }

        return edits;
    }

    public void Rebaseline(params string[] taskIds)
    {
        IEnumerable<TaskNode> scope = taskIds.Length == 0
            ? _plan.Tasks
            : _plan.Tasks.Where(t => taskIds.Contains(t.Id, StringComparer.Ordinal));
        foreach (TaskNode task in scope)
        {
            _baseline[task.Id] = TaskSnapshot.Of(task);
        }
    }

    /// <summary>
    /// The ONE home for the editor-artifact ignore list. INTERNAL rather than private so the
    /// settle-time divergence gate shares this exact predicate: two copies would let a future pattern
    /// reach one and miss the other. It is applied HERE and never in the hasher - moving it there would
    /// move every recorded definition hash in every plan.
    /// </summary>
    internal static bool IsEditorArtifact(string absolutePath)
    {
        string name = Path.GetFileName(absolutePath);
        foreach (string ignored in IgnoredFileNames)
        {
            if (string.Equals(name, ignored, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string suffix in IgnoredSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
