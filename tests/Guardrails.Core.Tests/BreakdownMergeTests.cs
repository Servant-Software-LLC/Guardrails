using Guardrails.Core.Breakdown;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="BreakdownMerge"/> (SSOT §11.3) against real temp folders. Exercises
/// every per-guardrail resolution (take-remote / keep-local / drop / conflict), task matching by
/// <c>stableId</c> across a folder rename, added/removed tasks, and the in-place
/// <see cref="BreakdownMerge.Apply"/> (preserve, take-remote, drop, add, re-lock).
/// </summary>
public sealed class BreakdownMergeTests : IDisposable
{
    private readonly List<PlanFolder> _folders = [];

    private PlanFolder NewFolder()
    {
        var folder = new PlanFolder();
        _folders.Add(folder);
        return folder;
    }

    private static GuardrailMergeItem ItemFor(MergePlan plan, string file) =>
        plan.Items.Single(i => i.GuardrailFile == file);

    [Fact]
    public void Compute_HumanUntouched_RemoteChanged_TakesRemote()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();           // generation snapshot
        // human leaves it alone; regeneration changes it
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v2"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.TakeRemote, ItemFor(plan, "01-g.sh").Action);
        Assert.False(plan.HasConflicts);
    }

    [Fact]
    public void Compute_HumanEdited_RemoteUnchanged_KeepsLocal()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-g.sh", "human-edit"); // human edits after generation
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));    // regeneration produces the original

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "01-g.sh");
        Assert.Equal(GuardrailMergeAction.KeepLocal, item.Action);
        Assert.Equal("tasks/01-a/guardrails/01-g.sh", item.LocalRelPath);
    }

    [Fact]
    public void Compute_BothChanged_IsConflict()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-g.sh", "human-edit");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "regen-edit"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.True(plan.HasConflicts);
        Assert.Equal(GuardrailMergeAction.Conflict, ItemFor(plan, "01-g.sh").Action);
    }

    [Fact]
    public void Compute_HumanAdded_RemoteHasNone_KeepsLocal()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.AddGuardrail("01-a", "02-extra.sh", "human-added"); // not in BASE
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.KeepLocal, ItemFor(plan, "02-extra.sh").Action);
    }

    [Fact]
    public void Compute_RemoteRemoved_HumanUntouched_Drops()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"), ("02-h.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // 02-h.sh gone

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.Drop, ItemFor(plan, "02-h.sh").Action);
        Assert.Null(ItemFor(plan, "02-h.sh").ResultRelPath);
    }

    [Fact]
    public void Compute_RemoteRemoved_HumanEdited_IsConflict()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"), ("02-h.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "02-h.sh", "human-edit");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // 02-h.sh gone despite human edit

        MergePlan plan = Compute(baseM, local, remote);

        Assert.True(plan.HasConflicts);
        Assert.Equal(GuardrailMergeAction.Conflict, ItemFor(plan, "02-h.sh").Action);
    }

    [Fact]
    public void Compute_StableIdMatch_AcrossRename_CarriesHumanEditToNewPath()
    {
        // Same stableId, different folder name (renumbered) — the human edit must follow.
        PlanFolder local = NewFolder();
        local.AddTask("09-old", "sid-x", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("09-old", "01-g.sh", "human-edit");
        PlanFolder remote = NewFolder();
        remote.AddTask("05-new", "sid-x", ("01-g.sh", "v1"));

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "01-g.sh");
        Assert.Equal(GuardrailMergeAction.KeepLocal, item.Action);
        Assert.Equal("tasks/09-old/guardrails/01-g.sh", item.LocalRelPath);  // read from here
        Assert.Equal("tasks/05-new/guardrails/01-g.sh", item.ResultRelPath); // lands here
    }

    [Fact]
    public void Compute_AddedTask_TakesRemote()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        remote.AddTask("02-b", "sid-b", ("01-new.sh", "brand-new")); // new task

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "01-new.sh");
        Assert.Equal(GuardrailMergeAction.TakeRemote, item.Action);
        Assert.Equal("sid-b", item.TaskIdentity);
    }

    [Fact]
    public void Compute_RemovedTask_DropsAndWarnsOnHumanEdit()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.AddTask("02-b", "sid-b", ("01-h.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("02-b", "01-h.sh", "human-edit");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // task sid-b removed

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.Drop, ItemFor(plan, "01-h.sh").Action);
        Assert.Contains(plan.Warnings, w => w.Contains("sid-b") && w.Contains("human-edited"));
    }

    [Fact]
    public void Apply_PreservesEdits_TakesRemote_Drops_Adds_AndRelocks()
    {
        PlanFolder local = NewFolder();
        local.AddTask("09-old", "sid-x",
            ("01-keep.sh", "v1"),   // will be human-edited → preserved
            ("02-take.sh", "v1"),   // remote changes it → take remote
            ("03-drop.sh", "v1"));  // remote removes it → dropped
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("09-old", "01-keep.sh", "HUMAN");

        PlanFolder remote = NewFolder();
        remote.AddTask("05-new", "sid-x",
            ("01-keep.sh", "v1"),       // unchanged from base → human edit wins
            ("02-take.sh", "REGEN"));   // changed → take remote; 03-drop.sh omitted
        remote.AddTask("06-added", "sid-y", ("01-fresh.sh", "FRESH"));

        MergePlan plan = Compute(baseM, local, remote);
        Assert.False(plan.HasConflicts);

        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        // Result uses REMOTE's folder names.
        Assert.False(Directory.Exists(Path.Combine(local.Dir, "tasks", "09-old")));
        Assert.True(Directory.Exists(Path.Combine(local.Dir, "tasks", "05-new")));
        Assert.True(Directory.Exists(Path.Combine(local.Dir, "tasks", "06-added")));

        // Human edit preserved at the new path; remote update taken; dropped gone; added present.
        Assert.Equal("HUMAN", ReadGuardrail(local.Dir, "05-new", "01-keep.sh"));
        Assert.Equal("REGEN", ReadGuardrail(local.Dir, "05-new", "02-take.sh"));
        Assert.False(File.Exists(GuardrailPath(local.Dir, "05-new", "03-drop.sh")));
        Assert.Equal("FRESH", ReadGuardrail(local.Dir, "06-added", "01-fresh.sh"));

        // Re-locked: the merged folder is the new BASE with no drift.
        BreakdownManifest? relock = BreakdownManifest.Read(local.Dir);
        Assert.NotNull(relock);
        Assert.False(BreakdownDiff.Compute(relock!, BreakdownManifest.Capture(local.Dir)).HasDrift);
    }

    [Fact]
    public void Apply_WithConflicts_Throws()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-g.sh", "human-edit");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "regen-edit"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Throws<InvalidOperationException>(() => BreakdownMerge.Apply(plan, local.Dir, remote.Dir));
    }

    // --- A. Previously-uncovered Resolve branches -------------------------------------

    [Fact]
    public void Compute_HumanAdded_MatchesRegeneration_TakesRemote()
    {
        // Human added a guardrail not in BASE; regeneration emits the SAME content. localHash ==
        // remoteHash with no base ⇒ converged, the machine version wins (no point preserving a copy).
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.AddGuardrail("01-a", "02-extra.sh", "same-content"); // not in BASE
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"), ("02-extra.sh", "same-content"));

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "02-extra.sh");
        Assert.Equal(GuardrailMergeAction.TakeRemote, item.Action);
        Assert.Equal("human-added matches regeneration", item.Reason);
        Assert.False(plan.HasConflicts);
    }

    [Fact]
    public void Compute_HumanAdded_DiffersFromRegeneration_IsConflict()
    {
        // Human added a guardrail not in BASE; regeneration emits a same-named but DIFFERENT file.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.AddGuardrail("01-a", "02-extra.sh", "human-version"); // not in BASE
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"), ("02-extra.sh", "regen-version"));

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "02-extra.sh");
        Assert.Equal(GuardrailMergeAction.Conflict, item.Action);
        Assert.Equal("human-added; regeneration produced a different version", item.Reason);
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public void Compute_HumanDeleted_RegenerationReinstates_TakesRemote_AndWarns()
    {
        // BASE has the guardrail (so baseHash != null); the human DELETED it locally (localHash null);
        // regeneration re-emits it. The plan wins, but the deletion is being undone — so warn.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-keep.sh", "keep"), ("02-del.sh", "v1"));
        BreakdownManifest baseM = local.Capture();          // BASE records 02-del.sh
        local.DeleteGuardrail("01-a", "02-del.sh");          // human removes it locally
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-keep.sh", "keep"), ("02-del.sh", "v1")); // regen re-adds it

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "02-del.sh");
        Assert.Equal(GuardrailMergeAction.TakeRemote, item.Action);
        Assert.Equal("regeneration reinstates it", item.Reason);
        Assert.Contains(plan.Warnings, w => w.Contains("reinstated") && w.Contains("02-del.sh"));
        Assert.False(plan.HasConflicts);
    }

    [Fact]
    public void Compute_AllThreeIdentical_TakesRemote_Unchanged()
    {
        // BASE == LOCAL == REMOTE: nobody touched it. TakeRemote with the "unchanged" reason.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();          // local stays identical to base
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // remote identical too

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "01-g.sh");
        Assert.Equal(GuardrailMergeAction.TakeRemote, item.Action);
        Assert.Equal("unchanged", item.Reason);
    }

    [Fact]
    public void Compute_HumanEdit_MatchesRegenerationChange_KeepsLocal()
    {
        // Both LOCAL and REMOTE changed away from BASE but to the SAME value: keep local (it already
        // holds the converged content; no need to overwrite it with an identical remote copy).
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-g.sh", "converged");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "converged")); // same change as the human

        MergePlan plan = Compute(baseM, local, remote);

        GuardrailMergeItem item = ItemFor(plan, "01-g.sh");
        Assert.Equal(GuardrailMergeAction.KeepLocal, item.Action);
        Assert.Equal("human edit matches regeneration", item.Reason);
        Assert.Equal("tasks/01-a/guardrails/01-g.sh", item.LocalRelPath);
    }

    // --- B. Sidecar / arbitrary files under guardrails/ -------------------------------

    [Fact]
    public void Compute_SidecarEditedLocally_ScriptUnchanged_KeepsSidecar()
    {
        // A guardrail's metadata sidecar (<basename>.json) is human content the merge tracks even
        // though it never appears in the loaded guardrail list. Edited locally (script untouched),
        // it must be preserved while the script itself is taken from regeneration.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 30 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 99 }"); // human edits sidecar
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        remote.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 30 }"); // regen unchanged

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.TakeRemote, ItemFor(plan, "01-g.sh").Action);
        GuardrailMergeItem sidecar = ItemFor(plan, "01-g.json");
        Assert.Equal(GuardrailMergeAction.KeepLocal, sidecar.Action);
        Assert.Equal("tasks/01-a/guardrails/01-g.json", sidecar.LocalRelPath);
    }

    [Fact]
    public void Apply_PreservesEditedSidecarBytes()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 30 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 99 }");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        remote.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 30 }");

        MergePlan plan = Compute(baseM, local, remote);
        Assert.False(plan.HasConflicts);

        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        Assert.Equal("{ \"timeoutSeconds\": 99 }", ReadGuardrail(local.Dir, "01-a", "01-g.json"));
    }

    [Fact]
    public void Apply_KeepsHumanAddedArbitraryFile_UnderGuardrails()
    {
        // A human-added arbitrary file with no regeneration counterpart is human content: KeepLocal,
        // and present on disk after Apply.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.WriteGuardrailFile("01-a", "notes.txt", "human notes"); // not in BASE, not in remote
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.KeepLocal, ItemFor(plan, "notes.txt").Action);
        Assert.False(plan.HasConflicts);

        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);
        Assert.Equal("human notes", ReadGuardrail(local.Dir, "01-a", "notes.txt"));
    }

    [Fact]
    public void Compute_SidecarEditedBothSidesDifferently_IsConflict()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 30 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 99 }");  // human
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "script-body"));
        remote.WriteGuardrailFile("01-a", "01-g.json", "{ \"timeoutSeconds\": 60 }"); // regen, different

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Equal(GuardrailMergeAction.Conflict, ItemFor(plan, "01-g.json").Action);
        Assert.True(plan.HasConflicts);
    }

    // --- D. Machine-owned + folder-fallback warnings ----------------------------------

    [Fact]
    public void Compute_MachineOwnedConfigEditedLocally_RemoteDiffers_Warns_NoConflict()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.WriteConfig("{ \"version\": 1, \"maxParallelism\": 2 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteConfig("{ \"version\": 1, \"maxParallelism\": 7 }");  // human edits machine-owned config
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        remote.WriteConfig("{ \"version\": 1, \"maxParallelism\": 4 }"); // regen differs again

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Contains(plan.Warnings,
            w => w.Contains("guardrails.json") && (w.Contains("machine-owned") || w.Contains("§11.3")));
        Assert.False(plan.HasConflicts); // machine-owned overwrite is a warning, never a conflict
    }

    [Fact]
    public void Compute_SeedEditedLocally_RemoteDiffers_Warns()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.WriteSeed("{ \"seed\": 1 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteSeed("{ \"seed\": 2 }");   // human edits seed
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        remote.WriteSeed("{ \"seed\": 3 }");  // regen has a differing seed

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Contains(plan.Warnings,
            w => w.Contains("seed.json") && (w.Contains("machine-owned") || w.Contains("§11.3")));
    }

    [Fact]
    public void Compute_SeedEditedLocally_RemoteHasNoSeed_NoOverwriteWarning()
    {
        // Lenient adopt: seed.json is only overwritten when REMOTE actually has one. Local edit + no
        // remote seed ⇒ no machine-owned-overwrite warning for the seed.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.WriteSeed("{ \"seed\": 1 }");
        BreakdownManifest baseM = local.Capture();
        local.WriteSeed("{ \"seed\": 2 }");   // human edits seed
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // no seed at all

        MergePlan plan = Compute(baseM, local, remote);

        Assert.DoesNotContain(plan.Warnings, w => w.Contains("seed.json"));
    }

    [Fact]
    public void Compute_FolderFallbackIdentity_Warns()
    {
        // A task with no stableId is matched by folder name only — edit preservation across a rename
        // is best-effort, so surface a heads-up.
        PlanFolder local = NewFolder();
        local.AddUnkeyedTask("01-a", ("01-g.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        PlanFolder remote = NewFolder();
        remote.AddUnkeyedTask("01-a", ("01-g.sh", "v1"));

        MergePlan plan = Compute(baseM, local, remote);

        Assert.Contains(plan.Warnings,
            w => w.Contains("stableId") && w.Contains("folder name") && w.Contains("best-effort"));
    }

    // --- E. Apply robustness & completeness -------------------------------------------

    [Fact]
    public void Apply_LeavesNoStagingOrBackupDirectories()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-keep.sh", "HUMAN");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "REGEN"));

        MergePlan plan = Compute(baseM, local, remote);
        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        string[] residue = Directory.GetDirectories(local.Dir)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => n.StartsWith(".merge-staged-", StringComparison.Ordinal) ||
                        n.StartsWith(".merge-backup-", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(residue);

        // And the re-locked manifest has no drift against the merged folder.
        BreakdownManifest? relock = BreakdownManifest.Read(local.Dir);
        Assert.NotNull(relock);
        Assert.False(BreakdownDiff.Compute(relock!, BreakdownManifest.Capture(local.Dir)).HasDrift);
    }

    [Fact]
    public void Apply_AdoptsRemoteConfig_AndRemoteSeed_WhenPresent()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.WriteConfig("{ \"version\": 1, \"maxParallelism\": 2 }");
        local.WriteSeed("{ \"seed\": \"local\" }");
        BreakdownManifest baseM = local.Capture();
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        remote.WriteConfig("{ \"version\": 1, \"maxParallelism\": 9 }"); // changed config
        remote.WriteSeed("{ \"seed\": \"remote\" }");                    // present seed

        MergePlan plan = Compute(baseM, local, remote);
        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        Assert.Equal("{ \"version\": 1, \"maxParallelism\": 9 }",
            File.ReadAllText(Path.Combine(local.Dir, "guardrails.json")));
        Assert.Equal("{ \"seed\": \"remote\" }",
            File.ReadAllText(Path.Combine(local.Dir, "state", "seed.json")));
    }

    [Fact]
    public void Apply_LeavesLocalSeed_WhenRemoteHasNone()
    {
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-g.sh", "v1"));
        local.WriteSeed("{ \"seed\": \"local\" }");
        BreakdownManifest baseM = local.Capture();
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-g.sh", "v1")); // no seed

        MergePlan plan = Compute(baseM, local, remote);
        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        Assert.Equal("{ \"seed\": \"local\" }",
            File.ReadAllText(Path.Combine(local.Dir, "state", "seed.json")));
    }

    // --- F. Staging location & swap (issue #9) ----------------------------------------

    [Fact]
    public void Apply_DoesNotCreateStagingInsideLocalFolder()
    {
        // Regression for issue #9: the temp staging dir must NOT be created inside the local plan
        // folder. On machines with Controlled Folder Access / integrity-level boundaries,
        // Directory.CreateDirectory of a new ".merge-staged-*" subdir under a protected non-system
        // path throws UnauthorizedAccessException even with Modify rights. We can't reproduce CFA in
        // a unit test, so we assert the contract that prevents it: no ".merge-staged-*" ever appears
        // inside local (it lives beside the remote / under %TEMP% instead), neither during a snapshot
        // nor as residue afterward.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-keep.sh", "HUMAN");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "REGEN"));

        MergePlan plan = Compute(baseM, local, remote);
        BreakdownMerge.Apply(plan, local.Dir, remote.Dir);

        bool stagingInsideLocal = Directory.GetDirectories(local.Dir, ".merge-staged-*").Length > 0;
        Assert.False(stagingInsideLocal, "no .merge-staged-* directory may be created inside the local plan folder");

        // Apply still succeeded and produced correct content.
        Assert.Equal("HUMAN", ReadGuardrail(local.Dir, "01-a", "01-keep.sh"));
        Assert.Equal("REGEN", ReadGuardrail(local.Dir, "01-a", "02-take.sh"));
    }

    [Fact]
    public void CreateStagingDir_PlacesStagingBesideRemote_NotInsideLocal()
    {
        // The primary staging location is beside the REMOTE folder, so in the normal sibling-staging
        // case it is on the same volume as local (atomic-rename swap) yet outside local (issue #9).
        PlanFolder remote = NewFolder();

        string staged = BreakdownMerge.CreateStagingDir(remote.Dir);
        try
        {
            Assert.True(Directory.Exists(staged));
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(remote.Dir)),
                Path.GetDirectoryName(Path.GetFullPath(staged)));
            Assert.StartsWith(".merge-staged-", Path.GetFileName(staged), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(staged, recursive: true);
        }
    }

    [Fact]
    public void CreateStagingDir_FallsBackToTemp_WhenBesideRemoteIsDenied()
    {
        // Belt-and-suspenders fallback: if creating the staged dir beside REMOTE itself throws
        // (the same CFA / integrity-level policy could protect that tree too), retry under %TEMP%.
        // Forcing CreateDirectory to fail deterministically requires a read-only parent, which is
        // only reliable via the Unix permission bits; on Windows a non-elevated process can still
        // create children under a "read-only" directory, so this path is covered by the unit test on
        // POSIX and by the CLI-level coverage elsewhere. The fallback code itself is OS-agnostic.
        if (OperatingSystem.IsWindows())
        {
            return; // documented limitation: cannot deterministically deny CreateDirectory on Windows here
        }

        string parent = Path.Combine(Path.GetTempPath(), "gr-ro-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string remote = Path.Combine(parent, "remote");
        Directory.CreateDirectory(remote);
        // Make the parent read+execute only: creating a NEW child (the ".merge-staged-*" sibling of
        // remote) is denied, so CreateStagingDir must fall back to %TEMP%.
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        string staged;
        try
        {
            staged = BreakdownMerge.CreateStagingDir(remote);
        }
        finally
        {
            File.SetUnixFileMode(parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(parent, recursive: true);
        }

        try
        {
            Assert.True(Directory.Exists(staged));
            // Fell back under the OS temp dir, NOT beside the (denied) remote parent.
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(staged),
                StringComparison.Ordinal);
            Assert.NotEqual(parent, Path.GetDirectoryName(Path.GetFullPath(staged)));
        }
        finally
        {
            Directory.Delete(staged, recursive: true);
        }
    }

    [Fact]
    public void SameVolume_TrueForSameRoot_FalseForDifferentRoot()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        Assert.True(BreakdownMerge.SameVolume(Path.Combine(root, "a"), Path.Combine(root, "b", "c")));

        if (OperatingSystem.IsWindows())
        {
            // Two distinct drive letters are distinct volumes — forces the cross-volume copy in SwapIn.
            Assert.False(BreakdownMerge.SameVolume(@"C:\a", @"D:\b"));
            // Drive-letter casing must not register as a different volume (Windows is case-insensitive).
            Assert.True(BreakdownMerge.SameVolume(@"c:\a", @"C:\b"));
        }
    }

    [Fact]
    public void SwapIn_SameVolume_RenamesStagedTreeIntoPlace()
    {
        // Same-volume happy path: SwapIn renames the assembled tree to the destination (no copy).
        string baseDir = Path.Combine(Path.GetTempPath(), "gr-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            string staged = Path.Combine(baseDir, "staged");
            Directory.CreateDirectory(Path.Combine(staged, "sub"));
            File.WriteAllText(Path.Combine(staged, "sub", "f.txt"), "payload");
            string dest = Path.Combine(baseDir, "tasks");

            BreakdownMerge.SwapIn(staged, dest);

            Assert.False(Directory.Exists(staged));                 // moved, not copied
            Assert.Equal("payload", File.ReadAllText(Path.Combine(dest, "sub", "f.txt")));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_PreservedSourceMissing_ThrowsAndLeavesFolderUnchanged()
    {
        // Rollback-on-failure: a preserved guardrail whose on-disk source vanishes makes Apply throw.
        // Because preserved bytes are read up front (before any mutation), the whole local folder —
        // tasks tree and lock — must be left exactly as it was.
        PlanFolder local = NewFolder();
        local.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "v1"));
        BreakdownManifest baseM = local.Capture();
        local.EditGuardrail("01-a", "01-keep.sh", "HUMAN");
        PlanFolder remote = NewFolder();
        remote.AddTask("01-a", "sid-a", ("01-keep.sh", "v1"), ("02-take.sh", "REGEN"));

        MergePlan plan = Compute(baseM, local, remote);
        // Yank the preserved source out from under Apply just before it runs.
        local.DeleteGuardrail("01-a", "01-keep.sh");

        Assert.ThrowsAny<IOException>(() => BreakdownMerge.Apply(plan, local.Dir, remote.Dir));

        // The live tasks tree is untouched: 02-take.sh still holds the LOCAL "v1", not "REGEN", and
        // 01-keep.sh is still absent exactly as we left it (no half-applied REMOTE tree).
        Assert.Equal("v1", ReadGuardrail(local.Dir, "01-a", "02-take.sh"));
        Assert.False(File.Exists(GuardrailPath(local.Dir, "01-a", "01-keep.sh")));
        // No staging/backup residue inside local.
        Assert.Empty(Directory.GetDirectories(local.Dir, ".merge-staged-*"));
        Assert.Empty(Directory.GetDirectories(local.Dir, ".merge-backup-*"));
    }

    private static MergePlan Compute(BreakdownManifest baseM, PlanFolder local, PlanFolder remote) =>
        BreakdownMerge.Compute(
            baseM,
            local.Load(), local.Capture(),
            remote.Load(), remote.Capture());

    private static string GuardrailPath(string planDir, string task, string file) =>
        Path.Combine(planDir, "tasks", task, "guardrails", file);

    private static string ReadGuardrail(string planDir, string task, string file) =>
        File.ReadAllText(GuardrailPath(planDir, task, file));

    public void Dispose()
    {
        foreach (PlanFolder folder in _folders)
        {
            folder.Dispose();
        }
    }

    /// <summary>A real temp plan folder with stableId-bearing tasks, for driving the merge.</summary>
    private sealed class PlanFolder : IDisposable
    {
        public string Dir { get; }

        public PlanFolder()
        {
            Dir = Path.Combine(Path.GetTempPath(), "gr-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "guardrails.json"), "{ \"version\": 1 }");
            Directory.CreateDirectory(Path.Combine(Dir, "tasks"));
        }

        public void AddTask(string folder, string stableId, params (string File, string Content)[] guardrails)
        {
            string taskDir = Path.Combine(Dir, "tasks", folder);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{ \"description\": \"{folder}\", \"stableId\": \"{stableId}\", \"dependsOn\": [] }}");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "echo run\n");
            foreach ((string file, string content) in guardrails)
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", file), content);
            }
        }

        public void EditGuardrail(string folder, string file, string content) =>
            File.WriteAllText(Path.Combine(Dir, "tasks", folder, "guardrails", file), content);

        public void AddGuardrail(string folder, string file, string content) =>
            File.WriteAllText(Path.Combine(Dir, "tasks", folder, "guardrails", file), content);

        /// <summary>Delete a guardrail file from a task's <c>guardrails/</c> directory (human delete).</summary>
        public void DeleteGuardrail(string folder, string file) =>
            File.Delete(Path.Combine(Dir, "tasks", folder, "guardrails", file));

        /// <summary>
        /// Write an arbitrary file under a task's <c>guardrails/</c> directory — a metadata sidecar
        /// (<c>&lt;basename&gt;.json</c>) or any human-added file. Used identically for add and edit.
        /// </summary>
        public void WriteGuardrailFile(string folder, string file, string content) =>
            File.WriteAllText(Path.Combine(Dir, "tasks", folder, "guardrails", file), content);

        /// <summary>Edit the machine-owned <c>guardrails.json</c> at the plan root.</summary>
        public void WriteConfig(string content) =>
            File.WriteAllText(Path.Combine(Dir, "guardrails.json"), content);

        /// <summary>Write the committed <c>state/seed.json</c> (machine-owned authored content, §11.3).</summary>
        public void WriteSeed(string content)
        {
            string stateDir = Path.Combine(Dir, "state");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "seed.json"), content);
        }

        /// <summary>
        /// Add a task with NO declared stableId — identity falls back to <c>folder:&lt;name&gt;</c>.
        /// Mirrors <see cref="AddTask"/> but omits the stableId field from task.json.
        /// </summary>
        public void AddUnkeyedTask(string folder, params (string File, string Content)[] guardrails)
        {
            string taskDir = Path.Combine(Dir, "tasks", folder);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{ \"description\": \"{folder}\", \"dependsOn\": [] }}");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "echo run\n");
            foreach ((string file, string content) in guardrails)
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", file), content);
            }
        }

        public BreakdownManifest Capture() => BreakdownManifest.Capture(Dir);

        public PlanDefinition Load() => new PlanLoader().Load(Dir).Plan
            ?? throw new InvalidOperationException($"plan failed to load: {Dir}");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                {
                    Directory.Delete(Dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
