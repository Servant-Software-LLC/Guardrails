using Guardrails.Core.Io;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Core.Breakdown;

/// <summary>
/// The identity-aware regeneration merge (SSOT §11.3, issue #5). Given BASE (the baseline), LOCAL
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
    private const string GuardrailsSubdir = "guardrails";
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
                    baseManifest, localManifest, remoteManifest,
                    localPlan.PlanDirectory, remotePlan.PlanDirectory, items, warnings);
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

        WarnOnMachineOwnedOverwrite(baseManifest, localManifest, remoteManifest, warnings);
        WarnOnFolderFallbackIdentity(localPlan, remotePlan, warnings);

        items.Sort(static (a, b) =>
            string.CompareOrdinal(a.ResultRelPath ?? a.LocalRelPath, b.ResultRelPath ?? b.LocalRelPath));

        return new MergePlan { Items = items, Warnings = warnings };
    }

    /// <summary>
    /// Apply a conflict-free plan in place on <paramref name="localFolder"/>: replace the
    /// authored content (<c>tasks/</c>, <c>guardrails.json</c>, and <c>state/seed.json</c> when
    /// REMOTE has one) with REMOTE's, overlay the preserved human guardrails, and re-write the
    /// baseline so the merged folder becomes the new BASE. Harness-owned <c>state/</c> runtime and the
    /// generated <c>diagram.md</c> are left untouched. Throws if the plan still has conflicts.
    ///
    /// The new <c>tasks/</c> tree is fully assembled in a temp directory first (the long copy +
    /// overlay), and only swapped in once complete. So a failure mid-assembly — a partial REMOTE, a
    /// disk-full, an unreadable preserved file — leaves the existing folder untouched rather than
    /// half-deleted with a stale baseline.
    ///
    /// The staging directory is created OUTSIDE the local plan folder (beside REMOTE, falling back
    /// to the OS temp dir), never inside it: some Windows policies (Controlled Folder Access,
    /// integrity-level boundaries) deny <c>Directory.CreateDirectory</c> for a non-shell .NET
    /// process writing a new subdirectory under a protected non-system path, even when the user has
    /// Modify rights (issue #9). When the staging directory lands on the SAME volume as local (the
    /// normal case — REMOTE is a <c>*.staging</c> sibling of local) the swap stays two fast renames.
    /// When the fallback puts it on a DIFFERENT volume (e.g. <c>%TEMP%</c> on C: while the plan is on
    /// F:), the swap-in is a recursive copy instead, because <c>Directory.Move</c> cannot cross
    /// volumes; the backup-then-restore-on-failure guarantee holds either way.
    ///
    /// <para>After re-writing the baseline, when <paramref name="output"/> is supplied it prints a
    /// GitGuardian baseline-exclusion suggestion (issue #67) if the repo's scanner config doesn't
    /// already cover it. That is advisory only — read-only, never edits the config, never throws.</para>
    /// </summary>
    public static void Apply(MergePlan plan, string localFolder, string remoteFolder, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.HasConflicts)
        {
            throw new InvalidOperationException("Cannot apply a merge plan with unresolved conflicts.");
        }

        string local = Path.GetFullPath(localFolder);
        string remote = Path.GetFullPath(remoteFolder);

        // 1. Read preserved human guardrail bytes up front, while the local tasks tree is intact.
        var preserved = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (GuardrailMergeItem item in plan.Items.Where(i => i.Action == GuardrailMergeAction.KeepLocal))
        {
            string source = Path.Combine(local, ToOsPath(item.LocalRelPath!));
            preserved[item.ResultRelPath!] = File.ReadAllBytes(source);
        }

        // 2. Assemble the merged tasks tree off to the side — OUTSIDE local (issue #9): beside
        //    REMOTE first (same volume as local in the normal sibling-staging case), %TEMP% on fallback.
        string staged = CreateStagingDir(remote);
        try
        {
            CopyDirectory(Path.Combine(remote, TasksDir), staged);

            foreach ((string resultRelPath, byte[] bytes) in preserved)
            {
                // resultRelPath is "tasks/<id>/guardrails/<file>"; map it under the staged tree.
                string withinTasks = ToOsPath(resultRelPath[(TasksDir.Length + 1)..]);
                string target = Path.Combine(staged, withinTasks);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            // 3. Swap the staged tree in for the live one. The backup lives inside local (a same-volume
            //    rename of the existing tasks/ — never blocked, and keeps the old tree recoverable).
            string localTasks = Path.Combine(local, TasksDir);
            string backup = Path.Combine(local, $".merge-backup-{Guid.NewGuid():N}");
            if (Directory.Exists(localTasks))
            {
                Directory.Move(localTasks, backup);
            }

            try
            {
                SwapIn(staged, localTasks);
            }
            catch
            {
                // Restore the original tasks tree if the final swap fails.
                if (Directory.Exists(localTasks))
                {
                    SafeDelete.DeleteDirectory(localTasks);
                }
                if (Directory.Exists(backup) && !Directory.Exists(localTasks))
                {
                    Directory.Move(backup, localTasks);
                }
                throw;
            }

            if (Directory.Exists(backup))
            {
                SafeDelete.DeleteDirectory(backup);
            }
        }
        finally
        {
            // Best-effort cleanup of the staging dir if anything left it behind.
            if (Directory.Exists(staged))
            {
                try { SafeDelete.DeleteDirectory(staged); }
                catch (IOException) { /* leave it; the swap either succeeded or threw already */ }
            }
        }

        // 4. Adopt machine-owned plan files from REMOTE (config always; seed leniently when present).
        CopyFileIfExists(Path.Combine(remote, ConfigFileName), Path.Combine(local, ConfigFileName));
        CopyFileIfExists(Path.Combine(remote, ToOsPath(SeedRelPath)), Path.Combine(local, ToOsPath(SeedRelPath)));

        // 5. Re-write the baseline: the merged folder is the new BASE.
        BreakdownManifest.Capture(local).Write(local);

        // 6. Detect-and-suggest the scanner exclusion alongside the baseline write (issue #67):
        // read-only, advisory only — it never edits the user's .gitguardian.yaml and never throws.
        // No-op when there's no git repo or when an output sink wasn't provided.
        if (output is not null)
        {
            GitGuardianConfig.SuggestBaselineExclusion(local, output);
        }
    }

    // --- matched task -----------------------------------------------------------------

    private static void MergeMatchedTask(
        string identity, TaskNode localTask, TaskNode remoteTask,
        BreakdownManifest baseManifest, BreakdownManifest localManifest, BreakdownManifest remoteManifest,
        string localDir, string remoteDir, List<GuardrailMergeItem> items, List<string> warnings)
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

            // BASE is addressed by the generation-time path. Human CRUD never renames a task folder,
            // so the local folder name equals the folder name at the last generation — this lets us
            // find the base entry even for a file the human DELETED locally (localRel is null then).
            string baseRel = $"{TasksDir}/{localTask.Id}/{GuardrailsSubdir}/{fileName}";
            string? baseHash = baseManifest.Files.GetValueOrDefault(baseRel);
            string? localHash = localRel is null ? null : localManifest.Files.GetValueOrDefault(localRel);
            string? remoteHash = remoteRel is null ? null : remoteManifest.Files.GetValueOrDefault(remoteRel);

            // The result always lands at the REMOTE task's path (the new folder name).
            string resultRel = $"{TasksDir}/{remoteTask.Id}/{GuardrailsSubdir}/{fileName}";

            (GuardrailMergeAction action, string reason) = Resolve(baseHash, localHash, remoteHash);

            // Silent resurrection: the human deleted a guardrail but regeneration re-added it. The
            // plan wins (TakeRemote), but never quietly — the deletion is being undone.
            if (action == GuardrailMergeAction.TakeRemote && baseHash is not null && localHash is null)
            {
                warnings.Add(
                    $"reinstated guardrail {resultRel} — you deleted it, but regeneration re-added it");
            }

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
                ResultRelPath = $"{TasksDir}/{remoteTask.Id}/{GuardrailsSubdir}/{fileName}"
            });
        }
    }

    /// <summary>
    /// The plan-level files <c>guardrails.json</c> and <c>state/seed.json</c> are machine-owned
    /// (SSOT §11.3) — apply takes REMOTE's. That is contractual, but a human edit to one would be
    /// overwritten, and <c>lock --diff</c> would have shown it as EDITED. Warn (don't block) when a
    /// human-edited machine-owned file is about to be replaced by a differing REMOTE, so the loss is
    /// never silent. seed.json is lenient: apply only overwrites it when REMOTE actually has one.
    /// </summary>
    private static void WarnOnMachineOwnedOverwrite(
        BreakdownManifest baseManifest, BreakdownManifest localManifest, BreakdownManifest remoteManifest,
        List<string> warnings)
    {
        WarnIfOverwritingHumanEdit(ConfigFileName, requireRemote: false);
        WarnIfOverwritingHumanEdit(SeedRelPath, requireRemote: true);

        void WarnIfOverwritingHumanEdit(string relPath, bool requireRemote)
        {
            string? baseHash = baseManifest.Files.GetValueOrDefault(relPath);
            string? localHash = localManifest.Files.GetValueOrDefault(relPath);
            string? remoteHash = remoteManifest.Files.GetValueOrDefault(relPath);

            bool humanEdited = localHash is not null && baseHash is not null && localHash != baseHash;
            // requireRemote: apply only overwrites seed.json when REMOTE has one (lenient adopt).
            bool willOverwrite = requireRemote
                ? remoteHash is not null && remoteHash != localHash
                : remoteHash != localHash;

            if (humanEdited && willOverwrite)
            {
                warnings.Add(
                    $"machine-owned {relPath} was edited locally but will be overwritten by regeneration (§11.3)");
            }
        }
    }

    /// <summary>
    /// Surface, once, that some tasks have no <c>stableId</c> and are therefore matched by folder
    /// name. Such a task loses its human guardrail edits the moment regeneration renumbers or
    /// renames its folder (it reads as a drop + an add). Minting stableIds removes the risk.
    /// </summary>
    private static void WarnOnFolderFallbackIdentity(
        PlanDefinition localPlan, PlanDefinition remotePlan, List<string> warnings)
    {
        int localUnkeyed = localPlan.Tasks.Count(t => t.StableId is null);
        int remoteUnkeyed = remotePlan.Tasks.Count(t => t.StableId is null);
        if (localUnkeyed > 0 || remoteUnkeyed > 0)
        {
            warnings.Add(
                $"{localUnkeyed} local / {remoteUnkeyed} regenerated task(s) have no stableId and are matched by " +
                "folder name; edit preservation across renumbering is best-effort — mint stableIds for stable identity (§11.3)");
        }
    }

    // --- helpers ----------------------------------------------------------------------

    /// <summary>
    /// Index a plan's tasks by identity: <c>stableId</c> when declared, else
    /// <c>folder:&lt;name&gt;</c>. A duplicate is a defensive backstop only — the caller validates
    /// first, so a duplicate <c>stableId</c> would already have surfaced as GR2010, and a
    /// <c>folder:</c> identity cannot collide (folder names are unique on disk, and the
    /// <c>folder:</c> prefix is reserved against real stableIds by GR2011).
    /// </summary>
    private static Dictionary<string, TaskNode> IndexByIdentity(PlanDefinition plan)
    {
        var byIdentity = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            string identity = task.StableId ?? $"folder:{task.Id}";
            if (!byIdentity.TryAdd(identity, task))
            {
                string hint = identity.StartsWith("folder:", StringComparison.Ordinal)
                    ? "two task folders resolved to the same name"
                    : "duplicate stableId — run 'guardrails validate' (GR2010)";
                throw new InvalidOperationException(
                    $"Duplicate task identity '{identity}' in {plan.PlanDirectory}: {hint}.");
            }
        }

        return byIdentity;
    }

    /// <summary>
    /// Map every file under a task's <c>guardrails/</c> directory → its plan-relative (forward-slash)
    /// path, keyed by the file's path within <c>guardrails/</c>. This enumerates the directory on
    /// disk rather than <see cref="TaskNode.Guardrails"/> deliberately: a guardrail's metadata
    /// sidecar (<c>&lt;basename&gt;.json</c>, SSOT §4.1), its <c>.prompt.md</c>, and any human-added
    /// file are all human-owned content the merge must track — but none appear in the loaded
    /// guardrail list. Iterating the directory keeps them from being silently clobbered by apply.
    /// </summary>
    private static Dictionary<string, string> GuardrailRelPaths(TaskNode task, string planDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string guardrailsDir = Path.Combine(task.Directory, GuardrailsSubdir);
        if (!Directory.Exists(guardrailsDir))
        {
            return map;
        }

        foreach (string file in Directory.EnumerateFiles(guardrailsDir, "*", SearchOption.AllDirectories))
        {
            // Key by the path within guardrails/ so a file is matched across a task-folder rename.
            string key = Path.GetRelativePath(guardrailsDir, file).Replace('\\', '/');
            string relPath = Path.GetRelativePath(planDir, file).Replace('\\', '/');
            map[key] = relPath;
        }

        return map;
    }

    /// <summary>
    /// Create the temp staging directory OUTSIDE the local plan folder (issue #9). Primary location:
    /// beside the REMOTE folder — in the normal case REMOTE is a <c>*.staging</c> sibling of local, so
    /// this is the same volume as local and the final swap stays an atomic rename. Belt-and-suspenders:
    /// if creating it beside REMOTE is itself denied (the same CFA / integrity-level policy could
    /// protect that tree too) fall back to the OS temp dir. Returns the created (empty) directory path.
    /// </summary>
    internal static string CreateStagingDir(string remote)
    {
        string besideRemote = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(remote))!, $".merge-staged-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(besideRemote);
            return besideRemote;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string inTemp = Path.Combine(Path.GetTempPath(), $".merge-staged-{Guid.NewGuid():N}");
            Directory.CreateDirectory(inTemp);
            return inTemp;
        }
    }

    /// <summary>
    /// Move the assembled staging tree into place at <paramref name="localTasks"/>. Within one volume
    /// this is an atomic <see cref="Directory.Move(string, string)"/> rename; across volumes (the temp
    /// fallback can land staging on a different drive than the plan) <c>Directory.Move</c> throws, so
    /// the swap is a recursive copy instead. The caller owns the backup-then-restore-on-failure guard,
    /// so a mid-copy failure here still leaves the original tasks tree recoverable.
    /// </summary>
    internal static void SwapIn(string staged, string localTasks)
    {
        if (SameVolume(staged, localTasks))
        {
            Directory.Move(staged, localTasks);
        }
        else
        {
            CopyDirectory(staged, localTasks);
        }
    }

    /// <summary>
    /// True when two paths share a volume root, so <see cref="Directory.Move(string, string)"/> can
    /// rename between them. Roots are compared case-insensitively on Windows (drive letters), ordinally
    /// elsewhere. A null/empty root (relative path) is treated as a non-match to force the safe copy.
    /// </summary>
    internal static bool SameVolume(string a, string b)
    {
        string? rootA = Path.GetPathRoot(Path.GetFullPath(a));
        string? rootB = Path.GetPathRoot(Path.GetFullPath(b));
        if (string.IsNullOrEmpty(rootA) || string.IsNullOrEmpty(rootB))
        {
            return false;
        }

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(rootA, rootB, cmp);
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
