using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Core.Breakdown;

/// <summary>
/// The identity-aware regeneration merge (SSOT §11.3, issue #5). Given BASE (the lock), LOCAL
/// (the current on-disk folder = BASE + human guardrail CRUD), and REMOTE (a freshly generated
/// candidate from the changed plan), it preserves human guardrail edits while re-deriving
/// everything else from the plan. Tasks are matched by <c>stableId</c> (§3), so a renumbered or
/// reordered task still carries its human guardrails forward; a task the plan removed takes its
/// guardrails with it. The merge is the deterministic half — the LLM owns task-identity
/// assignment (which <c>stableId</c> a regenerated task reuses); this engine owns the mechanical
/// per-guardrail resolution and the conflict gate.
/// </summary>
public static class BreakdownMerge
{
    private const string TasksDir = "tasks";
    private const string ConfigFileName = "guardrails.json";
    private const string SeedRelPath = "state/seed.json";

    /// <summary>
    /// Compute the merge plan. Pure: it reads the three captured manifests and the two loaded
    /// plans, and produces resolutions + warnings without touching the filesystem. Tasks are
    /// keyed by <c>stableId</c> (falling back to <c>folder:&lt;name&gt;</c> when a task declares
    /// none, so unkeyed tasks match by folder name only).
    /// </summary>
    public static MergePlan Compute(
        BreakdownManifest baseManifest,
        PlanDefinition localPlan, BreakdownManifest localManifest,
        PlanDefinition remotePlan, BreakdownManifest remoteManifest)
    {
        ArgumentNullException.ThrowIfNull(baseManifest);
        ArgumentNullException.ThrowIfNull(localPlan);
        ArgumentNullException.ThrowIfNull(localManifest);
        ArgumentNullException.ThrowIfNull(remotePlan);
        ArgumentNullException.ThrowIfNull(remoteManifest);

        Dictionary<string, TaskNode> localById = IndexByIdentity(localPlan);
        Dictionary<string, TaskNode> remoteById = IndexByIdentity(remotePlan);

        var items = new List<GuardrailMergeItem>();
        var warnings = new List<string>();

        foreach ((string identity, TaskNode localTask) in localById)
        {
            if (remoteById.TryGetValue(identity, out TaskNode? remoteTask))
            {
                MergeMatchedTask(identity, localTask, remoteTask,
                    baseManifest, localManifest, remoteManifest, localPlan.PlanDirectory, remotePlan.PlanDirectory, items);
            }
            else
            {
                DropRemovedTask(identity, localTask, baseManifest, localManifest, localPlan.PlanDirectory, items, warnings);
            }
        }

        foreach ((string identity, TaskNode remoteTask) in remoteById)
        {
            if (!localById.ContainsKey(identity))
            {
                AddNewTask(identity, remoteTask, remotePlan.PlanDirectory, items);
            }
        }

        items.Sort(static (a, b) =>
            string.CompareOrdinal(a.ResultRelPath ?? a.LocalRelPath, b.ResultRelPath ?? b.LocalRelPath));

        return new MergePlan { Items = items, Warnings = warnings };
    }

    /// <summary>
    /// Apply a conflict-free plan in place on <paramref name="localFolder"/>: replace the
    /// authored content (<c>tasks/</c>, <c>guardrails.json</c>, and <c>state/seed.json</c> when
    /// REMOTE has one) with REMOTE's, overlay the preserved human guardrails, and re-write the
    /// lock so the merged folder becomes the new BASE. Harness-owned <c>state/</c> runtime and the
    /// generated <c>diagram.md</c> are left untouched. Throws if the plan still has conflicts.
    /// </summary>
    public static void Apply(MergePlan plan, string localFolder, string remoteFolder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasConflicts)
        {
            throw new InvalidOperationException("Cannot apply a merge plan with unresolved conflicts.");
        }

        string local = Path.GetFullPath(localFolder);
        string remote = Path.GetFullPath(remoteFolder);

        // 1. Read preserved human guardrail bytes BEFORE the local tasks tree is replaced.
        var preserved = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (GuardrailMergeItem item in plan.Items.Where(i => i.Action == GuardrailMergeAction.KeepLocal))
        {
            string source = Path.Combine(local, ToOsPath(item.LocalRelPath!));
            preserved[item.ResultRelPath!] = File.ReadAllBytes(source);
        }

        // 2. Replace authored content with REMOTE's (machine-owned: tasks tree + config + seed).
        string localTasks = Path.Combine(local, TasksDir);
        if (Directory.Exists(localTasks))
        {
            Directory.Delete(localTasks, recursive: true);
        }
        CopyDirectory(Path.Combine(remote, TasksDir), localTasks);

        CopyFileIfExists(Path.Combine(remote, ConfigFileName), Path.Combine(local, ConfigFileName));
        // seed.json is treated leniently: adopt REMOTE's when present, otherwise leave LOCAL's.
        CopyFileIfExists(Path.Combine(remote, ToOsPath(SeedRelPath)), Path.Combine(local, ToOsPath(SeedRelPath)));

        // 3. Overlay the preserved human guardrails onto the REMOTE task structure.
        foreach ((string resultRelPath, byte[] bytes) in preserved)
        {
            string target = Path.Combine(local, ToOsPath(resultRelPath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }

        // 4. Re-lock: the merged folder is the new BASE.
        BreakdownManifest.Capture(local).Write(local);
    }

    // --- matched task -----------------------------------------------------------------

    private static void MergeMatchedTask(
        string identity, TaskNode localTask, TaskNode remoteTask,
        BreakdownManifest baseManifest, BreakdownManifest localManifest, BreakdownManifest remoteManifest,
        string localDir, string remoteDir, List<GuardrailMergeItem> items)
    {
        Dictionary<string, string> localFiles = GuardrailRelPaths(localTask, localDir);
        Dictionary<string, string> remoteFiles = GuardrailRelPaths(remoteTask, remoteDir);

        IEnumerable<string> fileNames = localFiles.Keys
            .Union(remoteFiles.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (string fileName in fileNames)
        {
            localFiles.TryGetValue(fileName, out string? localRel);
            remoteFiles.TryGetValue(fileName, out string? remoteRel);

            string? baseHash = localRel is null ? null : baseManifest.Files.GetValueOrDefault(localRel);
            string? localHash = localRel is null ? null : localManifest.Files.GetValueOrDefault(localRel);
            string? remoteHash = remoteRel is null ? null : remoteManifest.Files.GetValueOrDefault(remoteRel);

            // The result always lands at the REMOTE task's path (the new folder name).
            string resultRel = $"{TasksDir}/{remoteTask.Id}/guardrails/{fileName}";

            (GuardrailMergeAction action, string reason) = Resolve(baseHash, localHash, remoteHash);

            items.Add(new GuardrailMergeItem
            {
                TaskIdentity = identity,
                GuardrailFile = fileName,
                Action = action,
                Reason = reason,
                LocalRelPath = action == GuardrailMergeAction.KeepLocal ? localRel : null,
                ResultRelPath = action == GuardrailMergeAction.Drop ? null : resultRel
            });
        }
    }

    /// <summary>The per-guardrail 3-way decision (SSOT §11.3), refined to guardrail granularity.</summary>
    private static (GuardrailMergeAction Action, string Reason) Resolve(string? baseHash, string? localHash, string? remoteHash)
    {
        bool inBase = baseHash is not null;
        bool inLocal = localHash is not null;
        bool inRemote = remoteHash is not null;

        if (!inLocal && inRemote)
        {
            // Not on disk locally (never added, or human-deleted) — accept the regeneration.
            return (GuardrailMergeAction.TakeRemote, inBase ? "regeneration reinstates it" : "new in regeneration");
        }

        if (inLocal && !inRemote)
        {
            if (!inBase)
            {
                return (GuardrailMergeAction.KeepLocal, "human-added; regeneration has none");
            }

            return localHash == baseHash
                ? (GuardrailMergeAction.Drop, "regeneration removed it; human had not edited it")
                : (GuardrailMergeAction.Conflict, "human edited it but regeneration removed it");
        }

        // inLocal && inRemote
        if (!inBase)
        {
            return localHash == remoteHash
                ? (GuardrailMergeAction.TakeRemote, "human-added matches regeneration")
                : (GuardrailMergeAction.Conflict, "human-added; regeneration produced a different version");
        }

        bool localChanged = localHash != baseHash;
        bool remoteChanged = remoteHash != baseHash;

        if (!localChanged)
        {
            return (GuardrailMergeAction.TakeRemote, remoteChanged ? "regenerated; human had not edited it" : "unchanged");
        }

        if (!remoteChanged)
        {
            return (GuardrailMergeAction.KeepLocal, "human edit preserved");
        }

        return localHash == remoteHash
            ? (GuardrailMergeAction.KeepLocal, "human edit matches regeneration")
            : (GuardrailMergeAction.Conflict, "human and regeneration both changed it");
    }

    // --- removed / added tasks --------------------------------------------------------

    private static void DropRemovedTask(
        string identity, TaskNode localTask,
        BreakdownManifest baseManifest, BreakdownManifest localManifest, string localDir,
        List<GuardrailMergeItem> items, List<string> warnings)
    {
        foreach ((string fileName, string localRel) in GuardrailRelPaths(localTask, localDir))
        {
            string? baseHash = baseManifest.Files.GetValueOrDefault(localRel);
            string? localHash = localManifest.Files.GetValueOrDefault(localRel);

            bool humanAdded = baseHash is null;
            bool humanEdited = baseHash is not null && localHash != baseHash;
            if (humanAdded || humanEdited)
            {
                string kind = humanAdded ? "human-added" : "human-edited";
                warnings.Add($"dropped {kind} guardrail {localRel} — its task ({identity}) was removed from the plan");
            }

            items.Add(new GuardrailMergeItem
            {
                TaskIdentity = identity,
                GuardrailFile = fileName,
                Action = GuardrailMergeAction.Drop,
                Reason = "task removed from plan",
                LocalRelPath = localRel,
                ResultRelPath = null
            });
        }
    }

    private static void AddNewTask(string identity, TaskNode remoteTask, string remoteDir, List<GuardrailMergeItem> items)
    {
        foreach ((string fileName, _) in GuardrailRelPaths(remoteTask, remoteDir))
        {
            items.Add(new GuardrailMergeItem
            {
                TaskIdentity = identity,
                GuardrailFile = fileName,
                Action = GuardrailMergeAction.TakeRemote,
                Reason = "new task",
                LocalRelPath = null,
                ResultRelPath = $"{TasksDir}/{remoteTask.Id}/guardrails/{fileName}"
            });
        }
    }

    // --- helpers ----------------------------------------------------------------------

    /// <summary>
    /// Index a plan's tasks by identity: <c>stableId</c> when declared, else
    /// <c>folder:&lt;name&gt;</c>. Throws on a duplicate identity (a duplicate <c>stableId</c> is
    /// otherwise reported by <c>validate</c>/GR2010 — the caller validates first).
    /// </summary>
    private static Dictionary<string, TaskNode> IndexByIdentity(PlanDefinition plan)
    {
        var byIdentity = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            string identity = task.StableId ?? $"folder:{task.Id}";
            if (!byIdentity.TryAdd(identity, task))
            {
                throw new InvalidOperationException(
                    $"Duplicate task identity '{identity}' in {plan.PlanDirectory}; run 'guardrails validate' (GR2010).");
            }
        }

        return byIdentity;
    }

    /// <summary>Map a task's guardrail filenames → plan-relative (forward-slash) paths.</summary>
    private static Dictionary<string, string> GuardrailRelPaths(TaskNode task, string planDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            string fileName = Path.GetFileName(guardrail.Path);
            string relPath = Path.GetRelativePath(planDir, guardrail.Path).Replace('\\', '/');
            map[fileName] = relPath;
        }

        return map;
    }

    private static string ToOsPath(string relPath) => relPath.Replace('/', Path.DirectorySeparatorChar);

    private static void CopyFileIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
