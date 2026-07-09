using System.Diagnostics;
using System.Reflection;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration test proving that the production <see cref="SchedulerFactory"/> path does
/// NOT yet create a <see cref="GitWorktreeProvider"/> or wire it into the <see cref="Scheduler"/>.
/// The single test drives the real factory against a committed fixture plan at
/// <c>maxParallelism &gt; 1</c> and asserts the observable worktree-mode outputs:
/// <list type="bullet">
///   <item>A <c>guardrails/&lt;planName&gt;</c> plan branch is created in the repo.</item>
///   <item>At least two commits on that branch carry <c>Guardrails-Task:</c> trailers.</item>
///   <item>The user's original branch HEAD is <b>unchanged</b> (plan branch is separate).</item>
/// </list>
/// All three assertions fail on current code because the factory passes neither
/// <c>worktreeProvider</c> nor <c>reVerifier</c> to the Scheduler — no plan branch is ever
/// created. RED until task 36 wires the factory.
/// Do NOT implement any production wiring here; tests only, in this one file.
/// </summary>
public sealed class ProductionWiringTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — Windows-safe temp repo + separate worktree root.
    // Proven teardown pattern: strip read-only bits before delete (Windows .git objects).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-pwt-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# production-wiring-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() =>
            Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() =>
            Git(RepoPath, "rev-parse", "HEAD").Trim();

        public bool BranchExists(string branchName)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--verify");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add(branchName);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
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
    // Fixture plan helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a linear A → B plan committed inside <paramref name="repoPath"/> with
    /// <c>maxParallelism: 2</c> so the factory (after wiring) activates worktree mode.
    /// Each task action writes a unique <c>src/{id}.cs</c> file into
    /// <c>$env:GUARDRAILS_WORKSPACE</c> (the segment worktree root) for Integrate to commit.
    /// </summary>
    private static string CreateFixturePlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        // mergeOnSuccess: false is EXPLICIT (#340 flipped the default to ON): this test proves the factory
        // wires WORKTREE ISOLATION — the plan branch holds the work and the user's branch is UNCHANGED
        // during the run — which is a NON-delivery assertion, so it opts out of the now-default delivery.
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "mergeOnSuccess": false
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        WriteTask(planDir, "01-task-a", []);
        WriteTask(planDir, "02-task-b", ["01-task-a"]);

        // Commit the plan definition into the repo so the plan branch is forked from a real HEAD.
        TempGitRepo.Git(repoPath, "add", ".");
        TempGitRepo.Git(repoPath, "commit", "-m", "Add fixture plan");

        return planDir;
    }

    private static void WriteTask(string planDir, string taskId, string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "production wiring fixture {{taskId}}",
              "dependsOn": {{dependsJson}}
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
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Test — factory must wire GitWorktreeProvider when driving a real plan
    // METHOD NAME IS EXACT — the scenarios-present guardrail greps for it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the production <see cref="SchedulerFactory.Create"/> path end-to-end against a
    /// committed fixture plan at <c>maxParallelism = 2</c>. Asserts three worktree-mode outputs:
    /// <list type="number">
    ///   <item>A <c>guardrails/plan</c> branch exists in the repo after the run.</item>
    ///   <item>That branch carries ≥ 2 commits with <c>Guardrails-Task:</c> trailers
    ///         (one per task). This proves the factory wired the worktree provider so each
    ///         task's output was committed to an isolated segment and integrated.</item>
    ///   <item>The user's original branch HEAD is UNCHANGED — the plan branch is isolated.</item>
    /// </list>
    /// All three fail on current code because <see cref="SchedulerFactory.Create"/> passes
    /// <c>worktreeProvider: null</c> (default) to <see cref="Scheduler"/>, so no plan branch is
    /// ever created. RED until task 36 wires the factory.
    /// </summary>
    [Fact]
    public async Task Factory_RunsWorktreeMode_OnCommittedFixturePlan()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFixturePlan(repo.RepoPath);

        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors,
            "Fixture plan must load without errors: " + string.Join("\n", load.Diagnostics));

        // Drive the REAL production factory — no manual provider injection.
        // A test that injects the provider would pass even with an unwired factory and is FORBIDDEN.
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!,
            new ProcessRunner(),
            new PathExecutableProbe(),
            IRunObserver.Null);

        RunReport report = await scheduler.RunAsync(
            load.Plan!, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "Fixture plan must succeed end-to-end: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // ── Assertion 1: plan branch must exist ───────────────────────────────────────────────
        // SchedulerFactory must wire GitWorktreeProvider → CreateIntegration creates the branch.
        // Current: no worktreeProvider → branch never created → assertion fails → RED.
        string planName = Path.GetFileName(planDir);
        string planBranch = $"guardrails/{planName}";
        Assert.True(repo.BranchExists(planBranch),
            $"Plan branch '{planBranch}' must exist after a worktree-mode run. " +
            "Defect: SchedulerFactory.Create does not wire GitWorktreeProvider (task 36).");

        // ── Assertion 2: ≥ 2 Guardrails-Task: trailers on the plan branch ────────────────────
        // Each settled task must leave a Guardrails-Task: trailer on the plan branch.
        // Current: no plan branch → log empty → assertion fails → RED (already caught by assertion 1).
        string planLog = TempGitRepo.Git(repo.RepoPath,
            "log", "--first-parent", "--format=%B", planBranch);
        int trailerCount = planLog.Split('\n')
            .Count(l => l.StartsWith("Guardrails-Task:", StringComparison.Ordinal));
        Assert.True(trailerCount >= 2,
            $"Plan branch must have ≥ 2 Guardrails-Task: trailers (one per fixture task). " +
            $"Found {trailerCount}. Defect: factory does not wire worktree integration.");

        // ── Assertion 3: user branch HEAD unchanged ───────────────────────────────────────────
        // Plan branch isolates all task commits; the user's branch must not advance during the run
        // (mergeOnSuccess is EXPLICITLY false in the fixture plan — #340 flipped the default to ON, so
        // this NON-delivery isolation assertion opts out).
        string finalHead = repo.HeadSha();
        Assert.True(initialHead == finalHead,
            "User branch HEAD must be unchanged after a plan-branch-isolated run. " +
            "Defect: factory does not create a plan branch, so all commits land on the user branch.");
        Assert.True(originalBranch == repo.CurrentBranch(),
            "User must remain on the original branch throughout the run.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Composition-root: factory must wire an AiMergeWorker into the Scheduler in worktree mode.
    // Regression for defect #120-followup (AI-merge worker dead from the CLI). Method name greppable.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Defect #120-followup composition-root gap: <see cref="SchedulerFactory.Create"/> constructed
    /// the worktree provider and re-verifier but passed NO <c>aiMergeWorker</c> to the Scheduler, so
    /// <c>Scheduler.SettleAsync</c>'s <c>_aiMergeWorker != null &amp;&amp; …</c> short-circuited to
    /// needs-human on every conflict — the AI-merge worker was dead from the CLI.
    ///
    /// This test drives the REAL production factory at <c>maxParallelism = 2</c> against a plan that
    /// declares a <c>promptRunners</c> block, and asserts via reflection that the resulting Scheduler
    /// holds a non-null <c>_aiMergeWorker</c>. The contrast case (serial, <c>maxParallelism = 1</c>)
    /// asserts the worker is null — proving the wiring is conditional on worktree mode, not always-on.
    /// </summary>
    [Fact]
    public void Factory_WiresAiMergeWorker_InWorktreeMode()
    {
        using var repo = new TempGitRepo();

        // ── Worktree mode (parallel + git + promptRunners) → non-null AI-merge worker ───────────
        string parallelPlanDir = CreateMergeRunnerPlan(repo.RepoPath, maxParallelism: 2);
        PlanLoadResult parallelLoad = new PlanLoader().Load(parallelPlanDir);
        Assert.NotNull(parallelLoad.Plan);
        Assert.False(parallelLoad.HasErrors,
            "Worktree-mode fixture plan must load cleanly: " + string.Join("\n", parallelLoad.Diagnostics));

        Scheduler parallelScheduler = SchedulerFactory.Create(
            parallelLoad.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        object? worktreeWorker = AiMergeWorkerField().GetValue(parallelScheduler);
        Assert.NotNull(worktreeWorker);
        Assert.IsType<AiMergeWorker>(worktreeWorker);

        // ── Serial mode (maxParallelism = 1) → null AI-merge worker (no worktree mode) ──────────
        string serialPlanDir = CreateMergeRunnerPlan(repo.RepoPath, maxParallelism: 1, folder: "serial-plan");
        PlanLoadResult serialLoad = new PlanLoader().Load(serialPlanDir);
        Assert.NotNull(serialLoad.Plan);
        Assert.False(serialLoad.HasErrors,
            "Serial-mode fixture plan must load cleanly: " + string.Join("\n", serialLoad.Diagnostics));

        Scheduler serialScheduler = SchedulerFactory.Create(
            serialLoad.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        Assert.Null(AiMergeWorkerField().GetValue(serialScheduler));
    }

    private static FieldInfo AiMergeWorkerField()
    {
        FieldInfo? field = typeof(Scheduler).GetField(
            "_aiMergeWorker", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Composition-root: factory must wire a NeedsHumanTriage into the TaskExecutor whenever a
    // prompt runner is available — in BOTH serial and worktree mode (the triage is NOT
    // worktree-specific). Regression for the third #120-class "built but not injected" gap
    // (NeedsHumanTriage dead from the CLI). Method name greppable.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Defect #120-class composition-root gap, generalized for #269: <see cref="SchedulerFactory.Create"/>
    /// must construct the <see cref="Overwatch"/> (which SUBSUMES the shipped needs-human triage as its
    /// §9.2.1 terminal case), else the <c>TaskExecutor</c>'s <c>_overwatch is not null</c> guard
    /// short-circuits and BOTH the advisory triage AND the active supervisor are dead from the CLI.
    ///
    /// This drives the REAL production factory and asserts via reflection that the resulting
    /// Scheduler's <c>TaskExecutor</c> holds a non-null <c>_overwatch</c> when the plan declares a
    /// <c>promptRunners</c> block — and crucially asserts it is non-null in SERIAL mode
    /// (<c>maxParallelism = 1</c>) too, proving the overwatcher is wired whenever a runner is available
    /// rather than being worktree-specific like the AI-merge worker. The contrast case (a script-only
    /// plan with NO <c>promptRunners</c>) asserts it is null — no runner, no overwatcher.
    /// </summary>
    [Fact]
    public void Factory_WiresOverwatch_WhenRunnerAvailable()
    {
        using var repo = new TempGitRepo();

        // ── Serial mode WITH a prompt runner → non-null overwatch (NOT worktree-specific) ───────
        string serialPlanDir = CreateMergeRunnerPlan(repo.RepoPath, maxParallelism: 1, folder: "triage-serial-plan");
        PlanLoadResult serialLoad = new PlanLoader().Load(serialPlanDir);
        Assert.NotNull(serialLoad.Plan);
        Assert.False(serialLoad.HasErrors,
            "Serial-mode overwatch fixture plan must load cleanly: " + string.Join("\n", serialLoad.Diagnostics));

        Scheduler serialScheduler = SchedulerFactory.Create(
            serialLoad.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        object? serialOverwatch = OverwatchField().GetValue(ExecutorOf(serialScheduler));
        Assert.NotNull(serialOverwatch);
        Assert.IsType<Overwatch>(serialOverwatch);

        // ── Worktree mode WITH a prompt runner → non-null overwatch as well ─────────────────────
        string parallelPlanDir = CreateMergeRunnerPlan(repo.RepoPath, maxParallelism: 2, folder: "triage-parallel-plan");
        PlanLoadResult parallelLoad = new PlanLoader().Load(parallelPlanDir);
        Assert.NotNull(parallelLoad.Plan);
        Assert.False(parallelLoad.HasErrors,
            "Worktree-mode overwatch fixture plan must load cleanly: " + string.Join("\n", parallelLoad.Diagnostics));

        Scheduler parallelScheduler = SchedulerFactory.Create(
            parallelLoad.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        object? parallelOverwatch = OverwatchField().GetValue(ExecutorOf(parallelScheduler));
        Assert.NotNull(parallelOverwatch);
        Assert.IsType<Overwatch>(parallelOverwatch);

        // ── Script-only plan (NO promptRunners) → null overwatch (no runner, no supervisor) ─────
        string scriptOnlyPlanDir = CreateFixturePlan(repo.RepoPath);
        PlanLoadResult scriptOnlyLoad = new PlanLoader().Load(scriptOnlyPlanDir);
        Assert.NotNull(scriptOnlyLoad.Plan);
        Assert.False(scriptOnlyLoad.HasErrors,
            "Script-only fixture plan must load cleanly: " + string.Join("\n", scriptOnlyLoad.Diagnostics));

        Scheduler scriptOnlyScheduler = SchedulerFactory.Create(
            scriptOnlyLoad.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        Assert.Null(OverwatchField().GetValue(ExecutorOf(scriptOnlyScheduler)));
    }

    /// <summary>The Scheduler's concrete <see cref="TaskExecutor"/> (stored as <c>ITaskExecutor</c>).</summary>
    private static object ExecutorOf(Scheduler scheduler)
    {
        FieldInfo? field = typeof(Scheduler).GetField(
            "_executor", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        object? executor = field!.GetValue(scheduler);
        Assert.NotNull(executor);
        Assert.IsType<TaskExecutor>(executor);
        return executor!;
    }

    private static FieldInfo OverwatchField()
    {
        FieldInfo? field = typeof(TaskExecutor).GetField(
            "_overwatch", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!;
    }

    /// <summary>
    /// NIT-2: the production factory (no interaction argument) must wire the overwatcher's confirmation seam
    /// to <see cref="IOverwatchInteraction.NonInteractive"/> — the fail-safe that makes v1 grants unreachable
    /// in production (an approve can never come from a non-interactive seam). This pins the posture so a
    /// future refactor that silently threads an interactive seam is caught.
    /// </summary>
    [Fact]
    public void Factory_WiresNonInteractiveOverwatchSeam_ByDefault()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateMergeRunnerPlan(repo.RepoPath, maxParallelism: 1, folder: "overwatch-seam-plan");
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);

        object? overwatch = OverwatchField().GetValue(ExecutorOf(scheduler));
        Assert.NotNull(overwatch);

        FieldInfo? interactionField = typeof(Overwatch).GetField(
            "_interaction", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(interactionField);
        object? interaction = interactionField!.GetValue(overwatch);

        // Same singleton the production default resolves to — never an interactive seam.
        Assert.Same(IOverwatchInteraction.NonInteractive, interaction);
    }

    /// <summary>
    /// A single-task plan committed in <paramref name="repoPath"/> that declares a
    /// <c>promptRunners</c> block, so <see cref="SchedulerFactory"/> can resolve a merge-profile
    /// runner. The task body is irrelevant — this test inspects wiring, it does not run the plan.
    /// </summary>
    private static string CreateMergeRunnerPlan(string repoPath, int maxParallelism, string folder = "merge-plan")
    {
        string planDir = Path.Combine(repoPath, folder);
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": {{maxParallelism}},
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" }
              }
            }
            """);

        WriteTask(planDir, "01-task-a", []);

        TempGitRepo.Git(repoPath, "add", ".");
        TempGitRepo.Git(repoPath, "commit", "-m", $"Add {folder} fixture");
        return planDir;
    }
}
