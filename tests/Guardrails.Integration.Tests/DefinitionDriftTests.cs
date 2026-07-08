using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end definition-drift tests (issue #274 Part A, SSOT §7.2) with a REAL
/// <see cref="GitWorktreeProvider"/> (maxParallelism &gt; 1) so the full stack is exercised: the
/// <c>TaskDefinitionHash</c> stamped on the journal AND the <c>Guardrails-Task-Hash:</c> trailer, the
/// trailer-scan reconcile that recovers it, the resume drift detection, and the Tier-2 per-file
/// breakdown recovered from git (<c>git show</c> / <c>git ls-tree</c> at the task's old commit). The
/// plan folder is COMMITTED before the run so the old definition bytes are recoverable (the dogfood
/// scenario #274 was reported on); the drift edit is then a tracked-file modification in the checkout.
/// </summary>
public sealed class DefinitionDriftTests
{
    private static bool Ps => OperatingSystem.IsWindows();
    private static string GuardrailFile => Ps ? "01-check.ps1" : "01-check.sh";

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-drift-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# drift-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public void CommitAll(string message)
        {
            Git(RepoPath, "add", "-A");
            Git(RepoPath, "commit", "-m", message);
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
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    private sealed class PassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails, CancellationToken ct = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    /// <summary>Linear A → B plan under <paramref name="repoPath"/>, maxParallelism 2 (worktree mode).</summary>
    private static string CreateLinearPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
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
        WriteTask(planDir, "01-task-a", []);
        WriteTask(planDir, "02-task-b", ["01-task-a"]);
        return planDir;
    }

    private static void WriteTask(string planDir, string taskId, string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        string dependsJson = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "drift {{taskId}}", "dependsOn": {{dependsJson}} }""");

        string safe = taskId.Replace("-", "_");
        if (Ps)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{taskId}.cs\" -Force -Value 'class {safe} {{}}' | Out-Null\nexit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", GuardrailFile), "# original guardrail\nexit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath,
                "#!/usr/bin/env bash\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                $"printf 'class {safe} {{}}' > \"$GUARDRAILS_WORKSPACE/src/{taskId}.cs\"\nexit 0\n");
            MakeExec(actionPath);
            string gp = Path.Combine(taskDir, "guardrails", GuardrailFile);
            File.WriteAllText(gp, "#!/usr/bin/env bash\n# original guardrail\nexit 0\n");
            MakeExec(gp);
        }
    }

    private static void MakeExec(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static async Task<(RunReport report, RunJournal journal)> RunAsync(
        string planDir, GitWorktreeProvider provider, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("no prompt runners in drift tests"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: new PassReVerifier());
        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal);
    }

    [Fact]
    public async Task EditingSucceededGuardrail_ThenResume_HaltsWithPerFileDriftReport()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        // Commit the plan folder so the old definition bytes are recoverable at the task's commit.
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        (RunReport report1, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);
        Assert.True(report1.AllSucceeded, "phase 1 must fully succeed");
        Assert.Null(report1.DefinitionDrift);

        // Edit the already-succeeded task's guardrail (a real definition change; still exits 0).
        string guardrailPath = Path.Combine(planDir, "tasks", "01-task-a", "guardrails", GuardrailFile);
        File.WriteAllText(guardrailPath,
            (Ps ? "" : "#!/usr/bin/env bash\n") + "# edited guardrail — an extra assertion line\nexit 0\n");

        (RunReport report2, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);

        Assert.NotNull(report2.DefinitionDrift);
        DriftedTask drifted = Assert.Single(report2.DefinitionDrift!.Tasks);
        Assert.Equal("01-task-a", drifted.TaskId);

        // Tier 2: the per-file breakdown recovered the old guardrail bytes from git and named THAT file.
        Assert.Null(drifted.Note);
        ChangedDefinitionFile changed = Assert.Single(
            drifted.ChangedFiles, f => f.Path == $"guardrails/{GuardrailFile}");
        Assert.Equal("modified", changed.Change);
        Assert.True(changed.Added > 0 || changed.Removed > 0, "a ± line delta should be reported");

        // Dependents = the transitive-descendant set; the reference command + old commit are present.
        Assert.Contains("02-task-b", drifted.Dependents);
        Assert.NotNull(drifted.OldCommit);
        Assert.Contains("git diff", drifted.DiffCommand);
    }

    [Fact]
    public async Task EditingSucceededTaskJson_ThenResume_NamesTaskJson()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        (RunReport report1, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);
        Assert.True(report1.AllSucceeded);

        File.WriteAllText(Path.Combine(planDir, "tasks", "01-task-a", "task.json"),
            """{ "description": "drift 01-task-a EDITED description", "dependsOn": [] }""");

        (RunReport report2, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);

        DriftedTask drifted = Assert.Single(report2.DefinitionDrift!.Tasks);
        Assert.Equal("01-task-a", drifted.TaskId);
        Assert.Contains(drifted.ChangedFiles, f => f.Path == "task.json" && f.Change == "modified");
    }

    [Fact]
    public async Task RunStampsGuardrailsTaskHashTrailerOnThePlanBranch()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        (RunReport report, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);
        Assert.True(report.AllSucceeded);

        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";
        string log = TempGitRepo.Git(repo.RepoPath, "log", "--format=%B", planBranch);
        Assert.Contains("Guardrails-Task-Hash: sha256:", log);
    }

    [Fact]
    public async Task UnchangedPlan_Resume_NoDrift_AllGreen()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateLinearPlan(repo.RepoPath);
        repo.CommitAll("add plan");
        CancellationToken ct = TestContext.Current.CancellationToken;

        (RunReport report1, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);
        Assert.True(report1.AllSucceeded);

        // No edit: a plain resume must NOT halt on drift and must stay green (regression).
        (RunReport report2, _) = await RunAsync(planDir, new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot), ct);
        Assert.Null(report2.DefinitionDrift);
        Assert.True(report2.AllSucceeded);
        Assert.All(report2.Tasks, t => Assert.True(t.IsGreen));
    }
}
