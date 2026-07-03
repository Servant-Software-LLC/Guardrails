using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Verifies the #199/#192 worktree-containment PreToolUse hook: the generated settings JSON shape
/// (unit, no process spawn) and — running the REAL generated script standalone with synthetic
/// PreToolUse stdin JSON (no `claude` binary needed, per the issue's testability ask) — the actual
/// block/allow decisions for Write/Edit/NotebookEdit paths (including `..`-relative escapes) and
/// write-ish/stash Bash commands. Both scripts are pure string-based `.`/`..` normalization with NO
/// symlink resolution (a known, consistent gap across platforms — see
/// <see cref="Symlink_TargetEscapesWorktree_NotDetected_KnownConsistentGap"/>). The script is
/// spawned via the SAME <see cref="InterpreterMap"/> + <see cref="ProcessRunner"/> the harness uses
/// for any script action — no bespoke process-launch code under test.
/// </summary>
public sealed class WorktreeContainmentHookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-wch-" + Guid.NewGuid().ToString("N"));
    private readonly string _worktree;
    private readonly string _logDir;

    public WorktreeContainmentHookTests()
    {
        _worktree = Path.Combine(_root, "worktree");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(Path.Combine(_worktree, "sub"));
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // --- generation shape (no process spawn) ------------------------------------------------

    [Fact]
    public void WriteHookFiles_WritesScriptAndSettings_SettingsPointsAtScript()
    {
        string settingsPath = WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        Assert.True(File.Exists(settingsPath));
        string scriptPath = Path.Combine(_logDir,
            OperatingSystem.IsWindows() ? WorktreeContainmentHook.ScriptFileNameWindows : WorktreeContainmentHook.ScriptFileNameUnix);
        Assert.True(File.Exists(scriptPath));

        string settingsJson = File.ReadAllText(settingsPath);
        Assert.Contains("\"PreToolUse\"", settingsJson);
        Assert.Contains("\"matcher\"", settingsJson);
        Assert.Contains(WorktreeContainmentHook.Matcher, settingsJson);
        Assert.Contains("\"type\"", settingsJson);
        Assert.Contains("\"command\"", settingsJson);
        // The generated command line must reference the actual script path we just wrote.
        Assert.Contains(scriptPath.Replace("\\", "\\\\"), settingsJson);
    }

    [Fact]
    public void WriteHookFiles_UnixScript_IsMarkedExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file-mode bits are not meaningful on Windows.
        }

        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string scriptPath = Path.Combine(_logDir, WorktreeContainmentHook.ScriptFileNameUnix);

        UnixFileMode mode = File.GetUnixFileMode(scriptPath);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Matcher_CoversAllWriteFamilyToolsAndBash()
    {
        Assert.Contains("Write", WorktreeContainmentHook.Matcher);
        Assert.Contains("Edit", WorktreeContainmentHook.Matcher);
        Assert.Contains("MultiEdit", WorktreeContainmentHook.Matcher);
        Assert.Contains("NotebookEdit", WorktreeContainmentHook.Matcher);
        Assert.Contains("Bash", WorktreeContainmentHook.Matcher);
    }

    // --- real script execution (standalone, synthetic stdin, no `claude` binary) -----------

    private async Task<(int ExitCode, string StandardError)> RunHookAsync(string toolCallJson)
    {
        string scriptPath = Path.Combine(_logDir,
            OperatingSystem.IsWindows() ? WorktreeContainmentHook.ScriptFileNameWindows : WorktreeContainmentHook.ScriptFileNameUnix);

        var interpreterMap = new InterpreterMap(new PathExecutableProbe());
        InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, []);
        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);

        var processRunner = new ProcessRunner();
        ProcessResult result = await processRunner.RunAsync(
            resolution.Command!,
            _worktree,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            standardInput: toolCallJson,
            stdoutLineSink: null,
            TestContext.Current.CancellationToken);

        return (result.ExitCode, result.StandardError);
    }

    private static string ToolCall(string toolName, string inputJson) =>
        $$"""{"tool_name":"{{toolName}}","tool_input":{{inputJson}}}""";

    [Fact]
    public async Task Write_InsideWorktree_Allowed()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string path = Path.Combine(_worktree, "sub", "file.txt").Replace("\\", "\\\\");

        (int exitCode, _) = await RunHookAsync(ToolCall("Write", $$"""{"file_path":"{{path}}","content":"x"}"""));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Write_AbsolutePathOutsideWorktree_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string outside = Path.Combine(_root, "outside.txt").Replace("\\", "\\\\");

        (int exitCode, string stderr) = await RunHookAsync(ToolCall("Write", $$"""{"file_path":"{{outside}}","content":"x"}"""));

        Assert.Equal(2, exitCode);
        Assert.Contains("BLOCKED", stderr, StringComparison.Ordinal);
        Assert.Contains("outside", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_RelativePath_InsideWorktree_Allowed()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Edit", """{"file_path":"sub/file.txt"}"""));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Edit_DotDotRelativePath_Escapes_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, string stderr) = await RunHookAsync(ToolCall("Edit", """{"file_path":"../../escape.txt"}"""));

        Assert.Equal(2, exitCode);
        Assert.Contains("BLOCKED", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotebookEdit_OutsideWorktree_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string outside = Path.Combine(_root, "nb.ipynb").Replace("\\", "\\\\");

        (int exitCode, _) = await RunHookAsync(ToolCall("NotebookEdit", $$"""{"notebook_path":"{{outside}}"}"""));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Symlink_TargetEscapesWorktree_NotDetected_KnownConsistentGap()
    {
        // Symlink-escape detection was REMOVED from the bash script (it depended on `realpath -m`,
        // a GNU-coreutils-only flag; macOS ships BSD `realpath`, which silently misbehaved under it
        // and let 13 escape-detection tests down on macOS-only CI). Both scripts are now a PURE,
        // dependency-free string-based '.'/'..' normalization -- the literal path text
        // "<worktree>/linked/escaped.txt" is textually inside the worktree, so it is ALLOWED even
        // though "linked" is a symlink whose target lives outside. This is a known, ACCEPTED gap,
        // consistent across both platforms (neither resolves symlinks) -- not a macOS-only
        // regression. The `..`-escape and absolute-path-escape tests above cover the primary,
        // supported escape classes (#199 was written against accidental/careless escapes, not a
        // symlink-based adversarial bypass); see the class doc comment and SSOT §9.4.
        if (OperatingSystem.IsWindows())
        {
            // Creating a real symlink on Windows requires elevation in CI; the behavior is identical
            // on Windows anyway (PowerShell never resolved symlinks), so there is nothing extra to
            // demonstrate there.
            return;
        }

        string outsideDir = Path.Combine(_root, "outside-real");
        Directory.CreateDirectory(outsideDir);
        string linkPath = Path.Combine(_worktree, "linked");
        Directory.CreateSymbolicLink(linkPath, outsideDir);

        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string viaLink = Path.Combine(_worktree, "linked", "escaped.txt").Replace("\\", "\\\\");

        (int exitCode, _) = await RunHookAsync(ToolCall("Write", $$"""{"file_path":"{{viaLink}}","content":"x"}"""));

        // Documents the current, honest behavior -- NOT a desired security property.
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Bash_PlainCommand_Allowed()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", """{"command":"git status"}"""));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Bash_RedirectInsideWorktree_Allowed()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", """{"command":"echo hi > sub/out.txt"}"""));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Bash_RedirectOutsideWorktree_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string outside = Path.Combine(_root, "out.txt").Replace("\\", "\\\\");

        (int exitCode, string stderr) = await RunHookAsync(ToolCall("Bash", $$"""{"command":"echo hi > {{outside}}"}"""));

        Assert.Equal(2, exitCode);
        Assert.Contains("BLOCKED", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bash_CpToOutsidePath_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string outside = Path.Combine(_root, "copy.txt").Replace("\\", "\\\\");

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", $$"""{"command":"cp sub/file.txt {{outside}}"}"""));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Bash_GitWorktreeAdd_OutsidePath_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);
        string outside = Path.Combine(_root, "newwt").Replace("\\", "\\\\");

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", $$"""{"command":"git worktree add {{outside}} HEAD"}"""));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Bash_GitCheckoutDashDash_InsidePath_Allowed()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", """{"command":"git checkout HEAD -- sub/file.txt"}"""));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Bash_GitCheckoutBranchSwitch_NoDashDash_Allowed()
    {
        // `git checkout <branch>` (no `--`) is a branch switch, not a path restore — it carries no
        // path argument for the containment check to police, so it must never be blocked.
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", """{"command":"git checkout feature-branch"}"""));

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("git stash push -m wip")]
    [InlineData("git stash pop")]
    [InlineData("git stash apply")]
    [InlineData("git stash list")]
    [InlineData("git stash")]
    public async Task Bash_GitStashFamily_AlwaysBlocked_RegardlessOfPath(string command)
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, string stderr) = await RunHookAsync(ToolCall("Bash", $$"""{"command":"{{command}}"}"""));

        Assert.Equal(2, exitCode);
        Assert.Contains("stash", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worktree-scoped", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bash_GitStashInCompoundCommand_Blocked()
    {
        WorktreeContainmentHook.WriteHookFiles(_logDir, _worktree);

        (int exitCode, _) = await RunHookAsync(ToolCall("Bash", """{"command":"cd sub && git stash pop"}"""));

        Assert.Equal(2, exitCode);
    }
}
