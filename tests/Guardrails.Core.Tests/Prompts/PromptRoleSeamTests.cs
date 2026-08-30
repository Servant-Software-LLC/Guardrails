using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// Plan 28 §3.4 — the role seam. Task 00 landed <c>PromptInvocation.Role</c> as <c>required</c> and set
/// <c>Role = PromptRole.Action</c> at every one of the seven construction sites, DELIBERATELY including the
/// four where that is wrong (§3.4's table: only <see cref="ActionRunner"/>, <see cref="WaveBreakdownInvoker"/>
/// and <see cref="AiMergeResolver"/> are actually <c>Action</c>; <see cref="GuardrailRunner"/> is
/// <c>Guardrail</c>; <see cref="Overwatch"/>, <see cref="NeedsHumanTriage"/> and <see cref="CriticalityJudge"/>
/// are <c>Advisory</c>).
///
/// <para>Each test below drives the REAL producer class and captures the <see cref="PromptInvocation"/> it
/// built via a fake <see cref="IPromptRunner"/> that records its argument — never by re-reading the field the
/// producer itself reads (SSOT §9 "pinned by CONSTRUCTION, not by reflection", §3.5). Against task 00's tree
/// this yields the discriminator plan §3.4 calls out: the three <c>Action</c> sites PASS unchanged, and the
/// four others FAIL — proof the tests are bound to the real code path rather than vacuously green. Task
/// <c>02-assign-roles-at-seven-sites</c> turns the four failures green; nothing here is "fixed" in the
/// meantime.</para>
/// </summary>
public sealed class PromptRoleSeamTests : IDisposable
{
    private const string TestRunnerName = "test-runner";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-role-seam-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PromptRoleSeamTests() => Directory.CreateDirectory(_root);

    // ── shared fakes ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fake <see cref="IPromptRunner"/> that records the <see cref="PromptInvocation"/> it was called
    /// with — the honest pin the plan calls for, never an echo of the field under test. Every producer
    /// exercised here tolerates a bland "completed, no verdict" reply without throwing, so one canned
    /// result serves all seven sites.
    /// </summary>
    private sealed class CapturingRunner : IPromptRunner
    {
        public PromptInvocation? Seen { get; private set; }

        public string Name => TestRunnerName;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "fake result" });
        }
    }

    /// <summary>A minimal <see cref="ISchedulerJournal"/> — <see cref="AiMergeResolver"/> only ever calls the default no-op members here.</summary>
    private sealed class NoOpSchedulerJournal : ISchedulerJournal
    {
        public Guardrails.Core.Journal.TaskStatus StatusOf(string taskId) => Guardrails.Core.Journal.TaskStatus.Pending;

        public void MarkBlocked(string taskId)
        {
        }
    }

    // ── shared plan/task fixture (ActionRunner / GuardrailRunner) ────────────────────────────────

    private static RunConfig BuildRunConfig() => new()
    {
        Version = 1,
        DefaultPromptRunner = TestRunnerName,
        PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { TestRunnerName },
        PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
        {
            [TestRunnerName] = new PromptRunnerConfig
            {
                Name = TestRunnerName,
                Command = TestRunnerName,
                Settings = new PromptRunnerSettings()
            }
        }
    };

    private static PlanDefinition BuildPlan(string root, TaskNode task) => new()
    {
        PlanDirectory = root,
        Workspace = root,
        Config = BuildRunConfig(),
        Tasks = [task]
    };

    // ── ActionRunner.cs:185 — Action (the task action itself) ────────────────────────────────────

    [Fact]
    public async Task ActionRunner_PassesActionRole()
    {
        string taskDir = Path.Combine(_root, "action", "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);
        string promptPath = Path.Combine(taskDir, "action.prompt.md");
        File.WriteAllText(promptPath, "Do the work.\n");

        var task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition { Path = promptPath, Kind = ActionKind.Prompt },
            Guardrails = []
        };
        PlanDefinition plan = BuildPlan(Path.Combine(_root, "action"), task);

        var runner = new CapturingRunner();
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var promptSupport = new PromptExecutionSupport(registry);
        var journal = RunJournal.LoadOrCreate(plan);
        var graph = new DependencyGraph(plan.Tasks);
        var tasksById = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var dependencyContext = new DependencyContextBuilder(plan, journal, graph, tasksById);
        var scriptRunner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        var actionRunner = new ActionRunner(
            plan, scriptRunner, promptSupport, dependencyContext, (_, _) => TimeSpan.FromMinutes(5));

        string logDir = Path.Combine(plan.PlanDirectory, "logs", "run", task.Id, "attempt-1");
        await actionRunner.RunAsync(
            task,
            attemptNumber: 1,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            fragmentOutPath: Path.Combine(logDir, "fragment.json"),
            previousFeedbackPath: null,
            logDir: logDir,
            timeoutMultiplier: 1.0,
            stagingDir: null,
            maxTurnsMultiplier: 1.0,
            route: null,
            cancellationToken: Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Action, runner.Seen.Role);
    }

    // ── WaveBreakdownInvoker.cs:178 — Action (authors a task folder, the `breakdown` profile) ────

    [Fact]
    public async Task WaveBreakdownInvoker_PassesActionRole()
    {
        var runner = new CapturingRunner();
        var invoker = new WaveBreakdownInvoker(runner);

        var plan = new BreakdownInvocationPlan
        {
            Prompt = "Break down this wave.",
            ComposedPromptPath = Path.Combine(_root, "wave", "composed-prompt.md"),
            ComposedPromptBytes = 0,
            StreamLogPath = Path.Combine(_root, "wave", "claude-stream.jsonl"),
            TranscriptLogPath = Path.Combine(_root, "wave", "transcript.md"),
            MaxTurns = 10
        };

        await invoker.InvokeCoreAsync(
            plan,
            workingDirectory: _root,
            planDirectory: _root,
            additionalReadDirectory: null,
            chargeCost: null,
            Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Action, runner.Seen.Role);
    }

    // ── AiMergeResolver.cs:126 — Action (writes GUARDRAILS_MERGE_OUT, the `ai-merge` profile) ─────

    [Fact]
    public async Task AiMergeResolver_PassesActionRole()
    {
        using var repo = new TempConflictGitRepo(_root);

        var runner = new CapturingRunner();
        var resolver = new AiMergeResolver(runner);

        await resolver.TryResolveAsync(
            repo.RepoPath, repo.ConflictBranch, planDirectory: repo.RepoPath, new NoOpSchedulerJournal(), Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Action, runner.Seen.Role);
    }

    /// <summary>
    /// A real git repo with a genuine staged merge conflict (a "UU" entry in <c>git status --porcelain</c>) —
    /// exactly the precondition <see cref="AiMergeResolver.TryResolveAsync"/> assumes its caller already
    /// established (its first attempt never runs <c>git merge</c> itself). Two branches edit the same line of
    /// the same file so <c>git merge --no-commit --no-ff</c> conflicts rather than auto-resolving.
    /// </summary>
    private sealed class TempConflictGitRepo : IDisposable
    {
        private readonly string _repoRoot;

        public string RepoPath { get; }

        public string ConflictBranch { get; } = "conflict-branch";

        public TempConflictGitRepo(string root)
        {
            _repoRoot = Path.Combine(root, "merge-repo-" + Guid.NewGuid().ToString("N"));
            RepoPath = _repoRoot;
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(Path.Combine(RepoPath, "conflict.txt"), "base\n");
            Git("add", ".");
            Git("commit", "-m", "base");
            string mainBranch = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

            Git("checkout", "-b", ConflictBranch);
            File.WriteAllText(Path.Combine(RepoPath, "conflict.txt"), "theirs\n");
            Git("commit", "-am", "theirs change");

            Git("checkout", mainBranch);
            File.WriteAllText(Path.Combine(RepoPath, "conflict.txt"), "ours\n");
            Git("commit", "-am", "ours change");

            // A genuine content conflict — git exits non-zero here, which is expected and tolerated.
            TryGit("merge", "--no-commit", "--no-ff", ConflictBranch);
        }

        private string Git(params string[] args) => RunGit(args, tolerateNonZero: false);

        private void TryGit(params string[] args) => RunGit(args, tolerateNonZero: true);

        private string RunGit(string[] args, bool tolerateNonZero)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 && !tolerateNonZero)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (cwd={RepoPath}) exited {proc.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_repoRoot))
                {
                    foreach (string f in Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(_repoRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ── GuardrailRunner.cs:222 — Guardrail (the judge) ────────────────────────────────────────────

    [Fact]
    public async Task GuardrailRunner_PassesGuardrailRole()
    {
        string taskDir = Path.Combine(_root, "guardrail", "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);
        string guardrailPromptPath = Path.Combine(taskDir, "guardrails", "01-check.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(guardrailPromptPath)!);
        File.WriteAllText(guardrailPromptPath, "Check the work.\n");

        var task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition
            {
                Path = Path.Combine(taskDir, "action.sh"),
                Kind = ActionKind.Script
            },
            Guardrails =
            [
                new GuardrailDefinition { Name = "01-check", Path = guardrailPromptPath, Kind = ActionKind.Prompt }
            ]
        };
        PlanDefinition plan = BuildPlan(Path.Combine(_root, "guardrail"), task);

        var runner = new CapturingRunner();
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var promptSupport = new PromptExecutionSupport(registry);
        var scriptRunner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        var guardrailRunner = new GuardrailRunner(
            plan, IRunObserver.Null, scriptRunner, promptSupport, (_, _) => TimeSpan.FromMinutes(5));

        string logDir = Path.Combine(plan.PlanDirectory, "logs", "run", task.Id, "attempt-1");
        await guardrailRunner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Guardrail, runner.Seen.Role);
    }

    // ── Overwatch.cs:457 — Advisory (advisory-never-gates) ───────────────────────────────────────

    [Fact]
    public async Task Overwatch_PassesAdvisoryRole()
    {
        string taskDir = Path.Combine(_root, "overwatch", "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);

        var task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = []
        };
        PlanDefinition plan = BuildPlan(Path.Combine(_root, "overwatch"), task);

        var runner = new CapturingRunner();
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        var journal = RunJournal.LoadOrCreate(plan);
        string taskLogDir = Path.Combine(plan.PlanDirectory, "logs", journal.Document.RunId, task.Id);

        await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, plan, attempt: 2, taskLogDir, journal, IRunObserver.Null, Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Advisory, runner.Seen.Role);
    }

    // ── NeedsHumanTriage.cs:91 — Advisory (advisory-never-gates) ─────────────────────────────────

    [Fact]
    public async Task NeedsHumanTriage_PassesAdvisoryRole()
    {
        string taskDir = Path.Combine(_root, "triage", "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);

        var task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = []
        };
        PlanDefinition plan = BuildPlan(Path.Combine(_root, "triage"), task);

        var runner = new CapturingRunner();
        var triage = new NeedsHumanTriage(runner);

        var journal = RunJournal.LoadOrCreate(plan);
        string taskLogDir = Path.Combine(plan.PlanDirectory, "logs", journal.Document.RunId, task.Id);

        await triage.RunAsync(task, taskLogDir, plan.PlanDirectory, plan.Workspace, journal, Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Advisory, runner.Seen.Role);
    }

    // ── CriticalityJudge.cs:325 — Advisory (target-typed `new()`, invisible to a `new PromptInvocation` grep) ──

    [Fact]
    public async Task CriticalityJudge_PassesAdvisoryRole()
    {
        var runner = new CapturingRunner();
        var judge = new CriticalityJudge(runner, new AutonomyConfig());

        var context = new CriticalityGateContext
        {
            Gate = CriticalityGate.NeedsHuman,
            Detail = "Which JSON serializer should I use?"
        };

        await judge.AssessAsync(context, Ct);

        Assert.NotNull(runner.Seen);
        Assert.Equal(PromptRole.Advisory, runner.Seen.Role);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
