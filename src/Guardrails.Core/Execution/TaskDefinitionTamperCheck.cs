using System.Security.Cryptography;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The task-definition tamper check for an UNCONTAINED writer (#764, SSOT §9.9): a runner that writes
/// files but runs without the §9.4 containment hook (<see cref="Prompts.PromptRunnerKinds.IsUncontainedWriter"/> —
/// today only <c>cursor</c>, launched with <c>--force</c> and granted the plan directory by
/// <c>--add-dir</c>). Such an agent can rewrite the very guardrails about to grade it, and nothing else
/// notices: the plan directory is not the worktree, so the git-diff write-scope check never sees it.
///
/// <para>The check hashes the task's own definition files — exactly the set
/// <see cref="TaskDefinitionHash"/> folds (<c>task.json</c>, the action file, <c>guardrails/**</c>,
/// <c>preflights/**</c>) — before the action and again after it. Any added, removed or modified file FAILS
/// the attempt (never an advisory, unlike the plan-wide <see cref="LivePlanEditWatch"/>, which reports
/// operator edits between scheduler boundaries and is not attributable to one agent). Deliberately narrow:
/// the rest of the plan folder (other tasks, <c>guardrails.json</c>, plan-level gates) is not covered, and
/// neither are writes outside the plan directory.</para>
/// </summary>
internal sealed class TaskDefinitionTamperCheck
{
    private readonly TaskNode _task;
    private readonly IReadOnlyDictionary<string, string> _before;

    private TaskDefinitionTamperCheck(TaskNode task, IReadOnlyDictionary<string, string> before)
    {
        _task = task;
        _before = before;
    }

    /// <summary>Snapshot <paramref name="task"/>'s definition files now, before the action runs.</summary>
    public static TaskDefinitionTamperCheck Begin(TaskNode task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new TaskDefinitionTamperCheck(task, Snapshot(task));
    }

    /// <summary>
    /// The definition files that changed since <see cref="Begin"/>, each as <c>"&lt;label&gt; (added|removed|modified)"</c>
    /// in ordinal label order. Empty when nothing changed.
    /// </summary>
    public IReadOnlyList<string> Changes()
    {
        IReadOnlyDictionary<string, string> after = Snapshot(_task);
        var changes = new List<string>();

        foreach (string label in _before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            bool had = _before.TryGetValue(label, out string? oldHash);
            bool has = after.TryGetValue(label, out string? newHash);
            if (had && has && string.Equals(oldHash, newHash, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add($"{label} ({(!had ? "added" : !has ? "removed" : "modified")})");
        }

        return changes;
    }

    /// <summary>Label → content hash for every definition file that exists (raw bytes: any change counts).</summary>
    private static IReadOnlyDictionary<string, string> Snapshot(TaskNode task)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string label, string path) in TaskDefinitionFiles.Enumerate(task))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                hashes[label] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch (IOException)
            {
                // Unreadable (locked mid-write) is not evidence of a change; a sentinel keeps the file in the set
                // so a file that is unreadable on BOTH sides compares equal rather than vanishing.
                hashes[label] = "unreadable";
            }
        }

        return hashes;
    }
}
