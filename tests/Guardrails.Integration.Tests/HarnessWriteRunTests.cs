using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end runs of the <c>needsHarnessWrite</c> escape hatch (issue #191, SSOT §9) through the real
/// <see cref="TaskExecutor"/> attempt loop, using script actions (OS-picked .ps1/.sh) that write the
/// fragment key directly — no real Claude CLI needed, mirroring the <c>needsHuman</c>/staging-outputs
/// test conventions elsewhere in this project. Proves:
/// <list type="bullet">
///   <item>an in-scope request results in the harness process itself writing the file, and the task's
///     guardrails subsequently run and observe it (task goes green);</item>
///   <item>an out-of-scope request (outside the declared <c>writeScope</c>) is rejected with actionable
///     retry feedback naming the path, and the attempt fails — same shape as an existing
///     write-scope violation, eventual needs-human on budget exhaustion;</item>
///   <item>a path attempting to escape the workspace entirely is rejected regardless of writeScope.</item>
/// </list>
/// </summary>
public sealed class HarnessWriteRunTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-hwrun-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# needsHarnessWrite test");
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

    /// <summary>
    /// A single-task plan whose SCRIPT action writes a <c>needsHarnessWrite</c> fragment requesting
    /// <paramref name="requestedPath"/> with fixed content; the guardrail checks that
    /// <paramref name="guardrailChecksPath"/> exists in the workspace (defaults to the requested path,
    /// so a successful write is what makes the guardrail pass).
    /// </summary>
    private static string WriteHarnessWritePlan(
        string repoPath,
        string requestedPath,
        string? writeScope,
        string? guardrailChecksPath = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks", "01-write", "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-write");
        string writeScopeJson = writeScope is null ? "" : $", \"writeScope\": [{writeScope}]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "request a harness write",
              "dependsOn": []{{writeScopeJson}}
            }
            """);

        string checkedPath = guardrailChecksPath ?? requestedPath;

        // The fragment JSON embeds the requested path/content — write it via the STATE_OUT env var
        // directly (a script action IS allowed to write needsHarnessWrite, same fragment file
        // needsHuman uses).
        if (Ps)
        {
            string ps = "$json = '{ \"needsHarnessWrite\": { \"path\": \"" + requestedPath.Replace("\\", "\\\\") +
                        "\", \"content\": \"WRITTEN-BY-HARNESS\", \"reason\": \"runtime blocks this path\" } }'\n" +
                        "Set-Content -Path $env:GUARDRAILS_STATE_OUT -Value $json -NoNewline\n" +
                        "exit 0\n";
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), ps);

            string checkedPs = checkedPath.Replace("/", "\\");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-exists.ps1"),
                "if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '" + checkedPs + "')) { exit 0 } else { Write-Output 'missing'; exit 1 }\n");
        }
        else
        {
            string sh = "#!/usr/bin/env bash\n" +
                        "printf '%s' '{ \"needsHarnessWrite\": { \"path\": \"" + requestedPath +
                        "\", \"content\": \"WRITTEN-BY-HARNESS\", \"reason\": \"runtime blocks this path\" } }' > \"$GUARDRAILS_STATE_OUT\"\n" +
                        "exit 0\n";
            WriteSh(Path.Combine(taskDir, "action.sh"), sh);

            WriteSh(Path.Combine(taskDir, "guardrails", "01-exists.sh"),
                "#!/usr/bin/env bash\n" +
                $"if [ -f \"$GUARDRAILS_WORKSPACE/{checkedPath}\" ]; then exit 0; else echo missing; exit 1; fi\n");
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

    private static async Task<(RunReport report, string planBranch)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = Core.Prompts.PromptRunnerRegistry.Build(load.Plan!.Config,
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

    [Fact]
    public async Task Worktree_InScopeRequest_HarnessWritesFile_GuardrailsSeeIt_TaskSucceeds()
    {
        using var repo = new TempGitRepo();
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: ".claude/skills/foo/SKILL.md", writeScope: "\".claude/**\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        // The committed plan-branch carries the harness-written file — proof it reached the real
        // segment workspace (not just some scratch location) and survived integration.
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/foo/SKILL.md"),
            "the harness-written .claude/ file must be committed on the plan branch");
    }

    [Fact]
    public async Task Worktree_OutOfScopeRequest_Rejected_ActionableFeedback_EventualNeedsHuman()
    {
        using var repo = new TempGitRepo();
        // writeScope authorizes ONLY .claude/** — the requested path is deliberately outside it.
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: "src/Sneaky.cs", writeScope: "\".claude/**\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        // defaultRetries: 0 -> budget of 1 -> the single rejected attempt exhausts the budget. The
        // reported per-attempt TaskOutcome keeps the GuardrailFailed shape (same as any other
        // exhausted write-scope violation); the JOURNAL settles needs-human (asserted below) — the
        // task is non-green either way, so dependents correctly block.
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite", task.Summary);
        Assert.False(repo.PlanBranchHasPath(planBranch, "src/Sneaky.cs"),
            "a rejected needsHarnessWrite must never reach a commit");

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The retry feedback names the offending path (actionable, same tone as a normal write-scope
        // violation).
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));
        AttemptRecord attempt = Assert.Single(doc.Tasks["01-write"].Attempts);
        string feedbackPath = Path.Combine(planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md");
        Assert.True(File.Exists(feedbackPath));
        string feedback = File.ReadAllText(feedbackPath);
        Assert.Contains("src/Sneaky.cs", feedback);
    }

    [Fact]
    public async Task Worktree_WorkspaceEscapingRequest_Rejected_RegardlessOfWriteScope()
    {
        using var repo = new TempGitRepo();
        // An extremely permissive writeScope ("**") must still not let a workspace-escaping path
        // through — the escape check is independent of writeScope (issue #191).
        string escaping = OperatingSystem.IsWindows() ? "..\\..\\outside.txt" : "../../outside.txt";
        string planDir = WriteHarnessWritePlan(repo.RepoPath, requestedPath: escaping, writeScope: "\"**\"");

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // Nothing landed outside the repo tree.
        string outsidePath = Path.Combine(Path.GetDirectoryName(repo.RepoPath)!, "outside.txt");
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task Worktree_NoWriteScopeDeclared_AllowsHarnessWrite()
    {
        using var repo = new TempGitRepo();
        // No writeScope declared at all -> per the documented decision, needsHarnessWrite is allowed
        // unconditionally (mirrors "Absent => no check" for the retrospective write-scope check).
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: ".claude/skills/bar/SKILL.md", writeScope: null);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/bar/SKILL.md"));
    }
}
