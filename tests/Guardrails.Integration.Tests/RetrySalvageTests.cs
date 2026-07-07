using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end tests for issue #195 (retry salvage): a worktree-mode task that hits a NON-LOGIC
/// budget-exhaustion outcome (<c>max-turns</c>) has its rolled-back attempt preserved to an
/// inspectable git ref BEFORE the existing F2 <c>reset --hard</c> discards it, and the NEXT
/// attempt's <c>feedback.md</c> names the ref plus a <c>git diff --stat</c> summary. Covers the
/// scope guard (a <c>guardrail-failed</c> rollback does NOT preserve by default), that a salvaged
/// out-of-writeScope file is still caught by the (unchanged) write-scope check, and ref pruning on
/// task settle-succeeded / <c>--fresh</c>.
/// </summary>
/// <remarks>
/// Issue #253 tripwire: <see cref="HostRepoCleanlinessGuard"/> (an <see cref="IClassFixture{T}"/>)
/// snapshots the REAL Guardrails repo checkout hosting this test run before the class's first test
/// and re-checks it after the last. This class's fixtures use the exact literal filenames
/// (<c>outside.txt</c>, <c>src/output.txt</c>) that a real dogfood run mysteriously saw attributed to
/// two unrelated tasks with zero trace in either task's own transcript — every fixture root here is
/// already isolated under <see cref="Path.GetTempPath"/>, and a thorough investigation (see the issue)
/// could not reproduce a leak from running this suite, but the guard stands regardless as the tripwire
/// that would catch ANY future regression reintroducing one.
/// </remarks>
public sealed class RetrySalvageTests : IClassFixture<HostRepoCleanlinessGuard>, IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-salvage-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly string _planDir;
    private readonly string _counterPath;

    private const string RefAttempt1 = "refs/guardrails/01-implement/attempt-1";

    private enum FakeMode
    {
        /// <summary>Attempt 1 hits max-turns (writing an in-scope file first); attempt 2+ succeeds.</summary>
        MaxTurnsThenSucceed,

        /// <summary>Every attempt hits max-turns (never succeeds) — for the run-end pruning test.</summary>
        MaxTurnsForever,

        /// <summary>Every attempt succeeds cleanly but the GUARDRAIL is what fails (script always exit 1).</summary>
        AlwaysSucceedActionOnly,

        /// <summary>
        /// Attempt 1 hits max-turns; attempt 2+ succeeds but ALSO writes an out-of-scope file, modeling
        /// a bad salvage adoption that must still be caught by the (unchanged) write-scope check.
        /// </summary>
        MaxTurnsThenSucceedWithBadScope
    }

    public RetrySalvageTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoPath);
        InitRepo(_repoPath);

        _planDir = Path.Combine(_repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(_planDir, "state"));
        Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

        // OUTSIDE the repo entirely: the fake CLI's invocation counter must survive the segment's
        // F2 `git reset --hard` + `clean -fd` between attempt 1 (max-turns) and attempt 2 (succeeds).
        _counterPath = Path.Combine(_root, "invocations.count");
    }

    public void Dispose() => SafeDeleteTree(_root);

    private async Task<RunReport> RunAsync(FakeMode mode, bool preserveAttemptsForSalvage = true)
    {
        WritePlan(mode, preserveAttemptsForSalvage);
        PlanLoadResult load = new PlanLoader().Load(_planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MaxTurnsRollback_PreservesRef_AndNextAttemptFeedbackNamesIt_WithDiffStat()
    {
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceed);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The task went on to SUCCEED, so its salvage ref is pruned on settle (proven separately by
        // SalvageRefs_PrunedOnTaskSettleSucceeded) — the ref's mid-run existence + content is instead
        // proven by MaxTurnsRollback_RefContainsThePreservedAttempt (a task that never succeeds, so the
        // ref survives to be inspected). Here we assert what a human/agent would actually SEE: attempt
        // 2's feedback.md (attempt 1's failure feedback, read by attempt 2) names the ref and carries a
        // diff-stat summary of what attempt 1 changed.
        string feedbackPath = Path.Combine(AttemptDir(1), "feedback.md");
        Assert.True(File.Exists(feedbackPath), "expected attempt-1 feedback.md");
        string feedback = File.ReadAllText(feedbackPath);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains(RefAttempt1, feedback);
        Assert.Contains($"git checkout {RefAttempt1} -- <path>", feedback);
        Assert.Contains("output.txt", feedback); // the diff-stat summary names the changed file
    }

    [Fact]
    public async Task MaxTurnsRollback_RefContainsThePreservedAttempt()
    {
        // A task that NEVER succeeds (every attempt hits max-turns) keeps every salvage ref past run
        // end (the settle-prune only fires on Succeeded — proven by SalvageRefs_PrunedOnFreshReset),
        // so its attempt-1 ref can be inspected here for CONTENT: it must contain the in-scope file
        // that (rolled-back) attempt actually wrote, proving the preserve captured the real tree.
        await RunAsync(FakeMode.MaxTurnsForever);

        Assert.True(RefExists(_repoPath, RefAttempt1), $"expected salvage ref {RefAttempt1} to exist");
        string blobAtRef = RunGit(_repoPath, "show", $"{RefAttempt1}:src/output.txt").Trim();
        Assert.Equal("attempt-1-output", blobAtRef);
    }

    [Fact]
    public async Task PreserveAttemptsForSalvage_False_DisablesPreservation()
    {
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceed, preserveAttemptsForSalvage: false);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "salvage ref must NOT be created when preserveAttemptsForSalvage is false");

        string feedback = File.ReadAllText(Path.Combine(AttemptDir(1), "feedback.md"));
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public async Task GuardrailFailedRollback_DoesNotPreserveByDefault()
    {
        // Scope guard: a task whose GUARDRAIL fails (the action itself always succeeds — never
        // max-turns/output-cap) must NOT get a salvage ref, even with the config on. The code may be
        // genuinely wrong, so a guardrail-class failure is never silently carried forward.
        RunReport report = await RunAsync(FakeMode.AlwaysSucceedActionOnly);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome); // needs-human after budget exhaustion

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "a guardrail-failed rollback must not preserve a salvage ref by default");
    }

    [Fact]
    public async Task SalvagedOutOfScopeFile_StillCaughtByWriteScopeCheck()
    {
        // Deliverable 5: salvaged files remain subject to writeScope. The task declares writeScope
        // ["src/"], and attempt 2 (simulating an adopted salvage that included an out-of-scope file)
        // ALSO writes outside src/ — the existing retrospective write-scope check (which runs on the
        // FINAL state regardless of how it got there) must still catch it and fail the attempt.
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceedWithBadScope);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("write-scope violation", task.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside.txt", task.Summary);
    }

    [Fact]
    public async Task SalvageRefs_PrunedOnTaskSettleSucceeded()
    {
        await RunAsync(FakeMode.MaxTurnsThenSucceed);

        // Sanity already proven by the first test (the ref exists mid-flow); by the time RunAsync
        // returns, the task settled Succeeded, so the Scheduler's OnSettledAsync prune must have run.
        Assert.False(RefExists(_repoPath, RefAttempt1),
            "salvage refs for a task that went on to succeed must be pruned on settle");
    }

    [Fact]
    public async Task SalvageRefs_PrunedOnFreshReset()
    {
        // A task that NEVER succeeds (every attempt hits max-turns) keeps its salvage ref past run end
        // — the settle-prune only fires on Succeeded. --fresh (RunReset.Fresh) must sweep it instead.
        RunReport report = await RunAsync(FakeMode.MaxTurnsForever);
        Assert.NotEqual(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);

        Assert.True(RefExists(_repoPath, RefAttempt1),
            "sanity: a task that never succeeds must keep its salvage ref after the run (no settle-prune)");

        RunReset.Fresh(_planDir);

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "--fresh must prune every salvage ref via PruneAllSalvageRefs");
    }

    // --- fixture plumbing --------------------------------------------------------------------

    private string AttemptDir(int attempt)
    {
        string logsRoot = Path.Combine(_planDir, "logs");
        string runDir = Directory.GetDirectories(logsRoot).Single();
        return Path.Combine(runDir, "01-implement", $"attempt-{attempt}");
    }

    private void WritePlan(FakeMode mode, bool preserveAttemptsForSalvage)
    {
        string fakeCliPath = WriteFakeCli(mode);
        string commandJson = fakeCliPath.Replace("\\", "\\\\");

        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 1,
              "maxParallelism": 2,
              "preserveAttemptsForSalvage": {{(preserveAttemptsForSalvage ? "true" : "false")}},
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

        string taskDir = Path.Combine(_planDir, "tasks", "01-implement");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            {
              "description": "fake prompt task exercising retry salvage",
              "dependsOn": [],
              "writeScope": ["src/"],
              "action": { "path": "action.prompt.md" }
            }
            """);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Implement the thing.\n");

        string guardrailBody = mode == FakeMode.AlwaysSucceedActionOnly
            ? (Windows ? "exit 1\n" : "#!/usr/bin/env bash\nexit 1\n")
            : (Windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
        WriteScript(Path.Combine(taskDir, "guardrails", Windows ? "01-ok.ps1" : "01-ok.sh"), guardrailBody);
    }

    /// <summary>
    /// The fake Claude CLI. Every invocation increments a counter file kept OUTSIDE the repo (so it
    /// survives the segment's F2 reset between attempts) and always writes an IN-SCOPE
    /// <c>src/output.txt</c> so the action has SOME observable effect. Behavior then branches on
    /// <paramref name="mode"/>:
    /// <list type="bullet">
    /// <item><see cref="FakeMode.MaxTurnsThenSucceed"/>: invocation 1 emits <c>error_max_turns</c> (no
    /// fragment); invocation 2+ writes the fragment and succeeds.</item>
    /// <item><see cref="FakeMode.MaxTurnsForever"/>: EVERY invocation emits <c>error_max_turns</c>.</item>
    /// <item><see cref="FakeMode.AlwaysSucceedActionOnly"/>: EVERY invocation succeeds with a fragment —
    /// the guardrail script (not the action) is what fails in this mode.</item>
    /// <item><see cref="FakeMode.MaxTurnsThenSucceedWithBadScope"/>: like
    /// <see cref="FakeMode.MaxTurnsThenSucceed"/>, but invocation 2+ ALSO writes an out-of-scope
    /// <c>outside.txt</c> (simulating an adopted salvage that pulled in a bad file).</item>
    /// </list>
    /// </summary>
    private string WriteFakeCli(FakeMode mode)
    {
        string path = Path.Combine(_root, Windows ? "fake-claude.cmd" : "fake-claude.sh");
        string counter = _counterPath.Replace("\\", "\\\\");
        bool maxTurnsForever = mode == FakeMode.MaxTurnsForever;
        bool maxTurnsFirstOnly = mode is FakeMode.MaxTurnsThenSucceed or FakeMode.MaxTurnsThenSucceedWithBadScope;
        bool badScope = mode == FakeMode.MaxTurnsThenSucceedWithBadScope;

        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.ChangeExtension(path, ".ps1");
            File.WriteAllText(ps1,
                $$"""
                $null = [Console]::In.ReadToEnd()
                $count = 0
                if (Test-Path "{{counter}}") { $count = [int](Get-Content "{{counter}}" -Raw).Trim() }
                $count++
                Set-Content -NoNewline -Path "{{counter}}" -Value "$count"

                $srcDir = Join-Path $env:GUARDRAILS_WORKSPACE 'src'
                New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
                Set-Content -NoNewline -Path (Join-Path $srcDir 'output.txt') -Value "attempt-$count-output"

                $hitMaxTurns = ({{(maxTurnsForever ? "$true" : "$false")}}) -or (({{(maxTurnsFirstOnly ? "$true" : "$false")}}) -and ($count -eq 1))

                if ($hitMaxTurns) {
                    Write-Output '{"type":"result","subtype":"error_max_turns","is_error":true,"result":"Reached maximum number of turns (5)","num_turns":5}'
                } else {
                    if ({{(badScope ? "$true" : "$false")}}) {
                        Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'outside.txt') -Value 'out of scope'
                    }
                    if ($env:GUARDRAILS_STATE_OUT) {
                        $frag = '{"' + $env:GUARDRAILS_TASK_ID + '": {"done": true}' + '}'
                        Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value $frag
                    }
                    Write-Output '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":3}'
                }
                """);
            File.WriteAllText(path, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
        }
        else
        {
            string body =
                "#!/usr/bin/env bash\n" +
                "cat > /dev/null\n" +
                "count=0\n" +
                $"if [ -f \"{counter}\" ]; then count=$(cat \"{counter}\" | tr -d '[:space:]'); fi\n" +
                "count=$((count + 1))\n" +
                $"printf '%s' \"$count\" > \"{counter}\"\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                "printf 'attempt-%s-output' \"$count\" > \"$GUARDRAILS_WORKSPACE/src/output.txt\"\n" +
                (maxTurnsForever
                    ? "if true; then\n"
                    : maxTurnsFirstOnly
                        ? "if [ \"$count\" -eq 1 ]; then\n"
                        : "if false; then\n") +
                "  printf '{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true,\"result\":\"Reached maximum number of turns (5)\",\"num_turns\":5}\\n'\n" +
                "else\n" +
                (badScope ? "  printf 'out of scope' > \"$GUARDRAILS_WORKSPACE/outside.txt\"\n" : "") +
                "  if [ -n \"$GUARDRAILS_STATE_OUT\" ]; then\n" +
                "    printf '{\"%s\": {\"done\": true}}' \"$GUARDRAILS_TASK_ID\" > \"$GUARDRAILS_STATE_OUT\"\n" +
                "  fi\n" +
                "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"fake done\",\"total_cost_usd\":0.01,\"num_turns\":3}\\n'\n" +
                "fi\n";
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
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# retry-salvage-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    private static bool RefExists(string repoPath, string refName)
    {
        var (_, exitCode) = TryRunGit(repoPath, "rev-parse", "--verify", "--quiet", refName);
        return exitCode == 0;
    }

    private static string RunGit(string workingDir, params string[] args)
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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }
        return stdout;
    }

    private static (string stdout, int exitCode) TryRunGit(string workingDir, params string[] args)
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
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, proc.ExitCode);
    }

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
}

/// <summary>
/// Issue #253 tripwire, shared as an <see cref="IClassFixture{T}"/> so it wraps every test in
/// <see cref="RetrySalvageTests"/> (constructed once before the class's first test, disposed once
/// after its last): snapshots <c>git status --porcelain</c> of the REAL repo checkout hosting this
/// test run, then asserts on teardown that no NEW path appeared. A dogfood run once saw a live task's
/// write-scope check (<c>git add -A</c> in its segment worktree) attribute two files —
/// <c>outside.txt</c> and <c>src/output.txt</c> — to the agent with zero trace in its own transcript.
/// Those are the exact literal fixture names <see cref="RetrySalvageTests.WriteFakeCli"/> uses, which
/// made this suite a prime (if never confirmed) suspect; every fixture root in this file is already
/// isolated under <see cref="Path.GetTempPath"/>, so this guard is currently a no-op tripwire, not a
/// fix for a confirmed leak — its value is catching any FUTURE regression that reintroduces one,
/// exactly matching the issue's own suggested regression test ("assert `git status` in the real repo
/// stays clean around this test").
/// </summary>
public sealed class HostRepoCleanlinessGuard : IDisposable
{
    private readonly string? _hostRepoRoot;
    private readonly HashSet<string> _before;

    public HostRepoCleanlinessGuard()
    {
        _hostRepoRoot = FindEnclosingGitRepo(AppContext.BaseDirectory);
        _before = _hostRepoRoot is null ? [] : StatusLines(_hostRepoRoot);
    }

    public void Dispose()
    {
        // Not running from within a git checkout (e.g. some future packaging context) — nothing to
        // guard; this is a best-effort tripwire, not a hard requirement of the test environment.
        if (_hostRepoRoot is null) return;

        HashSet<string> after = StatusLines(_hostRepoRoot);
        List<string> newEntries = after.Except(_before).ToList();

        Assert.True(newEntries.Count == 0,
            "RetrySalvageTests must not leave any new untracked/modified path in the REAL repo " +
            "hosting the test run (issue #253) -- new git-status line(s): " + string.Join(" | ", newEntries));
    }

    /// <summary>Walks up from <paramref name="startDir"/> looking for a `.git` dir/file (a worktree's
    /// `.git` is a file, not a dir). Returns null if none is found (not running inside a checkout).</summary>
    private static string? FindEnclosingGitRepo(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static HashSet<string> StatusLines(string repoRoot)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--porcelain");
        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // A failure here (e.g. git not on PATH in some exotic environment) must not itself fail the
        // guard — return an empty snapshot so before/after compare equal and this stays a no-op.
        if (proc.ExitCode != 0) return [];

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToHashSet(StringComparer.Ordinal);
    }
}
