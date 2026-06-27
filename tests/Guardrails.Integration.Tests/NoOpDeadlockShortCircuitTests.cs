using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #174 — the no-op-deadlock short-circuit. A worktree-mode task whose action is a genuine
/// no-op (exits 0, writes no state fragment, touches no file) but whose guardrails keep failing the
/// SAME way cannot converge by retrying. The harness must escalate to <c>needs-human</c> on the
/// SECOND such attempt instead of exhausting the whole retry budget reproducing the identical failure
/// (the real plan-0009 terminal <c>integrationGate</c> against an un-fixable merge artifact).
///
/// These run in worktree mode (a real git repo + <c>maxParallelism: 2</c>) because the "action made
/// no file changes" half of the signal is proven by diffing the segment against <c>taskBase</c> — the
/// short-circuit is deliberately conservative and never fires in serial mode.
/// </summary>
public sealed class NoOpDeadlockShortCircuitTests
{
    // A real git repo for worktree mode; mirrors the proven-safe teardown in MergeLockAndSettleTests.
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-174-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# noop-deadlock-test");
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
    /// Writes a single-task worktree-mode plan inside <paramref name="repoPath"/> with a high retry
    /// budget. The action body and guardrail body are OS-appropriate snippets supplied by the caller,
    /// so a test can make the action a no-op (or a real writer) and the guardrail a stable (or varying)
    /// failure. <c>maxParallelism: 2</c> forces worktree mode (real segments, real <c>taskBase</c>).
    /// </summary>
    private static string CreateOneTaskPlan(string repoPath, int defaultRetries, string actionBody, string guardrailBody)
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

        string taskDir = Path.Combine(planDir, "tasks", "01-noop-gate");
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            {
              "description": "no-op terminal gate (issue #174)",
              "dependsOn": []
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

    /// <summary>A genuine no-op action: exits 0, writes no fragment, touches no file.</summary>
    private static string NoOpAction() => "exit 0";

    /// <summary>An action that writes a NEW file every attempt — observable change (NOT a no-op).</summary>
    private static string WritingAction() => OperatingSystem.IsWindows()
        ? "Set-Content -NoNewline -Path 'work.txt' -Value 'did work'; exit 0"
        : "printf 'did work' > work.txt; exit 0";

    /// <summary>A guardrail that fails with a STABLE, byte-identical message on every attempt.</summary>
    private static string StableFailure() => OperatingSystem.IsWindows()
        ? "Write-Output 'duplicate class CommanderRestImporter in Launcher.cs'; exit 1"
        : "echo 'duplicate class CommanderRestImporter in Launcher.cs'; exit 1";

    /// <summary>
    /// A guardrail that fails with a DIFFERENT message each attempt (driven by a counter file under the
    /// plan dir) — the changed-output case, where retrying might still converge so the budget is kept.
    /// </summary>
    private static string VaryingFailure() => OperatingSystem.IsWindows()
        ? """
          $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'g.count'
          Add-Content -Path $f -Value 'x'
          $n = (Get-Content $f).Count
          Write-Output "failure number $n"
          exit 1
          """
        : """
          f="$GUARDRAILS_PLAN_DIR/g.count"
          echo x >> "$f"
          n=$(wc -l < "$f" | tr -d '[:space:]')
          echo "failure number $n"
          exit 1
          """;

    private static async Task<(RunReport report, string planDir)> RunAsync(TempGitRepo repo, string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in #174 tests."));
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
    public async Task NoOpAction_IdenticalGuardrailFailure_EscalatesOnSecondAttempt_NotAfterWholeBudget()
    {
        // Budget = 1 + 5 = 6 attempts. The #174 short-circuit must escalate on attempt 2.
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(repo.RepoPath, defaultRetries: 5, NoOpAction(), StableFailure());

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);
        Assert.Contains("no-op", gate.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Escalated on the 2ND attempt — exactly 2 attempts journaled, NOT the full budget of 6.
        Assert.Equal(2, AttemptCount(planDir, "01-noop-gate"));
        Assert.Equal(JournalTaskStatus.NeedsHuman,
            JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-noop-gate"].Status);
    }

    [Fact]
    public async Task ActionMakesChanges_GuardrailKeepsFailing_RetriesNormally_NoShortCircuit()
    {
        // The action writes a file every attempt → NOT a no-op → the short-circuit must NOT fire.
        // The task still ends needs-human via normal budget exhaustion, but only after the FULL budget.
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(repo.RepoPath, defaultRetries: 2, WritingAction(), StableFailure());

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: 1 + 2 retries = 3 attempts (no early short-circuit).
        Assert.Equal(3, AttemptCount(planDir, "01-noop-gate"));
    }

    [Fact]
    public async Task NoOpAction_GuardrailOutputDiffersEachAttempt_RetriesNormally_NoShortCircuit()
    {
        // A no-op action, but the guardrail failure CHANGES every attempt → those can still converge,
        // so the short-circuit must NOT fire; the full budget is spent.
        using var repo = new TempGitRepo();
        string planDir = CreateOneTaskPlan(repo.RepoPath, defaultRetries: 2, NoOpAction(), VaryingFailure());

        var (report, _) = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "01-noop-gate");
        Assert.Equal(TaskOutcome.GuardrailFailed, gate.Outcome);
        Assert.DoesNotContain("retrying will not help", gate.Summary, StringComparison.OrdinalIgnoreCase);

        // Full budget honored: 1 + 2 retries = 3 attempts — the changing output is never short-circuited.
        Assert.Equal(3, AttemptCount(planDir, "01-noop-gate"));
    }
}
