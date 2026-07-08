using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #280 end-to-end (real Scheduler + TaskExecutor + <see cref="GitWorktreeProvider"/>, real git
/// worktrees, OS-picked <c>.ps1</c>/<c>.sh</c> scripts): a task's segment commit must contain exactly
/// its in-scope diff. A guardrail's filesystem side effects (an <c>npm ci</c> <c>node_modules</c>, a
/// build cache, an out-of-scope <c>dist/</c>) must NEVER reach the committed tree — stripped silently
/// (phase-2 scope-clean, §3.4) or excluded at staging (§5.3(D)) — and must NEVER fail the task nor
/// raise a spurious write-scope violation in a reused linear-chain worktree.
/// </summary>
public sealed class SegmentDependencyExclusionRunTests
{
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-depx-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# dep-exclusion-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public IReadOnlyList<string> PlanBranchFiles(string planName) =>
            Git(RepoPath, "ls-tree", "-r", "--name-only", $"guardrails/{planName}")
                .Replace('\\', '/')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

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
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan + task authoring (OS-picked scripts; forward slashes work in both PowerShell and bash).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string CreatePlan(string repoPath)
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
        return planDir;
    }

    /// <summary>
    /// Write one task: an action that creates <paramref name="actionFiles"/> (in-scope deliverables) and
    /// a guardrail that creates <paramref name="guardrailFiles"/> (the verifier's filesystem side
    /// effects) and exits 0. <paramref name="writeScope"/> null ⇒ the task declares none.
    /// </summary>
    private static void WriteTask(
        string planDir,
        string taskId,
        string[] dependsOn,
        string[]? writeScope,
        IReadOnlyDictionary<string, string> actionFiles,
        IReadOnlyDictionary<string, string> guardrailFiles)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        string scopeJson = writeScope is null
            ? ""
            : ",\n  \"writeScope\": [" + string.Join(", ", writeScope.Select(s => $"\"{s}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "dep-exclusion {{taskId}}",
              "dependsOn": {{dependsJson}}{{scopeJson}}
            }
            """);

        WriteScript(Path.Combine(taskDir, ScriptName("action")), actionFiles);
        WriteScript(Path.Combine(taskDir, "guardrails", ScriptName("01-check")), guardrailFiles);
    }

    private static string ScriptName(string stem) =>
        OperatingSystem.IsWindows() ? stem + ".ps1" : stem + ".sh";

    /// <summary>
    /// Emit an OS-picked script that creates each (relPath → content) file and exits 0. Content is
    /// embedded inside a single-quoted string in BOTH shells, so a single quote in the content would
    /// silently corrupt the emitted script (PowerShell parse error / bash quote break) — guard against
    /// it loudly so a future fixture author gets a clear failure, not a mysteriously empty commit.
    /// </summary>
    private static void WriteScript(string path, IReadOnlyDictionary<string, string> files)
    {
        Assert.All(files.Values, v => Assert.DoesNotContain('\'', v));
        if (OperatingSystem.IsWindows())
        {
            var lines = new List<string>();
            foreach ((string rel, string content) in files)
            {
                string dir = PosixDir(rel);
                if (dir.Length > 0)
                    lines.Add($"New-Item -ItemType Directory -Force -Path \"$env:GUARDRAILS_WORKSPACE/{dir}\" | Out-Null");
                lines.Add($"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/{rel}\" -Value '{content}'");
            }
            lines.Add("exit 0");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
        }
        else
        {
            var lines = new List<string> { "#!/usr/bin/env bash", "set -e" };
            foreach ((string rel, string content) in files)
            {
                string dir = PosixDir(rel);
                if (dir.Length > 0)
                    lines.Add($"mkdir -p \"$GUARDRAILS_WORKSPACE/{dir}\"");
                lines.Add($"printf '%s' '{content}' > \"$GUARDRAILS_WORKSPACE/{rel}\"");
            }
            lines.Add("exit 0");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static string PosixDir(string rel)
    {
        int slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel[..slash];
    }

    private static async Task<(RunReport report, RunJournal journal)> RunAsync(string planDir, string repoPath, string worktreeRoot)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in dep-exclusion tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var provider = new GitWorktreeProvider(repoPath, worktreeRoot);
        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier());

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
        return (report, journal);
    }

    private static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>();

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #280 test #1: a writeScope task whose GUARDRAIL creates a nested <c>node_modules</c> — the
    /// segment commit tree must NOT contain it, and the run is green.
    /// </summary>
    [Fact]
    public async Task WriteScopeTask_GuardrailCreatesNestedNodeModules_NotInSegmentCommit()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-vendor", [], ["dsl/src.js"],
            actionFiles: new Dictionary<string, string> { ["dsl/src.js"] = "export const ok = true;" },
            guardrailFiles: new Dictionary<string, string> { ["dsl/node_modules/ajv/dist/ajv.js"] = "module.exports={};" });

        var (report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, "The task must go green (the dep dir is excluded, not a failure).");
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("dsl/src.js", committed);
        Assert.DoesNotContain(committed, p => p.Contains("node_modules", StringComparison.Ordinal));
    }

    /// <summary>
    /// #280 test #2: a NO-writeScope task whose guardrail creates <c>node_modules</c> — the segment
    /// staging exclusion (§5.3(D)) is the safety net (phase-2 scope-clean is skipped without a scope),
    /// so it is still not committed.
    /// </summary>
    [Fact]
    public async Task NoWriteScopeTask_GuardrailCreatesNodeModules_NotCommitted()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-build", [], writeScope: null,
            actionFiles: new Dictionary<string, string> { ["app/main.js"] = "export const main = 1;" },
            guardrailFiles: new Dictionary<string, string> { ["app/node_modules/left-pad/index.js"] = "module.exports={};" });

        var (report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded);
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("app/main.js", committed);
        Assert.DoesNotContain(committed, p => p.Contains("node_modules", StringComparison.Ordinal));
    }

    /// <summary>
    /// #280 test #3: a legitimate IN-scope action output IS committed — the exclusion/strip must not
    /// over-reach and drop real deliverables.
    /// </summary>
    [Fact]
    public async Task InScopeActionOutput_IsCommitted_NoOverStrip()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-impl", [], ["src/**"],
            actionFiles: new Dictionary<string, string>
            {
                ["src/Feature.cs"] = "class Feature {}",
                ["src/Helper.cs"] = "class Helper {}"
            },
            guardrailFiles: None);

        var (report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded);
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("src/Feature.cs", committed);
        Assert.Contains("src/Helper.cs", committed);
    }

    /// <summary>
    /// #280 test #4: a reused linear-chain worktree — <c>01-vendor</c>'s guardrail creates a nested
    /// <c>node_modules</c> that survives on disk into the reused segment; <c>02-consume</c> (with a
    /// <c>writeScope</c>) must NOT get a spurious write-scope violation from the leftover (the
    /// exclusion is applied at the write-scope CHECK's own staging site too).
    /// </summary>
    [Fact]
    public async Task ReusedLinearChain_LeftoverNodeModules_NoSpuriousWriteScopeViolation()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        // 01 has no writeScope; its guardrail leaves a nested node_modules in the shared segment.
        WriteTask(planDir, "01-vendor", [], writeScope: null,
            actionFiles: new Dictionary<string, string> { ["dsl/pkg.js"] = "export const v = 1;" },
            guardrailFiles: new Dictionary<string, string> { ["dsl/node_modules/ajv/dist/ajv.js"] = "module.exports={};" });
        // 02 reuses the SAME worktree (linear chain) and declares a writeScope that does NOT include the
        // leftover node_modules — pre-fix, its git-add-all write-scope check flags node_modules 'A'.
        WriteTask(planDir, "02-consume", ["01-vendor"], ["src/**"],
            actionFiles: new Dictionary<string, string> { ["src/Consumer.cs"] = "class Consumer {}" },
            guardrailFiles: None);

        var (report, journal) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded,
            "02-consume must NOT be flagged for the leftover node_modules the 01-vendor guardrail left in " +
            "the reused segment (the exclusion is applied at the write-scope check's staging site).");
        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("02-consume"));
        TaskResult consume = Assert.Single(report.Tasks, t => t.TaskId == "02-consume");
        Assert.DoesNotContain("write-scope", consume.Summary ?? "", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("src/Consumer.cs", committed);
        Assert.DoesNotContain(committed, p => p.Contains("node_modules", StringComparison.Ordinal));
    }

    /// <summary>
    /// #280 test #5: a writeScope task whose guardrail builds an out-of-scope NON-dependency dir
    /// (<c>foo/dist/</c>) — phase-2 scope-clean STRIPS it (it is not in the reconstructable set, so the
    /// staging exclusion does not cover it) and it is not committed. Generalizes (A) beyond node_modules.
    /// </summary>
    [Fact]
    public async Task WriteScopeTask_GuardrailBuildsOutOfScopeNonDepDir_StrippedNotCommitted()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-impl", [], ["src/**"],
            actionFiles: new Dictionary<string, string> { ["src/Feature.cs"] = "class Feature {}" },
            guardrailFiles: new Dictionary<string, string> { ["foo/dist/bundle.js"] = "// out-of-scope build output" });

        var (report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, "Phase-2 strips the out-of-scope build dir; it does not fail the task.");
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("src/Feature.cs", committed);
        Assert.DoesNotContain(committed, p => p.StartsWith("foo/", StringComparison.Ordinal));
    }

    /// <summary>
    /// #280 test #6: guardrail side effects present (both an excluded <c>node_modules</c> AND a stripped
    /// out-of-scope file) — the task still goes GREEN. Phase-2 STRIPS/excludes; it never fails a passing
    /// guardrail's side effect.
    /// </summary>
    [Fact]
    public async Task GuardrailSideEffects_TaskStillGreen_StripNotFail()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-impl", [], ["src/**"],
            actionFiles: new Dictionary<string, string> { ["src/Feature.cs"] = "class Feature {}" },
            guardrailFiles: new Dictionary<string, string>
            {
                ["dsl/node_modules/ajv/dist/ajv.js"] = "module.exports={};",  // excluded (§5.3(D))
                ["generated/report.txt"] = "out-of-scope generated file"       // stripped (phase-2)
            });

        var (report, journal) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, "A passing guardrail's side effects must be cleaned, never punished.");
        TaskResult impl = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, impl.Outcome);
        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("01-impl"));
    }

    /// <summary>
    /// #280 test #8: a <c>.guardrails-agent-io/</c> residue (§9.5) left by a cleanup no-op (#266) is
    /// never committed (the exclusion set covers the harness's own scaffolding), via a full run.
    /// </summary>
    [Fact]
    public async Task AgentIoResidue_NotCommitted()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTask(planDir, "01-task", [], writeScope: null,
            actionFiles: new Dictionary<string, string> { ["out/result.txt"] = "real deliverable" },
            guardrailFiles: new Dictionary<string, string>
            {
                [".guardrails-agent-io/01-task/attempt-1/leftover.json"] = "{\"residue\":true}"
            });

        var (report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded);
        IReadOnlyList<string> committed = repo.PlanBranchFiles("plan");
        Assert.Contains("out/result.txt", committed);
        Assert.DoesNotContain(committed, p => p.Contains(".guardrails-agent-io", StringComparison.Ordinal));
    }
}
