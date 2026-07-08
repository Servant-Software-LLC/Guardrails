using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #264 — the deterministic-script reproduction short-circuit. A <c>script</c>-action task
/// cannot self-correct between attempts (there is no agent, just fixed bytes), so when a script's
/// recorded output reproduces BYTE-IDENTICALLY across two guardrail-class-failed attempts, re-running
/// it the rest of the budget is guaranteed-wasted work. The harness must escalate to
/// <c>needs-human</c> on the SECOND identical attempt instead of exhausting the whole retry budget
/// (the observed <c>02-vendor-validator</c> guardrail case and <c>10-gitignore</c> write-scope case).
///
/// This is the SIBLING of the #174 no-op-deadlock short-circuit: #174 fires only when the action made
/// NO file change, but a script that WROTE FILES is not a no-op — the gap #264 fills. The SAFE trigger
/// is byte-identical action output (positive evidence the script is DETERMINISTIC); a nondeterministic
/// script whose output differs across attempts keeps its full budget, because a retry genuinely might
/// pass (the flaky/network/timestamp escape hatch).
///
/// These run in worktree mode (a real git repo + <c>maxParallelism: 2</c>) because the byte-identical
/// action-output evidence is the load-bearing signal and the write-scope check only runs against a real
/// segment; the serial deterministic-script case is already covered by the #182 serial no-op gate.
/// </summary>
public sealed class ScriptActionReproductionShortCircuitTests
{
    // A real git repo for worktree mode; mirrors NoOpDeadlockShortCircuitTests' proven-safe teardown.
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-264-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# script-reproduction-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    /// <summary>
    /// Writes a single-task worktree-mode plan inside <paramref name="repoPath"/>. The action/guardrail
    /// bodies are OS-appropriate snippets supplied by the caller; an optional <paramref name="writeScope"/>
    /// drives the deterministic write-scope check. <c>maxParallelism: 2</c> forces worktree mode.
    /// </summary>
    private static string CreateOneTaskPlan(
        string repoPath, int defaultRetries, string actionBody, string guardrailBody, string? writeScope = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": 2
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-script-gate");
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        string scopeLine = writeScope is null ? "" : $",\n              \"writeScope\": [\"{writeScope}\"]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "deterministic script gate (issue #264)",
              "dependsOn": []{{scopeLine}}
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), actionBody);
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), guardrailBody);
        return planDir;
    }

    private static string ActionFileName => OperatingSystem.IsWindows() ? "action.ps1" : "action.sh";
    private static string GuardrailFileName => OperatingSystem.IsWindows() ? "01-check.ps1" : "01-check.sh";

    private static void WriteScript(string path, string body)
    {
        string content = OperatingSystem.IsWindows() ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// A DETERMINISTIC script: writes a file (so it is NOT a #174 no-op) and prints a STABLE stdout,
    /// byte-identical on every attempt — the observed <c>02-vendor-validator</c> shape.
    /// </summary>
    private static string DeterministicWritingAction() => OperatingSystem.IsWindows()
        ? "Set-Content -NoNewline -Path 'work.txt' -Value 'did work'; Write-Output 'Vendored deps @ 863d130'; exit 0"
        : "printf 'did work' > work.txt; echo 'Vendored deps @ 863d130'; exit 0";

    /// <summary>
    /// A DETERMINISTIC script that writes an OUT-OF-SCOPE file (root <c>outside.txt</c>, outside a
    /// <c>src/**</c> scope) identically every attempt — the observed <c>10-gitignore</c> write-scope shape.
    /// </summary>
    private static string OutOfScopeWritingAction() => OperatingSystem.IsWindows()
        ? "Set-Content -NoNewline -Path 'outside.txt' -Value 'not in scope'; Write-Output 'wrote dotfile'; exit 0"
        : "printf 'not in scope' > outside.txt; echo 'wrote dotfile'; exit 0";

    /// <summary>
    /// A NONDETERMINISTIC script: writes a file AND prints a CHANGING stdout each attempt (counter-file
    /// driven under the plan dir, which survives the F2 segment reset). A retry genuinely might behave
    /// differently, so the short-circuit must NOT fire and the full budget is honored.
    /// </summary>
    private static string NondeterministicWritingAction() => OperatingSystem.IsWindows()
        ? """
          Set-Content -NoNewline -Path 'work.txt' -Value 'did work'
          $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'a.count'
          Add-Content -Path $f -Value 'x'
          $n = (Get-Content $f).Count
          Write-Output "action run number $n"
          exit 0
          """
        : """
          printf 'did work' > work.txt
          f="$GUARDRAILS_PLAN_DIR/a.count"
          echo x >> "$f"
          n=$(wc -l < "$f" | tr -d '[:space:]')
          echo "action run number $n"
          exit 0
          """;

    /// <summary>A guardrail that fails with a STABLE, byte-identical message on every attempt.</summary>
    private static string StableFailure() => OperatingSystem.IsWindows()
        ? "Write-Output 'duplicate class CommanderRestImporter in Launcher.cs'; exit 1"
        : "echo 'duplicate class CommanderRestImporter in Launcher.cs'; exit 1";

    /// <summary>A guardrail that always passes (the write-scope check fails first, before guardrails run).</summary>
    private static string AlwaysPass() => "exit 0";

    private static async Task<(RunReport report, string planDir)> RunAsync(TempGitRepo repo, string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in #264 tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(load.Plan!, executor, journal, worktreeProvider: provider);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
        return (report, planDir);
    }

    private static int AttemptCount(string planDir, string taskId) =>
        JournalReader.Read(RunJournal.PathFor(planDir)).Tasks[taskId].Attempts.Count;

    [Fact]
    public async Task DeterministicScript_WritesFiles_GuardrailFailsIdentically_EscalatesOnSecondAttempt()
    {
        // Budget = 1 + 5 = 6 attempts. The script writes a file (NOT a #174 no-op) but reproduces
        // byte-identical stdout AND fails the same guardrail every attempt → #264 escalates on attempt 2.
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(repo.RepoPath, defaultRetries: 5, DeterministicWritingAction(), StableFailure());

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-script-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);
        Assert.Contains("script", gate.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);
        // It is the #264 (deterministic-script) path, NOT the #174 no-op path (the action DID write a file).
        Assert.DoesNotContain("no-op", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Escalated on the 2ND attempt — exactly 2 attempts journaled, NOT the full budget of 6.
        Assert.Equal(2, AttemptCount(planDir, "01-script-gate"));
        Assert.Equal(JournalTaskStatus.NeedsHuman,
            JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-script-gate"].Status);
    }

    [Fact]
    public async Task DeterministicScript_WriteScopeViolationReproduces_EscalatesOnSecondAttempt()
    {
        // The observed 10-gitignore case: a script deterministically writes an OUT-OF-SCOPE file, so the
        // write-scope CHECK (a guardrail-class failure) fails identically every attempt. Budget = 1 + 5 = 6;
        // #264 must escalate on attempt 2 rather than burning the whole budget on the unchanged violation.
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(
            repo.RepoPath, defaultRetries: 5, OutOfScopeWritingAction(), AlwaysPass(), writeScope: "src/**");

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-script-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);
        Assert.Contains("script", gate.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Escalated on the 2ND attempt — exactly 2 attempts journaled, NOT the full budget of 6.
        Assert.Equal(2, AttemptCount(planDir, "01-script-gate"));
    }

    [Fact]
    public async Task NondeterministicScript_DifferentOutputEachAttempt_GuardrailFails_RetriesFullBudget()
    {
        // The byte-identical guard, not a blanket "all scripts short-circuit": the action's stdout changes
        // every attempt, so there is NO positive evidence of determinism and a retry genuinely might pass.
        // The short-circuit must NOT fire — the full budget is spent (1 + 2 retries = 3 attempts).
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(
            repo.RepoPath, defaultRetries: 2, NondeterministicWritingAction(), StableFailure());

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-script-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: the changing action output blocks the deterministic-reproduction gate.
        Assert.Equal(3, AttemptCount(planDir, "01-script-gate"));
    }
}
