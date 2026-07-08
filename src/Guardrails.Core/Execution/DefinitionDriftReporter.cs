using Guardrails.Core.Graph;
using Guardrails.Core.Hashing;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Builds the <see cref="DefinitionDriftReport"/> for the resume definition-drift halt (SSOT §7.2, issue
/// #274 Part A). The drift itself is Tier 1 — an aggregate <c>TaskDefinitionHash</c> mismatch the caller
/// already detected, which NEVER depends on git. This reporter enriches each drifted task with Tier 2:
/// the transitive-descendant closure, the reference <c>git diff</c> command, and a best-effort per-file
/// breakdown (added / removed / modified + an approximate ± line count) recovered by comparing each
/// definition file's CURRENT on-disk bytes against its bytes at the task's old integration commit
/// (<c>Guardrails-Task-Hash:</c> trailer commit, §5.3) via the worktree provider's read-at-commit seam.
/// When the old bytes are unrecoverable (the plan folder was uncommitted at that commit, or there is no
/// plan-branch commit at all) the per-file breakdown degrades to empty with a note, and Tier 1 stands.
/// </summary>
internal static class DefinitionDriftReporter
{
    /// <summary>One task's Tier-1 drift facts, passed in by the caller that detected the mismatch.</summary>
    internal readonly record struct DriftInput(string TaskId, string OldHash, string NewHash, string? OldCommit);

    /// <summary>
    /// Build the report for <paramref name="drifted"/> (in plan order). <paramref name="provider"/>
    /// supplies the git recovery + repo-relative path; a null provider (serial mode / no git) yields
    /// Tier-1-only entries.
    /// </summary>
    public static DefinitionDriftReport Build(
        PlanDefinition plan,
        DependencyGraph graph,
        IReadOnlyList<DriftInput> drifted,
        IWorktreeProvider? provider)
    {
        var byId = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // Emit in plan order for a stable, legible report regardless of detection order.
        var driftById = drifted.ToDictionary(d => d.TaskId, StringComparer.Ordinal);
        var tasks = new List<DriftedTask>();
        foreach (TaskNode task in plan.Tasks)
        {
            if (!driftById.TryGetValue(task.Id, out DriftInput input))
            {
                continue;
            }

            (IReadOnlyList<ChangedDefinitionFile> changedFiles, string? note) =
                BuildChangedFiles(task, input.OldCommit, provider);

            IReadOnlyList<string> dependents = graph.TransitiveDependentsOf(task.Id)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();

            tasks.Add(new DriftedTask
            {
                TaskId = task.Id,
                OldHash = input.OldHash,
                NewHash = input.NewHash,
                OldCommit = input.OldCommit,
                ChangedFiles = changedFiles,
                DiffCommand = BuildDiffCommand(task, input.OldCommit, provider),
                Dependents = dependents,
                Note = note
            });
        }

        return new DefinitionDriftReport { Tasks = tasks };
    }

    /// <summary>
    /// The Tier-2 per-file breakdown: over the UNION of the task's current definition files and the files
    /// present under the task folder at the old commit (so a REMOVED file the current enumeration no
    /// longer yields is still named), recover each file's old bytes and classify it added / removed /
    /// modified. Returns (files, note) — note is non-null only when the old bytes could not be recovered
    /// for ANY file (degradation), in which case files is empty and Tier 1 (the aggregate hash) stands.
    /// </summary>
    private static (IReadOnlyList<ChangedDefinitionFile> Files, string? Note) BuildChangedFiles(
        TaskNode task, string? oldCommit, IWorktreeProvider? provider)
    {
        if (oldCommit is null || provider is null)
        {
            return ([], "prior versions not recoverable from git (no plan-branch commit recorded for this task); showing aggregate hash drift only.");
        }

        // Current definition files, task-relative path → absolute (the action label carries an "action:"
        // prefix; DisplayLabel strips it so it reads as its task-relative name, matching the old-tree rel).
        var currentByRel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string label, string abs) in TaskDefinitionFiles.Enumerate(task))
        {
            currentByRel[DisplayLabel(label)] = abs;
        }

        string actionRel = HashText.NormalizeRelative(task.Directory, task.Action.Path);

        // Old definition files present at the commit — recovers REMOVED guardrail/preflight files.
        var oldByRel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string abs in provider.ListFilesAtCommit(oldCommit, task.Directory))
        {
            string rel = HashText.NormalizeRelative(task.Directory, abs);
            if (IsDefinitionRel(rel, actionRel))
            {
                oldByRel[rel] = abs;
            }
        }

        // Defer files whose old bytes did not recover (candidate "added") so we can drop them if NOTHING
        // recovered (a wholly-untracked plan folder → not really "added").
        var entries = new List<(ChangedDefinitionFile File, bool IsDeferredAdd)>();
        bool anyRecovered = false;

        foreach (string rel in currentByRel.Keys.Union(oldByRel.Keys, StringComparer.Ordinal)
                     .OrderBy(r => r, StringComparer.Ordinal))
        {
            string abs = currentByRel.GetValueOrDefault(rel) ?? oldByRel[rel];
            bool currentExists = currentByRel.ContainsKey(rel) && File.Exists(abs);
            string? currentText = currentExists ? SafeRead(abs) : null;
            string? oldText = provider.ReadFileAtCommit(oldCommit, abs);

            if (oldText is not null)
            {
                anyRecovered = true;
                if (!currentExists)
                {
                    entries.Add((new ChangedDefinitionFile
                    {
                        Path = rel,
                        Change = "removed",
                        Removed = LineCount(oldText)
                    }, false));
                }
                else if (currentText is not null && !Equal(oldText, currentText))
                {
                    (int added, int removed) = LineDelta(oldText, currentText);
                    entries.Add((new ChangedDefinitionFile
                    {
                        Path = rel,
                        Change = "modified",
                        Added = added,
                        Removed = removed
                    }, false));
                }
                // else: recovered + unchanged → not part of the breakdown.
            }
            else if (currentExists)
            {
                entries.Add((new ChangedDefinitionFile
                {
                    Path = rel,
                    Change = "added",
                    Added = currentText is null ? null : LineCount(currentText)
                }, IsDeferredAdd: true));
            }
        }

        if (anyRecovered)
        {
            return (entries.Select(e => e.File).ToList(), null);
        }

        // Nothing recovered: the plan folder was not tracked at the old commit. Drop the false "added"
        // entries and degrade to Tier 1 + a note (the kept list is empty here — no recovered files).
        IReadOnlyList<ChangedDefinitionFile> kept = entries.Where(e => !e.IsDeferredAdd).Select(e => e.File).ToList();
        return (kept, $"prior file versions not recoverable from git at {Short(oldCommit)} " +
                      "(the plan folder was not tracked at that commit); showing aggregate hash drift only.");
    }

    /// <summary>
    /// True for a task-relative path that is part of the definition hash set: <c>task.json</c>, the
    /// current action file, or anything under <c>guardrails/</c> / <c>preflights/</c>. Filters stray
    /// non-definition files an old-tree listing might include so they never appear in the breakdown.
    /// </summary>
    private static bool IsDefinitionRel(string rel, string actionRel) =>
        rel == "task.json"
        || rel == actionRel
        || rel.StartsWith("guardrails/", StringComparison.Ordinal)
        || rel.StartsWith("preflights/", StringComparison.Ordinal);

    private static string BuildDiffCommand(TaskNode task, string? oldCommit, IWorktreeProvider? provider)
    {
        if (oldCommit is null)
        {
            return "(no plan-branch commit recorded for this task)";
        }

        string? repoRel = provider?.RepoRelativePath(task.Directory);
        string pathSpec = repoRel is not null ? $"{repoRel}/" : task.Directory;
        return $"git diff {Short(oldCommit)}..HEAD -- {pathSpec}";
    }

    /// <summary>Strip the <c>action:</c> label prefix so the action file shows as its task-relative path.</summary>
    private static string DisplayLabel(string label) =>
        label.StartsWith("action:", StringComparison.Ordinal) ? label["action:".Length..] : label;

    private static string? SafeRead(string absolutePath)
    {
        try { return File.ReadAllText(absolutePath); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool Equal(string a, string b) =>
        string.Equals(HashText.NormalizeNewlines(a), HashText.NormalizeNewlines(b), StringComparison.Ordinal);

    /// <summary>Line count after newline normalization, discounting a single trailing newline.</summary>
    private static int LineCount(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        string[] lines = HashText.NormalizeNewlines(text).Split('\n');
        int len = lines.Length;
        if (len > 0 && lines[^1].Length == 0)
        {
            len--; // a final newline yields a trailing empty element that is not a real line
        }

        return len;
    }

    /// <summary>
    /// An approximate ± line delta via a line MULTISET difference (deterministic, dependency-free): a line
    /// present more often in the new text counts as added; more often in the old counts as removed. Not a
    /// true LCS diff — a hint for the report, not a patch.
    /// </summary>
    private static (int Added, int Removed) LineDelta(string oldText, string newText)
    {
        var oldCounts = CountLines(oldText);
        var newCounts = CountLines(newText);

        int added = 0;
        foreach (KeyValuePair<string, int> kv in newCounts)
        {
            added += Math.Max(0, kv.Value - oldCounts.GetValueOrDefault(kv.Key));
        }

        int removed = 0;
        foreach (KeyValuePair<string, int> kv in oldCounts)
        {
            removed += Math.Max(0, kv.Value - newCounts.GetValueOrDefault(kv.Key));
        }

        return (added, removed);
    }

    private static Dictionary<string, int> CountLines(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in HashText.NormalizeNewlines(text).Split('\n'))
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        return counts;
    }

    private static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];
}
