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

    /// <summary>A temp plan folder with stableId tasks + OS-appropriate guardrail scripts.</summary>
    private sealed class MergeDir : IDisposable
    {
        public string Dir { get; }

        public MergeDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "gr-mergecli-" + Guid.NewGuid().ToString("N"));
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
