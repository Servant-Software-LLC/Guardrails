using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end runs of the write-scope contract as the AGENT meets it, through the real composition root:
/// a real git repo, the real <see cref="GitWorktreeProvider"/>, the real <see cref="TaskExecutor"/> attempt
/// loop, and an in-process <see cref="IPromptRunner"/> standing in for the model (no tokens, no fake CLI
/// script). Every assertion reads an artifact a run leaves on disk — <c>composed-prompt.md</c>,
/// <c>feedback.md</c>, <c>run.json</c> — because the defects these pin were all invisible to a unit test of
/// one collaborator: plan 40's task 20 spent four attempts against a scope it was never shown (#706).
/// </summary>
public sealed class WriteScopeRunTests
{
    /// <summary>The lead-in line the violation feedback's allowed-path list follows (#706).</summary>
    private const string AllowedPathsLead = "This task's writeScope allows changes ONLY to:";

    private const string WriteScopeHeading = "## Write scope (harness-enforced)";

    // ── #706: the agent is shown the scope it is judged by ──────────────────────────────────────

    [Fact]
    public async Task Worktree_ComposedPrompt_ListsTheEnforcedScope_IncludingStagingDestinations_Issue706()
    {
        // The section must be generated from the ENFORCED array — after the implicit stagingOutputs
        // destinations are folded in — not from task.json's writeScope alone. A staging task is the case
        // that tells the two apart: its `.claude/` destination is writable without being declared.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0,
            new TaskSpec("01-skill", ["src/Skill.cs"], StagingTo: ".claude/skills/demo/"));
        var agent = new ScriptedAgent((_, _, invocation) =>
            WriteFile(invocation.Environment["GUARDRAILS_STAGING_DIR"], "skill/SKILL.md", "staged"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-skill", 1), "composed-prompt.md"));
        Assert.Equal(
            ["src/Skill.cs", ".claude/skills/demo/**", ".guardrails-staging/**"],
            ListedScope(composed));
    }

    [Fact]
    public async Task Serial_ComposedPrompt_HasNoWriteScopeSection_BecauseNothingEnforcesIt_Issue706()
    {
        // DECLARED CONTROL for the gating — green before #706 and after. No write-scope check runs in
        // serial mode, so a section headed "harness-enforced" there would be a false claim.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-read", ["src/Read.cs"]));
        var agent = new ScriptedAgent((_, _, _) => { });

        RunReport report = await RunSerialAsync(planDir, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-read", 1), "composed-prompt.md"));
        Assert.Contains("## Output contract", composed); // not vacuous: this IS the composed prompt
        Assert.DoesNotContain("## Write scope", composed);
    }

    [Fact]
    public async Task Worktree_ViolationFeedback_ListsTheEnforcedAllowedPaths_Issue706()
    {
        // The retry feedback named the offense but never what was allowed. Attempt 1 writes a stray file;
        // attempt 2 writes only in scope and goes green — so this also stays a control once a REPEATED
        // offending path halts (#707): a different path on each attempt must keep retrying.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
        {
            if (call == 1)
            {
                WriteFile(invocation.WorkingDirectory, "docs/notes.md", "stray");
            }
            else
            {
                WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "implemented");
            }
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string feedback = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "feedback.md"));
        Assert.Contains("`docs/notes.md`", feedback);
        Assert.Equal(["src/Impl.cs"], BulletPathsAfter(feedback, AllowedPathsLead));
    }

    // ── fixture: plan, agent, runs ─────────────────────────────────────────────────────────────

    /// <summary>One prompt task: its id, declared writeScope, dependencies, and an optional staging destination.</summary>
    private sealed record TaskSpec(string Id, string[] WriteScope, string[]? DependsOn = null, string? StagingTo = null);

    /// <summary>
    /// Stands in for the model. <see cref="_act"/> receives the task id (read from the invocation's own
    /// <c>GUARDRAILS_TASK_ID</c>), the 1-based invocation count for THAT task, and the invocation itself —
    /// whose <see cref="PromptInvocation.WorkingDirectory"/> is the effective workspace, so a write there is
    /// exactly what an agent's file-editing tool would have produced. Writes no state fragment.
    /// </summary>
    private sealed class ScriptedAgent(Action<string, int, PromptInvocation> act) : IPromptRunner
    {
        private readonly Action<string, int, PromptInvocation> _act = act;
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public string Name => "scripted-agent";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            string taskId = invocation.Environment["GUARDRAILS_TASK_ID"];
            int call;
            lock (_calls)
            {
                call = _calls[taskId] = _calls.GetValueOrDefault(taskId) + 1;
            }

            _act(taskId, call, invocation);
            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "scripted agent done" });
        }
    }

    /// <summary>
    /// A plan of prompt tasks inside <paramref name="repoPath"/>, each guarded by one always-pass script
    /// (the write-scope check runs BEFORE guardrails, so it — not a guardrail — is what these tests observe).
    /// </summary>
    private static string WritePlan(string repoPath, int defaultRetries, params TaskSpec[] tasks)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": 1,
              "promptRunners": {
                "default": "scripted",
                "scripted": { "command": "scripted-agent", "maxTurns": 3 }
              }
            }
            """);

        foreach (TaskSpec spec in tasks)
        {
            string taskDir = Path.Combine(planDir, "tasks", spec.Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string scopeJson = string.Join(", ", spec.WriteScope.Select(p => $"\"{p}\""));
            string dependsJson = string.Join(", ", (spec.DependsOn ?? []).Select(d => $"\"{d}\""));
            string stagingJson = spec.StagingTo is null
                ? ""
                : $",\n  \"stagingOutputs\": [ {{ \"from\": \"skill/**\", \"to\": \"{spec.StagingTo}\" }} ]";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "scripted prompt task {{spec.Id}}",
                  "dependsOn": [{{dependsJson}}],
                  "writeScope": [{{scopeJson}}]{{stagingJson}}
                }
                """);
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the work inside your write scope.\n");

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), "exit 0\n");
            }
            else
            {
                string guardrail = Path.Combine(taskDir, "guardrails", "01-ok.sh");
                File.WriteAllText(guardrail, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(guardrail,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        return planDir;
    }

    private static async Task<(RunReport Report, RunJournal Journal)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, IPromptRunner agent)
    {
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent);
        var scheduler = new Scheduler(plan, executor, journal,
            worktreeProvider: new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            reVerifier: new AlwaysPassReVerifier());
        return (await scheduler.RunAsync(plan, TestContext.Current.CancellationToken), journal);
    }

    private static async Task<RunReport> RunSerialAsync(string planDir, IPromptRunner agent)
    {
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent);
        return await new Scheduler(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);
    }

    private static (TaskExecutor, RunJournal, Core.Model.PlanDefinition) BuildExecutor(string planDir, IPromptRunner agent)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Core.Model.PlanDefinition plan = load.Plan!;
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
            stateManager, journal, IRunObserver.Null, PromptRunnerRegistry.Build(plan.Config, _ => agent));
        return (executor, journal, plan);
    }

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<Core.Model.GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    // ── fixture: reading what the run left behind ──────────────────────────────────────────────

    /// <summary>The absolute log dir of <paramref name="taskId"/>'s attempt <paramref name="attempt"/>, as journaled.</summary>
    private static string AttemptDir(string planDir, string taskId, int attempt)
    {
        AttemptRecord record = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks[taskId].Attempts[attempt - 1];
        return Path.Combine(planDir, record.LogDir.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The backticked entries listed under the composed prompt's write-scope section (fails when absent).</summary>
    private static List<string> ListedScope(string composed)
    {
        int start = composed.IndexOf(WriteScopeHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a '{WriteScopeHeading}' section:\n{composed}");
        int next = composed.IndexOf("\n## ", start + WriteScopeHeading.Length, StringComparison.Ordinal);
        string section = next < 0 ? composed[start..] : composed[start..next];
        return section.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
    }

    /// <summary>The backticked paths of the bullet run that immediately follows <paramref name="lead"/>.</summary>
    private static List<string> BulletPathsAfter(string text, string lead)
    {
        int at = text.IndexOf(lead, StringComparison.Ordinal);
        Assert.True(at >= 0, $"expected '{lead}' in:\n{text}");
        return text[(at + lead.Length)..].Replace("\r\n", "\n").Split('\n')
            .SkipWhile(line => line.Length == 0)
            .TakeWhile(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wsrun-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            // Pinned so a segment worktree checks out exactly the committed bytes on every OS.
            Git(RepoPath, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# write-scope run test");
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
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0
                ? stdout
                : throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
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
}
