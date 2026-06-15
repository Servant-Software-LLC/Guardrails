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
