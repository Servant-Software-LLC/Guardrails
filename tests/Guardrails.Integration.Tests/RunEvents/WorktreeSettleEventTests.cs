using System.Diagnostics;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Behavioural proof of Bug A (SSOT §15.2a, plan 35): the worktree-mode SUCCESS path never raises
/// <see cref="IRunObserver.AttemptFinished"/>. <c>Scheduler.RecordSucceededSettle</c> journals the
/// attempt (via <see cref="ISchedulerJournal.RecordSettleWithAttempt"/>) but calls nothing on the
/// observer, so a supervisor watching <c>events.jsonl</c> sees <c>task-started</c> and, eventually,
/// <c>task-settled</c> — with no <c>attempt-finished</c> row in between for the one path most real runs
/// take (worktree mode is the default).
///
/// <para><b>Two traps this file is built to avoid</b> (see the task 08 brief):
/// <list type="number">
///   <item>A test driven through the FAKE worktree provider never reaches
///   <see cref="AttemptJournaler.ValidateFragmentForSettle"/>'s real deferred settle — it would take a
///   short-circuit and prove nothing about the bug.</item>
///   <item>A test that only sets <c>maxParallelism &gt; 1</c> can be silently demoted to the SERIAL path
///   (issue #596: <c>SchedulerFactory</c> spells the worktree predicate twice and can disagree with
///   itself), which already raises <c>AttemptFinished</c> and would pass for the wrong reason.</item>
/// </list>
/// Every test here therefore constructs a <see cref="Guardrails.Core.Execution.Scheduler"/> DIRECTLY
/// with a real <see cref="GitWorktreeProvider"/> — the same pattern <c>MergeLockAndSettleTests</c> uses —
/// which sidesteps <c>SchedulerFactory</c>'s predicate entirely, and asserts on
/// <see cref="TaskResult.DeferredSettle"/>, which <see cref="AttemptJournaler.ValidateFragmentForSettle"/>
/// is the ONLY place that sets true (see its doc comment on <c>RunReport.cs</c>). That field, not a mere
/// "a row appeared" check, is the path assertion the brief requires.</para>
/// </summary>
public sealed class WorktreeSettleEventTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — same proven-safe pattern MergeLockAndSettleTests uses (strip read-only
    // before delete, Windows-portable).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wste-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# worktree-settle-event-test");
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single-task plan INSIDE <paramref name="repoPath"/> with <c>maxParallelism: 2</c> (worktree
    /// mode). The action writes a state fragment AND a real source file (so the segment's git commit is
    /// non-empty and settles via a clean FF — no re-verifier needed).
    /// </summary>
    private static string CreateSingleTaskWorktreePlan(string repoPath, string taskId)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "worktree-settle-event fixture {{taskId}}",
              "writeScope": ["src/**"],
              "dependsOn": []
            }
            """);

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";
        string safeName = taskId.Replace("-", "_");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\n" +
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{taskId}.cs\" -Force" +
                $" -Value 'class {safeName} {{}}' | Out-Null\n" +
                "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                $"printf 'class {safeName} {{}}' > \"$GUARDRAILS_WORKSPACE/src/{taskId}.cs\"\n" +
                "exit 0\n");
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }

    /// <summary>
    /// The serial-mode contrast fixture: a plain temp plan dir with NO git repo and
    /// <c>maxParallelism: 1</c>, so it never touches <see cref="IWorktreeProvider"/> at all.
    /// </summary>
    private static string CreateSingleTaskSerialPlan(string taskId)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr-wste-serial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "worktree-settle-event serial control {{taskId}}",
              "writeScope": [],
              "dependsOn": []
            }
            """);

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'\n" +
                "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                $"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                "exit 0\n");
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Run + events.jsonl plumbing
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string FreshLogsDir() =>
        Path.Combine(Path.GetTempPath(), "gr-wste-logs-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Drives <paramref name="planDir"/> to completion by constructing the Scheduler DIRECTLY with a
    /// real <paramref name="provider"/> — never through <c>SchedulerFactory</c> (issue #596: its
    /// worktree predicate is spelled twice and the two evaluations can disagree). The same
    /// <paramref name="observer"/> instance is wired into BOTH the executor (so the serial-mode
    /// <c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c> forwarding still works if ever taken)
    /// and the scheduler (so a future fix to the worktree settle path is observed here too).
    /// </summary>
    private static async Task<RunReport> RunDirectAsync(
        string planDir, IWorktreeProvider? provider, IRunObserver observer, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in worktree-settle-event tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, observer: observer);

        return await scheduler.RunAsync(load.Plan!, ct);
    }

    /// <summary>Every <c>events.jsonl</c> row of the given <c>kind</c>, parsed.</summary>
    private static List<JsonNode> ReadEventRows(string logsDir, string kind)
    {
        string path = Path.Combine(logsDir, "events.jsonl");
        if (!File.Exists(path)) return [];

        return [.. File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonNode.Parse(line))
            .Where(node => node?["kind"]?.GetValue<string>() == kind)
            .Select(node => node!)];
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 1 — the path assertion, standing alone. Must PASS today (declared red-census exemption).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task WorktreeSucceededSettle_TakesTheDeferredSettlePath()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateSingleTaskWorktreePlan(repo.RepoPath, "01-only");
        string logsDir = FreshLogsDir();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var events = new RunEventStream(IRunObserver.Null, logsDir, runId: "test-run");

        RunReport report = await RunDirectAsync(
            planDir, provider, events, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "fixture task must succeed; " + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The path assertion: DeferredSettle is set ONLY by AttemptJournaler.ValidateFragmentForSettle
        // (the real worktree deferred-settle path) — false in serial mode and on the fake-provider
        // short-circuit (RunReport.cs doc comment on TaskResult.DeferredSettle). A demoted-to-serial run
        // (issue #596) would report false here, failing this assertion — that is the point.
        Assert.True(task.DeferredSettle,
            "expected the fixture task to settle via AttemptJournaler.ValidateFragmentForSettle (the real " +
            "worktree deferred-settle path); TaskResult.DeferredSettle was false, meaning the run took the " +
            "serial/fake-provider short-circuit instead — this test's own control would be meaningless.");
        Assert.NotNull(task.PendingAttempt);

        // Corroborate at the git layer: the plan-integration branch only exists if the REAL provider's
        // git integration commit actually ran. A demoted-to-serial run never creates it.
        string planBranch = "guardrails/" + Path.GetFileName(planDir);
        string branches = TempGitRepo.Git(repo.RepoPath, "branch", "--list", planBranch);
        Assert.Contains(planBranch, branches);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 2 — Bug A. Must FAIL today.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task WorktreeSucceededAttempt_EmitsAnAttemptFinishedRow()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateSingleTaskWorktreePlan(repo.RepoPath, "01-only");
        string logsDir = FreshLogsDir();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var events = new RunEventStream(IRunObserver.Null, logsDir, runId: "test-run");

        RunReport report = await RunDirectAsync(
            planDir, provider, events, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "fixture task must succeed; " + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        TaskResult task = Assert.Single(report.Tasks);
        // Same path guard as test 1: if this ever reads false, the failure below is not Bug A.
        Assert.True(task.DeferredSettle,
            "expected the real worktree deferred-settle path (see WorktreeSucceededSettle_TakesTheDeferredSettlePath); " +
            "TaskResult.DeferredSettle was false, so a failure of the assertion below would not be Bug A.");

        List<JsonNode> attemptFinishedRows = ReadEventRows(logsDir, "attempt-finished");
        JsonNode? row = attemptFinishedRows.FirstOrDefault(r => r?["taskId"]?.GetValue<string>() == "01-only");

        Assert.True(row is not null && row["outcome"]?.GetValue<string>() == "succeeded",
            "Bug A: the worktree-mode SUCCESS path (AttemptJournaler.ValidateFragmentForSettle -> " +
            "Scheduler.RecordSucceededSettle) emitted NO 'attempt-finished' row for '01-only' in " +
            "events.jsonl. Nothing on this path calls IRunObserver.AttemptFinished, so a supervisor " +
            "watching the stream sees the task start and settle but never sees its attempt complete.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test 3 — the contrast case. Must PASS today AND after task 09 (declared red-census exemption).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task SerialSucceededAttempt_StillEmitsAnAttemptFinishedRow()
    {
        string planDir = CreateSingleTaskSerialPlan("01-only");
        try
        {
            string logsDir = FreshLogsDir();
            var events = new RunEventStream(IRunObserver.Null, logsDir, runId: "test-run");

            RunReport report = await RunDirectAsync(
                planDir, provider: null, events, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded,
                "fixture task must succeed; " + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

            TaskResult task = Assert.Single(report.Tasks);
            // The mirror-image path guard: a serial run must NEVER take the deferred-settle path.
            Assert.False(task.DeferredSettle,
                "expected the serial control task to settle via AttemptJournaler.CompleteSucceededOrInvalidFragment " +
                "(no worktree provider was supplied); TaskResult.DeferredSettle was true.");

            List<JsonNode> attemptFinishedRows = ReadEventRows(logsDir, "attempt-finished");
            JsonNode? row = attemptFinishedRows.FirstOrDefault(r => r?["taskId"]?.GetValue<string>() == "01-only");

            Assert.True(row is not null && row["outcome"]?.GetValue<string>() == "succeeded",
                "control case: the serial success path already calls IRunObserver.AttemptFinished from " +
                "AttemptJournaler.CompleteSucceededOrInvalidFragment. A failure here is a REGRESSION, not Bug A " +
                "— it means the fix for Bug A moved the event rather than adding the missing worktree route.");
        }
        finally
        {
            try { Directory.Delete(planDir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }
}
