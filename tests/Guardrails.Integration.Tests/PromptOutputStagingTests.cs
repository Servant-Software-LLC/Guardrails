using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Fast (no tokens) fake-runner proof of the prompt-output staging fix (SSOT §9.5, issue #266): a
/// plan folder physically nested under <c>.claude/</c> (a natural place to keep one — this repo's
/// own plan docs live under <c>.claude/plans/</c>) puts the harness's own <c>GUARDRAILS_STATE_OUT</c>/
/// <c>GUARDRAILS_VERDICT_OUT</c> targets under <c>.claude/</c> too. Before the fix, a real Claude Code
/// sub-agent's Write to either target hit the runtime's sensitive-path block and no retry could clear
/// it. This proves the mechanism end-to-end with an in-process fake <see cref="IPromptRunner"/> — no
/// real claude process, no tokens — mirroring <c>AiMergeWorkerTests</c>' CannedResolutionRunner /
/// HunkDropperRunner pattern: a runner that reads the path handed to it via
/// <see cref="PromptInvocation.Environment"/> and writes a trivial fragment/verdict there.
/// </summary>
public sealed class PromptOutputStagingTests
{
    // ── fake runner ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a trivial fragment to whatever <c>GUARDRAILS_STATE_OUT</c> it is handed (a prompt
    /// action), or a trivial passing verdict to whatever <c>GUARDRAILS_VERDICT_OUT</c> it is handed
    /// (a prompt guardrail) — recording every path it actually received so the test can assert the
    /// staging redirection happened.
    /// </summary>
    private sealed class RecordingPromptRunner : IPromptRunner
    {
        public List<string> ReceivedStateOutPaths { get; } = [];
        public List<string> ReceivedVerdictOutPaths { get; } = [];

        public string Name => "fake-staging";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            if (invocation.Environment.TryGetValue("GUARDRAILS_VERDICT_OUT", out string? verdictOut))
            {
                ReceivedVerdictOutPaths.Add(verdictOut);
                File.WriteAllText(verdictOut, "{\"pass\": true, \"reason\": \"fake verifier says ok\"}");
            }
            else if (invocation.Environment.TryGetValue("GUARDRAILS_STATE_OUT", out string? stateOut))
            {
                ReceivedStateOutPaths.Add(stateOut);
                string taskId = invocation.Environment["GUARDRAILS_TASK_ID"];
                File.WriteAllText(stateOut, "{\"" + taskId + "\": {\"done\": true}}");
            }

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "fake-staging: wrote to whatever path it was handed"
            });
        }
    }

    // ── temp git repo (worktree-mode variant only) ──────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-promptstage-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# prompt-output-staging test");
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

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<Core.Model.GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    // ── plan authoring ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single-task plan physically rooted at <c>&lt;workspaceRoot&gt;/.claude/plans/probe/</c> with
    /// a PROMPT action AND a PROMPT guardrail, so one run exercises both STATE_OUT and VERDICT_OUT
    /// staging. <paramref name="workspaceRelative"/> is the <c>guardrails.json</c> <c>workspace</c>
    /// value — three levels up (out of <c>.claude/plans/probe</c>) so the codebase root sits OUTSIDE
    /// <c>.claude/</c> while only the plan folder (and thus its <c>logs/</c>) is nested inside it —
    /// exactly the real-world shape from issue #266 (a repo whose plan lives at <c>.claude/plans/…</c>).
    /// </summary>
    private static string BuildClaudeNestedPromptPlan(string workspaceRoot, string workspaceRelative = "../../..")
    {
        string planDir = Path.Combine(workspaceRoot, ".claude", "plans", "probe");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "{{workspaceRelative}}",
              "defaultRetries": 0,
              "maxParallelism": 1,
              "promptRunners": {
                "default": "fake",
                "fake": { "command": "fake-claude", "maxTurns": 3 }
              }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-probe");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "probe .claude-nested staging", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"),
            "Write a trivial fragment to your state-out path. Then stop.\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-verify.prompt.md"),
            "Write a passing verdict to your verdict-out path. Then stop.\n");

        return planDir;
    }

    // ── run helpers ──────────────────────────────────────────────────────────────────────────

    private static async Task<(RunReport report, RunJournal journal, RecordingPromptRunner runner)> RunSerialAsync(
        string planDir, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var runner = new RecordingPromptRunner();
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        // No worktree provider + maxParallelism 1 (from config) => serial shared-workspace mode.
        // effectiveWorkspaceRoot inside ActionRunner/GuardrailRunner resolves to `worktreeRoot ??
        // _plan.Workspace` = _plan.Workspace here (worktreeRoot is always null in serial mode).
        var scheduler = new Scheduler(load.Plan!, executor, journal);

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal, runner);
    }

    private static async Task<(RunReport report, RunJournal journal, RecordingPromptRunner runner)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var runner = new RecordingPromptRunner();
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        // maxParallelism: 2 so the Scheduler F7 hard guard doesn't clamp worktree mode back to
        // serial (it requires >1 requested parallelism to keep a real provider).
        var scheduler = new Scheduler(load.Plan!, executor, journal,
            worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier(), maxParallelism: 2);

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal, runner);
    }

    // ── assertions shared by both modes ─────────────────────────────────────────────────────────

    private static void AssertStagedThenPromoted(
        string planDir, RunReport report, RunJournal journal, RecordingPromptRunner runner, string effectiveWorkspaceRoot)
    {
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // (a) the fake runner's ACTION invocation never saw a `.claude`-segmented STATE_OUT path —
        // proves the staging redirection happened.
        string receivedStateOut = Assert.Single(runner.ReceivedStateOutPaths);
        Assert.DoesNotContain(".claude", receivedStateOut.Replace('\\', '/').Split('/'));

        // (b) same for the GUARDRAIL's VERDICT_OUT path.
        string receivedVerdictOut = Assert.Single(runner.ReceivedVerdictOutPaths);
        Assert.DoesNotContain(".claude", receivedVerdictOut.Replace('\\', '/').Split('/'));

        // (c) the DOCUMENTED final path (which DOES sit under .claude/) exists and carries the fake
        // runner's exact bytes — proves the promote-move worked.
        string finalFragmentPath = Path.Combine(
            planDir, "logs", journal.Document.RunId, "01-probe", "attempt-1", "action-out-fragment.json");
        Assert.Contains(".claude", finalFragmentPath.Replace('\\', '/').Split('/'));
        Assert.True(File.Exists(finalFragmentPath));
        Assert.Equal("{\"01-probe\": {\"done\": true}}", File.ReadAllText(finalFragmentPath));

        string finalVerdictPath = Path.Combine(
            planDir, "logs", journal.Document.RunId, "01-probe", "attempt-1", "guardrail-01-verify.verdict.json");
        Assert.Contains(".claude", finalVerdictPath.Replace('\\', '/').Split('/'));
        Assert.True(File.Exists(finalVerdictPath));
        Assert.Equal("{\"pass\": true, \"reason\": \"fake verifier says ok\"}", File.ReadAllText(finalVerdictPath));

        // (d) no .guardrails-agent-io/ FILE residue survives anywhere under the effective workspace
        // (an empty leftover directory shell, if any, is not residue — git never tracks those).
        string stagingRoot = Path.Combine(effectiveWorkspaceRoot, ".guardrails-agent-io");
        if (Directory.Exists(stagingRoot))
        {
            Assert.Empty(Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories));
        }
    }

    // ── tests ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Serial_ClaudeNestedPlan_StagesStateOutAndVerdictOut_PromotesToFinalPath_NoResidue()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-promptstage-serial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string planDir = BuildClaudeNestedPromptPlan(root);

            (RunReport report, RunJournal journal, RecordingPromptRunner runner) =
                await RunSerialAsync(planDir, TestContext.Current.CancellationToken);

            // Serial mode: effectiveWorkspaceRoot = _plan.Workspace = `root` (three levels up from
            // .claude/plans/probe, resolved by the loader).
            AssertStagedThenPromoted(planDir, report, journal, runner, effectiveWorkspaceRoot: root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Worktree_ClaudeNestedPlan_StagesStateOutAndVerdictOut_PromotesToFinalPath_NoResidue()
    {
        using var repo = new TempGitRepo();
        string planDir = BuildClaudeNestedPromptPlan(repo.RepoPath);

        (RunReport report, RunJournal journal, RecordingPromptRunner runner) =
            await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        // Worktree mode: effectiveWorkspaceRoot = the task's segment worktree. Only ONE task ran, so
        // its segment worktree is the sole entry under repo.WorktreeRoot's run folder — the fake
        // runner's received paths already prove which root staging used; residue is checked under
        // every segment worktree that was created.
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        string receivedStateOut = Assert.Single(runner.ReceivedStateOutPaths);
        Assert.DoesNotContain(".claude", receivedStateOut.Replace('\\', '/').Split('/'));
        // The staging path must live under the harness-owned worktree root, not the plan folder.
        Assert.StartsWith(repo.WorktreeRoot, receivedStateOut, StringComparison.OrdinalIgnoreCase);

        string receivedVerdictOut = Assert.Single(runner.ReceivedVerdictOutPaths);
        Assert.DoesNotContain(".claude", receivedVerdictOut.Replace('\\', '/').Split('/'));
        Assert.StartsWith(repo.WorktreeRoot, receivedVerdictOut, StringComparison.OrdinalIgnoreCase);

        string finalFragmentPath = Path.Combine(
            planDir, "logs", journal.Document.RunId, "01-probe", "attempt-1", "action-out-fragment.json");
        Assert.True(File.Exists(finalFragmentPath));
        Assert.Equal("{\"01-probe\": {\"done\": true}}", File.ReadAllText(finalFragmentPath));

        string finalVerdictPath = Path.Combine(
            planDir, "logs", journal.Document.RunId, "01-probe", "attempt-1", "guardrail-01-verify.verdict.json");
        Assert.True(File.Exists(finalVerdictPath));
        Assert.Equal("{\"pass\": true, \"reason\": \"fake verifier says ok\"}", File.ReadAllText(finalVerdictPath));

        // No .guardrails-agent-io/ FILE residue survives anywhere under the harness-owned worktree root.
        if (Directory.Exists(repo.WorktreeRoot))
        {
            var residue = Directory.EnumerateFiles(repo.WorktreeRoot, "*", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/.guardrails-agent-io/"))
                .ToList();
            Assert.Empty(residue);
        }
    }
}
