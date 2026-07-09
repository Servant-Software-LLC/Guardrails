using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #190 part 3 — the reported "journal staleness after rate-limit recovery" incident: a task's
/// <c>state/run.json</c> showed <c>status: succeeded</c> but <c>attempts[]</c> contained only the stale
/// <c>rate-limited</c> attempt-1 record, even though a real <c>attempt-2/</c> folder on disk proved a
/// second attempt genuinely ran to success.
/// <para>
/// Root-cause tracing (see the harness-developer report): <see cref="Scheduler"/>'s
/// <c>RecordSucceededSettle</c> only takes the attempt-less <see cref="ISchedulerJournal.RecordSettle"/>
/// fallback when <see cref="TaskResult.PendingAttempt"/> is null — and the ONLY worktree-mode path that
/// sets <see cref="TaskResult.DeferredSettle"/> (<see cref="AttemptJournaler.ValidateFragmentForSettle"/>)
/// ALWAYS populates <see cref="TaskResult.PendingAttempt"/> on its success return. A resumed task
/// re-enters <see cref="TaskExecutor.ExecuteAsync"/> through the ordinary attempt loop (the Scheduler's
/// resume pre-pass only short-circuits an already-<c>succeeded</c> task), so its NEXT attempt goes
/// through the full <c>ValidateFragmentForSettle</c> path exactly like a first attempt. NO actual bug was
/// found on current <c>master</c> for the worktree-mode append path — this test proves the append works
/// correctly end-to-end across a rate-limit-exhaust → resume → succeed cycle, closing the gap between
/// "believed to work" and "proven to work" with real git worktrees (no fake provider) and a TCS-free
/// sequencing fake <see cref="IPromptRunner"/> (no real sleeps, no real Claude CLI).
/// </para>
/// </summary>
public sealed class RateLimitResumeJournalTests
{
    // ── temp git repo (mirrors StagingOutputsRunTests/AiMergeWorkerTests conventions) ──────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-rl-resume-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# rate-limit resume test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public static void Git(string workingDir, params string[] args)
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
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
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
    /// A scripted <see cref="IPromptRunner"/>: returns a never-clearing transient result FOREVER (so the
    /// first `guardrails run` exhausts its tiny pause budget and settles rate-limited), or a clean
    /// success once <see cref="SucceedFromHereOn"/> is flipped (simulating the human re-running the plan
    /// after the provider-side limit cleared).
    /// </summary>
    private sealed class ResumableRunner : IPromptRunner
    {
        public bool SucceedFromHereOn;
        public int Calls;
        public string Name => "claude";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            if (!SucceedFromHereOn)
            {
                return Task.FromResult(new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = "session limit · resets soon",
                    FailureKind = PromptFailureKind.Transient,
                    Summary = "claude reported is_error (session limit)"
                });
            }

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                CostUsd = 0.02m,
                FailureKind = PromptFailureKind.None,
                Summary = "claude completed"
            });
        }
    }

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    /// <summary>One prompt-action task, a trivial always-pass deterministic guardrail, tiny pause budget.</summary>
    private static string WriteSingleTaskPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks", "01-task", "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 2,
              "maxParallelism": 1,
              "transientPauseBudgetSeconds": 1,
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "rate-limit resume task", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-ok.cmd" : "01-ok.sh");
        File.WriteAllText(guardrailPath, win ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }

    /// <summary>Run the plan once (worktree mode, real git) with the given runner. Fresh journal each call skipped — LoadOrCreate resumes.</summary>
    private static async Task<RunReport> RunOnceAsync(
        string planDir, TempGitRepo repo, ResumableRunner runner, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        // LoadOrCreate applies the SSOT §7 resume rules on every call — the SECOND invocation IS the
        // "guardrails run" resume the incident describes (needs-human -> pending, fresh budget).
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry,
            overwatch: null,
            // Instant delay — the backoff/pause budget is exercised but never actually sleeps.
            transientDelay: (_, _) => Task.CompletedTask);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(load.Plan!, executor, journal,
            worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier());

        return await scheduler.RunAsync(load.Plan!, ct);
    }

    [Fact]
    public async Task Worktree_RateLimitExhaust_ThenResume_ThenSucceed_JournalCarriesBothAttempts()
    {
        using var repo = new TempGitRepo();
        string planDir = WriteSingleTaskPlan(repo.RepoPath);
        CancellationToken ct = TestContext.Current.CancellationToken;

        // ── Run 1: the runner never clears its transient signal, so the tiny pause budget is spent
        // and the task settles RateLimited (attempt-1 recorded with AttemptOutcome.RateLimited). ──
        var firstRunner = new ResumableRunner { SucceedFromHereOn = false };
        RunReport firstReport = await RunOnceAsync(planDir, repo, firstRunner, ct);

        TaskResult firstResult = Assert.Single(firstReport.Tasks);
        Assert.Equal(TaskOutcome.RateLimited, firstResult.Outcome);

        JournalDocument afterFirst = JournalReader.Read(RunJournal.PathFor(planDir));
        TaskJournalEntry entryAfterFirst = afterFirst.Tasks["01-task"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entryAfterFirst.Status);
        AttemptRecord attempt1 = Assert.Single(entryAfterFirst.Attempts);
        Assert.Equal(1, attempt1.Attempt);
        Assert.Equal(AttemptOutcome.RateLimited, attempt1.Outcome);

        // A real attempt-1 log dir must exist on disk (the "believed to work" claim under test).
        string attempt1LogDir = Path.Combine(planDir, attempt1.LogDir.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(attempt1LogDir), $"expected {attempt1LogDir} to exist");

        // ── Resume: a plain `guardrails run` (not --fresh). RunJournal.LoadOrCreate resets the
        // needs-human task to pending with a fresh budget (issue #190 part 2 — outcome-agnostic). ──
        var secondRunner = new ResumableRunner { SucceedFromHereOn = true };
        RunReport secondReport = await RunOnceAsync(planDir, repo, secondRunner, ct);

        TaskResult secondResult = Assert.Single(secondReport.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, secondResult.Outcome);

        // ── The load-bearing assertion: the FINAL journal's attempts[] carries BOTH the stale
        // rate-limited attempt-1 record AND a new succeeded attempt-2 record — not just one or the
        // other, and not a duplicate/overwritten attempt-1. ──
        JournalDocument afterSecond = JournalReader.Read(RunJournal.PathFor(planDir));
        TaskJournalEntry entryAfterSecond = afterSecond.Tasks["01-task"];
        Assert.Equal(JournalTaskStatus.Succeeded, entryAfterSecond.Status);
        Assert.Equal(2, entryAfterSecond.Attempts.Count);

        AttemptRecord finalAttempt1 = entryAfterSecond.Attempts[0];
        AttemptRecord finalAttempt2 = entryAfterSecond.Attempts[1];
        Assert.Equal(1, finalAttempt1.Attempt);
        Assert.Equal(AttemptOutcome.RateLimited, finalAttempt1.Outcome);
        Assert.Equal(2, finalAttempt2.Attempt);
        Assert.Equal(AttemptOutcome.Succeeded, finalAttempt2.Outcome);

        // A real attempt-2 log dir must also exist on disk — the incident's "attempt-2/ folder on disk
        // proved a second attempt genuinely ran" claim, now matched by a journal entry for it.
        string attempt2LogDir = Path.Combine(planDir, finalAttempt2.LogDir.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(attempt2LogDir), $"expected {attempt2LogDir} to exist");
        Assert.NotEqual(attempt1LogDir, attempt2LogDir);
    }
}
