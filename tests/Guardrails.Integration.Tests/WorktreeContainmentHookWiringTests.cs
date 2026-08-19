using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end WIRING proof for issue #199/#192: a worktree-mode prompt task, run through a FAKE
/// Claude CLI (no tokens, same test-double pattern as <see cref="FakeClaudePlanBuilder"/>) that
/// records the argv it actually received, proves the harness (a) generates the containment hook
/// settings/script INTO the attempt's log dir (never inside the segment worktree — must not
/// pollute <c>git status</c>/the write-scope diff) and (b) passes <c>--settings &lt;path&gt;</c>
/// pointing at that exact file to the real `claude` invocation. The hook's own block/allow
/// decisions are unit-tested standalone in <c>WorktreeContainmentHookTests</c> (Core.Tests); this
/// test proves the PLUMBING that gets the hook in front of the real process in the first place.
/// </summary>
public sealed class WorktreeContainmentHookWiringTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-wchwiring-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly string _planDir;
    private readonly string _argvLogPath;

    public WorktreeContainmentHookWiringTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoPath);
        InitRepo(_repoPath);

        _planDir = Path.Combine(_repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(_planDir, "state"));
        Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

        _argvLogPath = Path.Combine(_root, "argv.log");
        string fakeCliPath = WriteFakeCli();

        string commandJson = fakeCliPath.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "{{commandJson}}",
                  "permissionMode": "acceptEdits",
                  "maxTurns": 5
                }
              }
            }
            """);

        string taskDir = Path.Combine(_planDir, "tasks", "01-generate");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            { "description": "fake prompt task", "dependsOn": [], "action": { "path": "action.prompt.md" } }
            """);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Generate the thing.\n");
        WriteScript(Path.Combine(taskDir, "guardrails", Windows ? "01-ok.ps1" : "01-ok.sh"), GreenGuardrailScript());
    }

    public void Dispose() => SafeDeleteTree(_root);

    /// <summary>Windows-safe recursive delete (strips the read-only bit git leaves on loose objects).</summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }

    [Fact]
    public async Task WorktreeModePromptAction_GeneratesContainmentHook_AndPassesSettingsFlag()
    {
        PlanLoadResult load = new PlanLoader().Load(_planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // Locate the attempt log dir (worktree-mode layout: logs/<runId>/<task>/attempt-N/).
        string logsRoot = Path.Combine(_planDir, "logs");
        string runDir = Directory.GetDirectories(logsRoot).Single();
        string attemptDir = Path.Combine(runDir, "01-generate", "attempt-1");
        Assert.True(Directory.Exists(attemptDir), $"expected attempt dir at {attemptDir}");

        // (a) The hook settings + script were generated INTO the log dir (outside the worktree).
        string settingsPath = Path.Combine(attemptDir, WorktreeContainmentHook.SettingsFileName);
        Assert.True(File.Exists(settingsPath), "containment settings file must be generated");
        string scriptPath = Path.Combine(attemptDir,
            Windows ? WorktreeContainmentHook.ScriptFileNameWindows : WorktreeContainmentHook.ScriptFileNameUnix);
        Assert.True(File.Exists(scriptPath), "containment hook script must be generated");

        string settingsJson = File.ReadAllText(settingsPath);
        Assert.Contains("PreToolUse", settingsJson, StringComparison.Ordinal);
        Assert.Contains(WorktreeContainmentHook.Matcher, settingsJson, StringComparison.Ordinal);

        // (a2) The root BAKED into that script is THIS task's segment worktree — nothing asserted that
        // before, so a wiring bug that baked the plan dir or the user's own checkout would have gone
        // unseen here (and would have handed the agent a hook that polices the wrong tree). The
        // `<taskId>/attempt-N` tail is the shape GitWorktreeProvider.CreateSegment builds; the negative
        // half is the one that matters most — the harness's OUTER boundary may never be the checkout.
        IReadOnlyList<string> bakedRoots = BakedAcceptedRoots(File.ReadAllText(scriptPath));
        Assert.NotEmpty(bakedRoots);
        Assert.All(bakedRoots, r => Assert.True(Path.IsPathRooted(r), $"baked root '{r}' must be absolute"));
        Assert.Equal(
            Path.Combine("01-generate", "attempt-1"),
            string.Join(Path.DirectorySeparatorChar, bakedRoots[0].Split(Path.DirectorySeparatorChar)[^2..]),
            StringComparer.FromComparison(RealPath.Comparison));
        Assert.All(
            bakedRoots,
            r => Assert.False(
                RealPath.IsUnder(r, _repoPath),
                $"the containment root '{r}' must be a segment worktree, never the user's checkout '{_repoPath}'"));

        // (a3) Issue #464, asserted at the COMPOSITION ROOT rather than against a synthetic root: the
        // script must accept the segment worktree in its symlink-RESOLVED spelling too, because that is
        // the spelling the OS, git and the agent's own resolved cwd produce. HONEST SCOPE — this bites
        // only where the harness's worktree root is reached through a link (macOS CI: Path.GetTempPath()
        // lives under /var, a symlink to /private/var); elsewhere Resolve is the identity and the
        // primary entry satisfies it trivially. The every-OS proof of the same property is
        // WorktreeContainmentHookAliasedRootTests in Core.Tests, which builds the link itself.
        Assert.Contains(
            RealPath.Resolve(bakedRoots[0]),
            bakedRoots,
            StringComparer.FromComparison(RealPath.Comparison));

        // (b) The real invocation received --settings pointing at exactly that file.
        Assert.True(File.Exists(_argvLogPath), "fake CLI should have logged its received argv");
        string argv = File.ReadAllText(_argvLogPath);
        Assert.Contains("--settings", argv, StringComparison.Ordinal);
        Assert.Contains(settingsPath, argv, StringComparison.Ordinal);

        // (c) The composed prompt carries the #192 stash-safety warning + safe alternative — the
        // SAME harness-contract injection mechanism as the state/needsHuman contract, gated on
        // worktree mode.
        string composedPrompt = File.ReadAllText(Path.Combine(attemptDir, "composed-prompt.md"));
        Assert.Contains("## Worktree safety", composedPrompt, StringComparison.Ordinal);
        Assert.Contains("git stash", composedPrompt, StringComparison.Ordinal);
        Assert.Contains("git apply", composedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerialModePromptAction_NoContainmentHook_NoStashWarning()
    {
        // Negative-assertion companion (SSOT §4.4 archetype): serial/shared-workspace mode has no
        // isolated segment worktree to contain writes to, so NEITHER the hook NOR the stash-safety
        // warning should ever be injected there — proves the feature is properly GATED, not just
        // present in worktree mode.
        using var plan = new FakeClaudePlanBuilder().AddPromptTask("01-generate", mode: "fragment");

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        string logsRoot = Path.Combine(plan.PlanDir, "logs");
        string runDir = Directory.GetDirectories(logsRoot).Single();
        string attemptDir = Path.Combine(runDir, "01-generate", "attempt-1");

        Assert.False(
            File.Exists(Path.Combine(attemptDir, WorktreeContainmentHook.SettingsFileName)),
            "serial mode must never generate the containment hook settings file");

        string composedPrompt = File.ReadAllText(Path.Combine(attemptDir, "composed-prompt.md"));
        Assert.DoesNotContain("## Worktree safety", composedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("git stash", composedPrompt, StringComparison.Ordinal);
    }

    // --- fixture plumbing --------------------------------------------------------------------

    /// <summary>
    /// The accepted worktree roots (#464) the generator baked into the script, read back out of the
    /// generated text: the single-quoted literals between <c>ACCEPTED_ROOTS=(</c> / <c>$AcceptedRoots =
    /// @(</c> and the closing <c>)</c>. The quote-stripping is deliberately naive — a harness worktree
    /// path under the temp root cannot contain a quote — so a parse that goes wrong shows up as a
    /// failing assertion on the value, not as a silently-empty list.
    /// </summary>
    private static IReadOnlyList<string> BakedAcceptedRoots(string scriptText)
    {
        string[] lines = scriptText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int open = Array.FindIndex(
            lines,
            l => l.EndsWith('(') && l.Contains("ccepted", StringComparison.OrdinalIgnoreCase));
        Assert.True(open >= 0, "the generated script must open an accepted-roots array");

        var roots = new List<string>();
        for (int i = open + 1; i < lines.Length && lines[i].Trim() != ")"; i++)
        {
            roots.Add(lines[i].Trim().Trim('\''));
        }

        return roots;
    }

    private static string GreenGuardrailScript() => Windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n";

    private string WriteFakeCli()
    {
        string path = Path.Combine(_root, Windows ? "fake-claude.cmd" : "fake-claude.sh");
        string argvLog = _argvLogPath.Replace("\\", "\\\\");

        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.ChangeExtension(path, ".ps1");
            File.WriteAllText(ps1,
                $$"""
                $null = [Console]::In.ReadToEnd()
                $argsLine = ($args -join ' ')
                Add-Content -Path "{{argvLog}}" -Value $argsLine
                Write-Output '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":1}'
                """);
            File.WriteAllText(path, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
        }
        else
        {
            string body = "#!/usr/bin/env bash\n" +
                "cat > /dev/null\n" +
                "printf '%s\\n' \"$*\" >> \"" + argvLog + "\"\n" +
                "printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"fake done\",\"total_cost_usd\":0.01,\"num_turns\":1}\\n'\n";
            File.WriteAllText(path, body);
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void InitRepo(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "test@guardrails.local");
        RunGit(repoPath, "config", "user.name", "Guardrails Test");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        RunGit(repoPath, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# containment-hook-wiring-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }
    }
}
