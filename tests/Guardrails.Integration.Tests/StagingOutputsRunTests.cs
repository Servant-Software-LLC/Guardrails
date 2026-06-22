using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end runs of the <c>stagingOutputs</c> contract (SSOT §3.5, issue #130) against real
/// scripts (PowerShell on Windows, bash elsewhere). These prove the BEHAVIOR the design promises:
/// <list type="bullet">
///   <item>the move happens AFTER the action and BEFORE guardrails — a task writing to the staging
///     dir lands its file at the real <c>.claude/</c> path, and a guardrail checking that real path
///     PASSES;</item>
///   <item>the integrated plan-branch commit carries the moved <c>.claude/</c> file and NO
///     <c>.guardrails-staging/</c> scaffolding (worktree mode);</item>
///   <item>a guardrail failure on the moved artifact rolls back — no committed <c>.claude/</c>
///     artifact survives;</item>
///   <item>the write-scope check AUTO-SCOPES the <c>to</c> destinations: a staging task whose
///     <c>writeScope</c> does NOT list its <c>.claude/</c> destination still passes, but an
///     UNDECLARED <c>.claude/</c> write (not via staging) still fails the check.</item>
/// </list>
/// Serial mode covers the user-checkout move; worktree mode covers the segment-isolated move.
/// </summary>
public sealed class StagingOutputsRunTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    // ── temp git repo (Windows-safe teardown) ───────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-stage-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# staging-outputs test");
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

        /// <summary>True when <paramref name="planBranch"/> tracks a blob at <paramref name="path"/>.</summary>
        public bool PlanBranchHasPath(string planBranch, string path)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"{planBranch}:{path}");
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
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
    /// Write a single-task staging plan. The action writes <paramref name="stagedRelative"/> (a path
    /// relative to GUARDRAILS_STAGING_DIR) with a marker; the harness moves it to the real
    /// <paramref name="to"/> path; the guardrail asserts that real path exists. When
    /// <paramref name="guardrailChecksReal"/> is false, the guardrail instead asserts a NON-existent
    /// path so the attempt fails (rollback test). <paramref name="undeclaredClaudeWrite"/> makes the
    /// action ALSO write a .claude/ path directly (bypassing staging) to test the write-scope wall.
    /// </summary>
    private static string WriteStagingPlan(
        string repoPath,
        int maxParallelism,
        string from = "skill/**",
        string stagedRelative = "skill/SKILL.md",
        string to = ".claude/skills/foo/",
        string? writeScope = null,
        bool guardrailChecksReal = true,
        string? undeclaredClaudeWrite = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": {{maxParallelism}}
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-skill");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string writeScopeJson = writeScope is null ? "" : $", \"writeScope\": [{writeScope}]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "author the foo skill",
              "dependsOn": [],
              "stagingOutputs": [ { "from": "{{from}}", "to": "{{to}}" } ]{{writeScopeJson}}
            }
            """);

        // The destination file path the guardrail verifies (the real .claude/ path the move lands).
        string movedFile = to.TrimEnd('/') + "/" + Path.GetFileName(stagedRelative);
        string checkedFile = guardrailChecksReal ? movedFile : ".claude/skills/foo/DOES-NOT-EXIST.md";

        if (Ps)
        {
            string stagedPs = stagedRelative.Replace("/", "\\");
            var action = new System.Text.StringBuilder();
            action.AppendLine("$dest = Join-Path $env:GUARDRAILS_STAGING_DIR '" + stagedPs + "'");
            action.AppendLine("New-Item -Path $dest -Force -Value 'STAGED-MARKER' | Out-Null");
            if (undeclaredClaudeWrite is not null)
            {
                string uw = undeclaredClaudeWrite.Replace("/", "\\");
                action.AppendLine("New-Item -Path (Join-Path $env:GUARDRAILS_WORKSPACE '" + uw + "') -Force -Value 'SNEAKY' | Out-Null");
            }
            action.AppendLine("exit 0");
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), action.ToString());

            string checkedPs = checkedFile.Replace("/", "\\");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-exists.ps1"),
                "if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '" + checkedPs + "')) { exit 0 } else { Write-Output 'missing'; exit 1 }\n");
        }
        else
        {
            string stagedParent = stagedRelative.Contains('/') ? stagedRelative[..stagedRelative.LastIndexOf('/')] : "";
            var action = new System.Text.StringBuilder();
            action.AppendLine("#!/usr/bin/env bash");
            if (!string.IsNullOrEmpty(stagedParent))
                action.AppendLine($"mkdir -p \"$GUARDRAILS_STAGING_DIR/{stagedParent}\"");
            action.AppendLine($"printf '%s' 'STAGED-MARKER' > \"$GUARDRAILS_STAGING_DIR/{stagedRelative}\"");
            if (undeclaredClaudeWrite is not null)
            {
                string uwParent = undeclaredClaudeWrite.Contains('/') ? undeclaredClaudeWrite[..undeclaredClaudeWrite.LastIndexOf('/')] : "";
                if (!string.IsNullOrEmpty(uwParent))
                    action.AppendLine($"mkdir -p \"$GUARDRAILS_WORKSPACE/{uwParent}\"");
                action.AppendLine($"printf '%s' 'SNEAKY' > \"$GUARDRAILS_WORKSPACE/{undeclaredClaudeWrite}\"");
            }
            action.AppendLine("exit 0");
            WriteSh(Path.Combine(taskDir, "action.sh"), action.ToString());

            WriteSh(Path.Combine(taskDir, "guardrails", "01-exists.sh"),
                "#!/usr/bin/env bash\n" +
                $"if [ -f \"$GUARDRAILS_WORKSPACE/{checkedFile}\" ]; then exit 0; else echo missing; exit 1; fi\n");
        }

        return planDir;
    }

    private static void WriteSh(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ── run helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Worktree mode: real GitWorktreeProvider rooted at the repo.</summary>
    private static async Task<(RunReport report, string planBranch)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("no prompt runners"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(load.Plan!, executor, journal,
            worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier());

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, "guardrails/plan");
    }

    /// <summary>Serial mode: the default in-process scheduler, workspace = the user checkout.</summary>
    private static async Task<RunReport> RunSerialAsync(string planDir, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, ct);
    }

    // ── tests ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Worktree_MoveLandsBeforeGuardrails_GuardrailSeesRealClaudePath_AndCommitCarriesIt()
    {
        using var repo = new TempGitRepo();
        string planDir = WriteStagingPlan(repo.RepoPath, maxParallelism: 2);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        // The guardrail (which checks the real .claude/ path) passed → the move ran BEFORE it.
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        // The integrated plan-branch commit carries the moved .claude/ file …
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/foo/SKILL.md"),
            "the moved .claude/ deliverable must be committed on the plan branch");
        // … and NO .guardrails-staging/ scaffolding.
        Assert.False(repo.PlanBranchHasPath(planBranch, ".guardrails-staging/01-skill/skill/SKILL.md"),
            "staging scaffolding must never reach a commit");
    }

    [Fact]
    public async Task Serial_MoveLandsBeforeGuardrails_GuardrailSeesRealClaudePath()
    {
        // Serial mode: no git repo required; the move lands in the plan workspace (the user checkout).
        using var repo = new TempGitRepo();
        string planDir = WriteStagingPlan(repo.RepoPath, maxParallelism: 1);

        RunReport report = await RunSerialAsync(planDir, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        // The move landed the real file in the workspace (repo root, the serial workspace).
        Assert.True(File.Exists(Path.Combine(repo.RepoPath, ".claude", "skills", "foo", "SKILL.md")));
        // The per-task staging tree was deleted (no staged file survives).
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, ".guardrails-staging", "01-skill", "skill", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(repo.RepoPath, ".guardrails-staging", "01-skill")));
    }

    [Fact]
    public async Task Worktree_GuardrailFailsOnMovedArtifact_RollsBack_NoCommittedClaudeArtifact()
    {
        using var repo = new TempGitRepo();
        // The guardrail checks a NON-existent path, so even though the move succeeds the attempt fails.
        string planDir = WriteStagingPlan(repo.RepoPath, maxParallelism: 2, guardrailChecksReal: false);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        // The moved .claude/ file lived only in the segment working tree (never committed) and was
        // wiped by the failed-attempt reset — nothing reaches the plan branch.
        Assert.False(repo.PlanBranchHasPath(planBranch, ".claude/skills/foo/SKILL.md"),
            "a guardrail failure must leave NO committed .claude/ artifact");
    }

    [Fact]
    public async Task Worktree_WriteScope_AutoScopesStagingToDestination_TaskGoesGreen()
    {
        using var repo = new TempGitRepo();
        // writeScope lists ONLY the staging prefix — NOT the .claude/ destination. The to path is
        // implicitly in-scope, so the moved .claude/ file is not flagged out-of-scope and the task
        // goes green (the design's PRIMARY option).
        string planDir = WriteStagingPlan(
            repo.RepoPath, maxParallelism: 2, writeScope: "\".guardrails-staging/**\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/foo/SKILL.md"));
    }

    [Fact]
    public async Task Worktree_WriteScope_UndeclaredClaudeWrite_StillFlaggedOutOfScope()
    {
        using var repo = new TempGitRepo();
        // The action stages its declared deliverable (to .claude/skills/foo/, implicitly in-scope) AND
        // writes an UNDECLARED .claude/ path directly via GUARDRAILS_WORKSPACE. writeScope authorizes
        // only the staging prefix, so the staged destination is auto-scoped but the undeclared
        // .claude/other.md write is NOT — the write-scope check must still flag it.
        string planDir = WriteStagingPlan(
            repo.RepoPath, maxParallelism: 2,
            writeScope: "\".guardrails-staging/**\"",
            undeclaredClaudeWrite: ".claude/sneaky/other.md");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        // The undeclared .claude/ write is out-of-scope → the attempt fails the write-scope check.
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("write-scope", task.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(repo.PlanBranchHasPath(planBranch, ".claude/sneaky/other.md"),
            "the undeclared .claude/ write must not be committed");
    }
}
