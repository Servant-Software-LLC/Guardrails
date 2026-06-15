using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Core.Breakdown;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails merge</c> through the REAL composition root
/// (<see cref="CommandFactory.BuildRootCommand"/>) against temp plan folders (SSOT §11.3):
/// dry-run/apply, the conflict block, missing/corrupt lock, and a missing remote. Asserts on
/// exit codes and on-disk content (a preserved human edit) via <see cref="StringConsoleIo"/>, so
/// it stays parallel-safe and also proves the command is actually wired into the factory.
/// </summary>
public sealed class MergeCliTests
{
    /// <summary>Exit code merge returns for an actionable "human must act" outcome (conflicts / missing lock).</summary>
    private const int ActionNeededExitCode = 2;

    // OS-appropriate scripts so the plan validates on every runner (mirrors ScriptPlanBuilder).
    private static string Ext => OperatingSystem.IsWindows() ? ".ps1" : ".sh";

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task Merge_DryRun_NoConflicts_ExitsZero()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        local.Lock();
        local.EditGuardrail("01-a", "01-g", "human-edit");

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Dry run", output);
        // Dry run writes nothing: the human edit is still on disk, untouched by an apply.
        Assert.Equal("human-edit", local.ReadGuardrail("01-a", "01-g"));
    }

    [Fact]
    public async Task Merge_WithConflicts_ExitsActionNeeded_AndBlocks()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        local.Lock();
        local.EditGuardrail("01-a", "01-g", "human-edit");

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "regen-edit"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir, "--apply");

        Assert.Equal(ActionNeededExitCode, exit);
        Assert.Contains("CONFLICT", output);
        Assert.Contains("Blocked", output);
        // --apply must NOT mutate when blocked.
        Assert.Equal("human-edit", local.ReadGuardrail("01-a", "01-g"));
    }

    [Fact]
    public async Task Merge_Apply_PreservesHumanEdit_AcrossRename_AndRelocks()
    {
        using var local = new MergeDir();
        local.AddTask("09-old", "sid-x", ("01-g", "v1"));
        local.Lock();
        local.EditGuardrail("09-old", "01-g", "HUMAN");

        using var remote = new MergeDir();
        remote.AddTask("05-new", "sid-x", ("01-g", "v1")); // renumbered, same stableId, unchanged content

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir, "--apply");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Applied", output);
        // The human edit followed the task to its new folder name.
        Assert.False(Directory.Exists(Path.Combine(local.Dir, "tasks", "09-old")));
        Assert.Equal("HUMAN", local.ReadGuardrail("05-new", "01-g"));
        // Re-locked clean.
        (int checkExit, _) = await InvokeAsync("lock", local.Dir, "--check");
        Assert.Equal(ExitCodes.Success, checkExit);
    }

    [Fact]
    public async Task Merge_Apply_SiblingStagingRemote_LeavesNoStagingInsideLocal()
    {
        // Models the exact issue #9 layout: REMOTE is a sibling "<local>.staging" folder on the same
        // volume as local. Apply must succeed and must NOT leave (or have created) any ".merge-staged-*"
        // directory inside the local plan folder — the staging tree is assembled beside REMOTE instead.
        string parent = Path.Combine(Path.GetTempPath(), "gr-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using var local = new MergeDir(Path.Combine(parent, "plan"));
            local.AddTask("01-a", "sid-a", ("01-g", "v1"));
            local.Lock();
            local.EditGuardrail("01-a", "01-g", "HUMAN");

            using var remote = new MergeDir(Path.Combine(parent, "plan.staging"));
            remote.AddTask("01-a", "sid-a", ("01-g", "v1")); // unchanged → human edit preserved

            (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir, "--apply");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("Applied", output);
            Assert.Equal("HUMAN", local.ReadGuardrail("01-a", "01-g"));
            // The smoking gun for issue #9: nothing named ".merge-staged-*" inside local.
            Assert.Empty(Directory.GetDirectories(local.Dir, ".merge-staged-*"));
            Assert.Empty(Directory.GetDirectories(local.Dir, ".merge-backup-*"));
            // Re-locked clean.
            (int checkExit, _) = await InvokeAsync("lock", local.Dir, "--check");
            Assert.Equal(ExitCodes.Success, checkExit);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Merge_MissingLock_ExitsActionNeeded()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1")); // no Lock() → no BASE

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ActionNeededExitCode, exit);
        Assert.Contains("missing", output);
    }

    [Fact]
    public async Task Merge_CorruptLock_ExitsHarnessError()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        await File.WriteAllTextAsync(Path.Combine(local.Dir, BreakdownManifest.FileName), "{ not json",
            TestContext.Current.CancellationToken);

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("corrupt", output);
    }

    [Fact]
    public async Task Merge_MissingRemote_ExitsHarnessError()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        local.Lock();

        string missingRemote = Path.Combine(Path.GetTempPath(), "no-remote-" + Guid.NewGuid().ToString("N"));

        (int exit, _) = await InvokeAsync("merge", local.Dir, "--remote", missingRemote);

        Assert.Equal(ExitCodes.HarnessError, exit);
    }

    [Fact]
    public async Task Merge_MissingLocalFolder_ExitsHarnessError()
    {
        string missingLocal = Path.Combine(Path.GetTempPath(), "no-local-" + Guid.NewGuid().ToString("N"));
        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", missingLocal, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("does not exist", output);
    }

    [Fact]
    public async Task Merge_InvalidLocalPlan_ExitsHarnessError()
    {
        // A duplicate stableId is a validation error (GR2010) on the LOCAL side — merge must refuse.
        using var local = new MergeDir();
        local.AddTask("01-a", "dup", ("01-g", "v1"));
        local.AddTask("02-b", "dup", ("01-h", "v1")); // duplicate stableId
        local.Lock();

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("current", output);
        Assert.Contains("not valid", output);
    }

    [Fact]
    public async Task Merge_InvalidRemotePlan_ExitsHarnessError()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        local.Lock();

        using var remote = new MergeDir();
        remote.AddTask("01-a", "dup", ("01-g", "v1"));
        remote.AddTask("02-b", "dup", ("01-h", "v1")); // duplicate stableId on the remote side

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("remote", output);
        Assert.Contains("not valid", output);
    }

    [Fact]
    public async Task Merge_Apply_PrintsNextHint()
    {
        using var local = new MergeDir();
        local.AddTask("01-a", "sid-a", ("01-g", "v1"));
        local.Lock();
        local.EditGuardrail("01-a", "01-g", "HUMAN");

        using var remote = new MergeDir();
        remote.AddTask("01-a", "sid-a", ("01-g", "v1"));

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir, "--apply");

        Assert.Equal(ExitCodes.Success, exit);
        // The applied run nudges the human to re-validate and refresh the diagram.
        Assert.Contains("Next:", output);
        Assert.Contains("validate", output);
        Assert.Contains("graph", output);
    }

    [Fact]
    public async Task Merge_FolderFallbackTask_RendersIdentityWithFolderTag()
    {
        // An unkeyed task is matched by folder name. When dropped, its DROP line must render the
        // identity as "<name> (folder)" — never the raw "folder:<name>" synthetic key.
        using var local = new MergeDir();
        local.AddTask("01-keep", "sid-a", ("01-g", "v1"));
        local.AddUnkeyedTask("02-orphan", ("01-h", "v1")); // no stableId
        local.Lock();

        using var remote = new MergeDir();
        remote.AddTask("01-keep", "sid-a", ("01-g", "v1")); // 02-orphan removed from the plan

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("02-orphan (folder)/01-h" + Ext, output);
        Assert.DoesNotContain("folder:02-orphan", output);
    }

    [Fact]
    public async Task Merge_Report_ItemsAreOrdinalOrdered()
    {
        // Multiple human edits across tasks/files: the KEEP report lines must appear in deterministic
        // ordinal order by result path (tasks/<id>/guardrails/<file>). Tasks are ADDED in the reverse
        // of sorted order (02-b first, files 02-second first) so the final ordinal sort is load-bearing
        // — an insertion-order report would fail.
        using var local = new MergeDir();
        local.AddTask("02-b", "sid-b", ("01-only", "v1"));
        local.AddTask("01-a", "sid-a", ("02-second", "v1"), ("01-first", "v1"));
        local.Lock();
        local.EditGuardrail("01-a", "02-second", "H2");
        local.EditGuardrail("01-a", "01-first", "H1");
        local.EditGuardrail("02-b", "01-only", "H3");

        using var remote = new MergeDir();
        remote.AddTask("02-b", "sid-b", ("01-only", "v1"));
        remote.AddTask("01-a", "sid-a", ("02-second", "v1"), ("01-first", "v1")); // remote unchanged → keep local

        (int exit, string output) = await InvokeAsync("merge", local.Dir, "--remote", remote.Dir);

        Assert.Equal(ExitCodes.Success, exit);
        // Report lines are labelled by stableId identity, but ORDERED by result path
        // (tasks/<folder>/guardrails/<file>): tasks/01-a/.../01-first < .../02-second < tasks/02-b/.../01-only.
        int firstA = output.IndexOf("sid-a/01-first" + Ext, StringComparison.Ordinal);
        int secondA = output.IndexOf("sid-a/02-second" + Ext, StringComparison.Ordinal);
        int onlyB = output.IndexOf("sid-b/01-only" + Ext, StringComparison.Ordinal);
        Assert.True(firstA >= 0 && secondA >= 0 && onlyB >= 0, $"missing KEEP lines in:\n{output}");
        Assert.True(firstA < secondA, $"01-first should precede 02-second:\n{output}");
        Assert.True(secondA < onlyB, $"01-a items should precede 02-b items:\n{output}");
    }

    /// <summary>A temp plan folder with stableId tasks + OS-appropriate guardrail scripts.</summary>
    private sealed class MergeDir : IDisposable
    {
        public string Dir { get; }

        public MergeDir()
            : this(Path.Combine(Path.GetTempPath(), "gr-mergecli-" + Guid.NewGuid().ToString("N")))
        {
        }

        /// <summary>Build the plan folder at an explicit path (used to model a sibling <c>*.staging</c> remote).</summary>
        public MergeDir(string dir)
        {
            Dir = dir;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "guardrails.json"),
                "{ \"version\": 1, \"defaultRetries\": 0, \"maxParallelism\": 1 }");
            Directory.CreateDirectory(Path.Combine(Dir, "tasks"));
        }

        public void AddTask(string folder, string stableId, params (string Name, string Content)[] guardrails)
        {
            string taskDir = Path.Combine(Dir, "tasks", folder);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{ \"description\": \"{folder}\", \"stableId\": \"{stableId}\", \"dependsOn\": [] }}");
            Write(Path.Combine(taskDir, "action" + Ext), "echo run\n");
            foreach ((string name, string content) in guardrails)
            {
                Write(Path.Combine(taskDir, "guardrails", name + Ext), content);
            }
        }

        /// <summary>Add a task with NO declared stableId — identity falls back to <c>folder:&lt;name&gt;</c>.</summary>
        public void AddUnkeyedTask(string folder, params (string Name, string Content)[] guardrails)
        {
            string taskDir = Path.Combine(Dir, "tasks", folder);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $"{{ \"description\": \"{folder}\", \"dependsOn\": [] }}");
            Write(Path.Combine(taskDir, "action" + Ext), "echo run\n");
            foreach ((string name, string content) in guardrails)
            {
                Write(Path.Combine(taskDir, "guardrails", name + Ext), content);
            }
        }

        public void Lock() => BreakdownManifest.Capture(Dir).Write(Dir);

        public void EditGuardrail(string folder, string name, string content) =>
            Write(Path.Combine(Dir, "tasks", folder, "guardrails", name + Ext), content);

        public string ReadGuardrail(string folder, string name) =>
            File.ReadAllText(Path.Combine(Dir, "tasks", folder, "guardrails", name + Ext));

        private static void Write(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

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
