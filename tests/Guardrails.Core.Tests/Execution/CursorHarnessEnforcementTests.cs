using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// #764 round 2 — the harness-side checks that compensate for a <c>cursor</c> block running with
/// <c>--force</c> and no containment hook (SSOT §9.9), plus the two shared-parser/classifier changes the
/// runner leans on:
/// <list type="bullet">
/// <item>D6: a cursor block is never chosen for an ADVISORY profile, by name or by default fallback, and the
/// CLI line that says so;</item>
/// <item>D7: a verdict file planted before a prompt judge runs is deleted, so a judge that writes nothing
/// fails honestly (runner-agnostic);</item>
/// <item>D8: an action on an uncontained writer that edits its own task definition fails and settles;</item>
/// <item>D4 / D10: Cursor's camelCase usage parses cache-inclusive, and Claude's spend-limit text is
/// Transient in the shared classifier.</item>
/// </list>
/// </summary>
public sealed class CursorHarnessEnforcementTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr-cursor-harness-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Win => OperatingSystem.IsWindows();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    // ── D4 / D10: shared parser and classifier ───────────────────────────────────────────────────────

    [Fact]
    public void Parser_ReadsCursorCamelCaseUsage_CacheInclusive()
    {
        ClaudeResult result = ClaudeStreamParser.ParseAll(
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"inputTokens":21522,"outputTokens":159,"cacheReadTokens":38400,"cacheWriteTokens":7}}""");

        Assert.Equal(21522 + 38400 + 7, result.Usage!.InputTokens);
        Assert.Equal(159, result.Usage.OutputTokens);
        Assert.Null(result.CostUsd);
        Assert.Null(result.NumTurns);
    }

    /// <summary>Claude's snake_case block wins outright: the camelCase fallback is consulted only when it is absent.</summary>
    [Fact]
    public void Parser_SnakeCaseUsage_IsUnchanged_AndNeverMixedWithCamelCase()
    {
        ClaudeResult result = ClaudeStreamParser.ParseAll(
            """{"type":"result","is_error":false,"result":"ok","usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":3,"inputTokens":999999}}""");

        Assert.Equal(15, result.Usage!.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
    }

    /// <summary>
    /// D10 — a deliberate CLAUDE behavior change: "You've hit your individual spend limit" (Claude Code's
    /// quota text, #764) used to classify Error and burn every retry in seconds. Transient pauses without
    /// consuming the budget and then settles needs-human "re-run later" when the pause budget runs out.
    /// </summary>
    [Theory]
    [InlineData("You've hit your individual spend limit")]
    [InlineData("Error: You've hit your individual spend limit. Contact your admin to raise it.")]
    public void SpendLimit_IsTransient_InTheSharedClassifier(string text) =>
        Assert.Equal(PromptFailureKind.Transient, ClaudeSignalClassifier.Classify(text));

    // ── D6: never an advisory runner ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AdvisoryProfiles_NeverResolveToACursorDefault_ButActionProfilesDo()
    {
        RunConfig config = Config(("cursor", PromptRunnerKind.Cursor));
        PromptRunnerRegistry registry = PromptRunnerRegistry.Build(config, block => new NamedRunner(block.Name));

        Assert.Null(SchedulerFactory.ResolveReservedRunner(registry, "overwatch", PromptRole.Advisory));
        Assert.Null(SchedulerFactory.ResolveReservedRunner(registry, "ai-triage", PromptRole.Advisory));
        Assert.Equal("cursor", SchedulerFactory.ResolveReservedRunner(registry, "ai-merge", PromptRole.Action)?.Name);
        Assert.Equal("cursor", SchedulerFactory.ResolveReservedRunner(registry, "breakdown", PromptRole.Action)?.Name);
    }

    [Fact]
    public void AnAdvisoryProfileDeclaredAsCursor_IsOff_AndDoesNotFallThroughToAnotherBlock()
    {
        RunConfig config = Config(("claude", PromptRunnerKind.Claude), ("overwatch", PromptRunnerKind.Cursor));
        PromptRunnerRegistry registry = PromptRunnerRegistry.Build(config, block => new NamedRunner(block.Name));

        Assert.Null(SchedulerFactory.ResolveReservedRunner(registry, "overwatch", PromptRole.Advisory));
        Assert.Equal("claude", SchedulerFactory.ResolveReservedRunner(registry, "ai-triage", PromptRole.Advisory)?.Name);
    }

    [Fact]
    public void AClaudeDefault_KeepsEveryAdvisoryProfile()
    {
        RunConfig config = Config(("claude", PromptRunnerKind.Claude), ("cursor", PromptRunnerKind.Cursor));
        PromptRunnerRegistry registry = PromptRunnerRegistry.Build(config, block => new NamedRunner(block.Name));

        Assert.Equal("claude", SchedulerFactory.ResolveReservedRunner(registry, "overwatch", PromptRole.Advisory)?.Name);
        Assert.Empty(SchedulerFactory.WithheldAdvisoryProfiles(config));
    }

    [Fact]
    public void WithheldAdvisoryProfiles_NamesEveryFeatureThatIsOff_AndWhy()
    {
        IReadOnlyList<string> lines = SchedulerFactory.WithheldAdvisoryProfiles(Config(("cursor", PromptRunnerKind.Cursor)));

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("'overwatch' is OFF for this run", StringComparison.Ordinal)
                                    && l.Contains("criticality judge", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("'ai-triage' is OFF for this run", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Contains("kind 'cursor'", l, StringComparison.Ordinal));
    }

    // ── D7: a planted verdict never grades a judge that wrote nothing ────────────────────────────────

    /// <summary>
    /// The attack, concretely: before the prompt judge runs, a passing verdict sits at BOTH predictable paths
    /// (the final one in the log dir, the staged one in the workspace). The judge writes nothing. Before
    /// #764 round 2 the staged plant was promoted, or the final one read, and the guardrail PASSED.
    /// </summary>
    [Fact]
    public async Task PlantedVerdict_IsDeleted_SoAJudgeThatWritesNothing_Fails()
    {
        (GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) = GuardrailFixture(new SilentJudge());
        string finalPath = Path.Combine(logDir, "guardrail-01-check.verdict.json");
        string stagedPath = Path.Combine(plan.Workspace, ".guardrails-agent-io", task.Id, "attempt-1", "guardrail-01-check.verdict.json");
        Plant(finalPath);
        Plant(stagedPath);

        GuardrailRunResult result = await RunGuardrailsAsync(runner, task, plan, logDir);

        Assert.False(result.Results.Single().Passed, "a planted verdict graded a judge that wrote nothing");
    }

    /// <summary>The positive control: the same fixture passes when the judge DOES write its verdict.</summary>
    [Fact]
    public async Task AJudgeThatWritesItsVerdict_StillPasses_AfterTheClear()
    {
        (GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) = GuardrailFixture(new PassingJudge());
        Plant(Path.Combine(logDir, "guardrail-01-check.verdict.json"));

        GuardrailRunResult result = await RunGuardrailsAsync(runner, task, plan, logDir);

        Assert.True(result.Results.Single().Passed);
    }

    // ── D8: an uncontained writer may not edit its own task definition ───────────────────────────────

    [Fact]
    public async Task CursorAction_ThatEditsItsOwnGuardrail_FailsAndSettles_NamingTheFile()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);
        var runner = new DefinitionEditingRunner(Path.Combine(plan.Tasks.Single().Directory, "guardrails", CheckFileName));

        RunReport report = await RunSerialAsync(plan, runner);

        TaskResult result = report.Tasks.Single();
        Assert.Equal(TaskOutcome.NeedsHuman, result.Outcome);
        Assert.Contains("modified the task's own definition", result.Summary, StringComparison.Ordinal);
        Assert.Contains($"guardrails/{CheckFileName} (modified)", result.Summary, StringComparison.Ordinal);
        Assert.Equal(1, runner.Calls); // settled, not retried against the rewritten guardrail
    }

    /// <summary>
    /// Scoped to UNCONTAINED writers: the identical edit by a Claude-kind action is not judged by this check
    /// (Claude's own containment story applies), so the attempt proceeds to its guardrails as before.
    /// </summary>
    [Fact]
    public async Task ClaudeAction_ThatEditsTheSameFile_IsNotTouchedByTheTamperCheck()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Claude);
        var runner = new DefinitionEditingRunner(Path.Combine(plan.Tasks.Single().Directory, "guardrails", CheckFileName));

        RunReport report = await RunSerialAsync(plan, runner);

        Assert.DoesNotContain("modified the task's own definition", report.Tasks.Single().Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CursorAction_ThatLeavesTheDefinitionAlone_RunsItsGuardrailsNormally()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);

        RunReport report = await RunSerialAsync(plan, new DefinitionEditingRunner(pathToEdit: null));

        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single().Outcome);
    }

    // ── #767 / #773: a runner-configuration failure settles, and refusals reach the feedback ──────────

    /// <summary>
    /// A RunnerConfiguration failure (the admin's Run-Everything refusal, or every shell call refused) settles
    /// the task needs-human on the FIRST attempt — with two retries in the budget, the runner is called once.
    /// The feedback carries the runner's summary (the remedy) and every refusal with its reason.
    /// </summary>
    [Fact]
    public async Task RunnerConfigurationFailure_SettlesNeedsHuman_WithoutSpendingRetries()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);
        var runner = new ScriptedRunner(new PromptResult
        {
            Completed = false,
            IsError = true,
            FailureKind = PromptFailureKind.RunnerConfiguration,
            Summary = "cursor reported success, but EVERY shell command it attempted was refused — set approvalMode",
            RefusedCommands = ["git status"],
            BlockedWritePaths = ["git status"],
            RefusedToolCalls = [new ToolRefusal("shell", "git status", "refused by Cursor approval policy")]
        });

        RunReport report = await RunSerialAsync(plan, runner);

        TaskResult result = report.Tasks.Single();
        Assert.Equal(TaskOutcome.NeedsHuman, result.Outcome);
        Assert.Equal(1, runner.Calls);
        Assert.Contains("EVERY shell command it attempted was refused", result.Summary, StringComparison.Ordinal);
        Assert.Contains("not retried", result.Summary, StringComparison.Ordinal);

        string feedback = File.ReadAllText(SingleFeedback(plan));
        Assert.Contains("the prompt runner's configuration cannot do this work", feedback, StringComparison.Ordinal);
        Assert.Contains("- shell `git status` — refused by Cursor approval policy", feedback, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: an ordinary action Error with the same budget is retried (the runner is called 3 times) —
    /// so the single call above is the RunnerConfiguration decision, not a fixture that never retries.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryActionError_IsStillRetried()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);
        var runner = new ScriptedRunner(new PromptResult
        {
            Completed = false,
            IsError = true,
            FailureKind = PromptFailureKind.Error,
            Summary = "cursor exited 1"
        });

        RunReport report = await RunSerialAsync(plan, runner);

        Assert.Equal(3, runner.Calls);
        Assert.NotEqual(TaskOutcome.Succeeded, report.Tasks.Single().Outcome);
    }

    /// <summary>
    /// A MIXED session completes and its guardrails decide; when they FAIL, the retry's feedback names each tool
    /// call the runner refused, with its reason, even though none of them repeated.
    /// </summary>
    [Fact]
    public async Task CompletedActionWithRefusals_WhoseGuardrailFails_NamesTheRefusalsInFeedback()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor, checkPasses: false);
        var runner = new ScriptedRunner(new PromptResult
        {
            Completed = true,
            IsError = false,
            Summary = "cursor completed; 1 tool call(s) refused by Cursor: shell `rm -rf bin` — Hook blocked",
            RefusedCommands = ["rm -rf bin"],
            BlockedWritePaths = ["rm -rf bin"],
            RefusedToolCalls = [new ToolRefusal("shell", "rm -rf bin", "Hook blocked with message: destructive command")]
        });

        await RunSerialAsync(plan, runner);

        string firstFeedback = Directory
            .GetFiles(Path.Combine(plan.PlanDirectory), "feedback.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();
        string feedback = File.ReadAllText(firstFeedback);
        Assert.Contains("## Tool calls the runner refused this attempt", feedback, StringComparison.Ordinal);
        Assert.Contains("- shell `rm -rf bin` — Hook blocked with message: destructive command", feedback, StringComparison.Ordinal);
    }

    /// <summary>The same completed-with-refusals action whose guardrail PASSES goes green (#534/#708 parity).</summary>
    [Fact]
    public async Task CompletedActionWithRefusals_WhoseGuardrailPasses_Succeeds()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);
        var runner = new ScriptedRunner(new PromptResult
        {
            Completed = true,
            IsError = false,
            Summary = "cursor completed; 1 tool call(s) refused by Cursor",
            RefusedCommands = ["rm -rf bin"],
            BlockedWritePaths = ["rm -rf bin"],
            RefusedToolCalls = [new ToolRefusal("shell", "rm -rf bin", "Hook blocked")]
        });

        RunReport report = await RunSerialAsync(plan, runner);

        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single().Outcome);
    }

    // ── PR #775 W1: every shell call refused in an ACTION is outcome-aware ───────────────────────────

    private static PromptResult AllShellRefusedAction() => new()
    {
        Completed = true,
        IsError = false,
        Summary = "cursor completed, but EVERY shell command it attempted was refused (1 shell call(s), none ran)",
        AllShellRefused = true,
        RunnerConfigurationRemedy =
            "EVERY shell command it attempted was refused (1 shell call(s), none ran); 1 tool call(s) refused by " +
            "Cursor: shell `dotnet test` — refused by Cursor approval policy — Cursor's auto-review classifier " +
            "(or a hook Cursor loaded — see the reasons above) refused every one.",
        RefusedCommands = ["dotnet test"],
        BlockedWritePaths = ["dotnet test"],
        RefusedToolCalls = [new ToolRefusal("shell", "dotnet test", "refused by Cursor approval policy")]
    };

    /// <summary>The edits were right: the guardrails run, pass, and the task is green — with the refusal named.</summary>
    [Fact]
    public async Task AllShellRefusedAction_WhoseGuardrailsPass_Succeeds_NamingTheRefusal()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor);
        var runner = new ScriptedRunner(AllShellRefusedAction());

        RunReport report = await RunSerialAsync(plan, runner);

        TaskResult result = report.Tasks.Single();
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, runner.Calls);
        Assert.Contains("1 tool call(s) refused by the runner: shell `dotnet test`", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guardrails run and FAIL: needs-human at once with the remedy (two retries were in the budget; the runner
    /// is called once), because the next attempt would run under the same approval policy.
    /// </summary>
    [Fact]
    public async Task AllShellRefusedAction_WhoseGuardrailFails_SettlesNeedsHuman_WithTheRemedy_WithoutRetrying()
    {
        PlanDefinition plan = WritePlan(PromptRunnerKind.Cursor, checkPasses: false);
        var runner = new ScriptedRunner(AllShellRefusedAction());

        RunReport report = await RunSerialAsync(plan, runner);

        TaskResult result = report.Tasks.Single();
        Assert.Equal(TaskOutcome.NeedsHuman, result.Outcome);
        Assert.Equal(1, runner.Calls);
        Assert.Contains("not retried", result.Summary, StringComparison.Ordinal);
        Assert.Contains("auto-review classifier", result.Summary, StringComparison.Ordinal);

        string feedback = File.ReadAllText(SingleFeedback(plan));
        Assert.Contains("its runner could run no shell command", feedback, StringComparison.Ordinal);
        Assert.Contains("- shell `dotnet test` — refused by Cursor approval policy", feedback, StringComparison.Ordinal);
    }

    // ── PR #775 B1: a judge that could not run fails closed, whatever verdict it wrote ──────────────

    [Fact]
    public async Task JudgeWhoseEveryShellCallWasRefused_FailsClosed_DespiteAPassVerdict()
    {
        var judge = new VerdictWritingJudge(new PromptResult
        {
            Completed = false,
            IsError = true,
            FailureKind = PromptFailureKind.RunnerConfiguration,
            AllShellRefused = true,
            Summary = "cursor reported success, but EVERY shell command it attempted was refused",
            RefusedToolCalls = [new ToolRefusal("shell", "dotnet test", "refused by Cursor approval policy")]
        });
        (GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) = GuardrailFixture(judge);

        GuardrailRunResult result = await RunGuardrailsAsync(runner, task, plan, logDir);

        GuardrailResult verdict = result.Results.Single();
        Assert.False(verdict.Passed, "a judge that could run none of its checks certified the work");
        Assert.StartsWith("judge could not run: cursor reported success, but EVERY shell command", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("shell `dotnet test`", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>The control: the same pass verdict from a judge that ran passes — and names any refusal it had.</summary>
    [Fact]
    public async Task JudgeThatRan_WithAPassVerdict_Passes_AndNamesItsRefusals()
    {
        var judge = new VerdictWritingJudge(new PromptResult
        {
            Completed = true,
            IsError = false,
            Summary = "cursor completed",
            RefusedToolCalls = [new ToolRefusal("shell", "rm -rf bin", "Hook blocked")]
        });
        (GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) = GuardrailFixture(judge);

        GuardrailRunResult result = await RunGuardrailsAsync(runner, task, plan, logDir);

        GuardrailResult verdict = result.Results.Single();
        Assert.True(verdict.Passed);
        Assert.Equal("passed; 1 tool call(s) refused by the runner: shell `rm -rf bin` — Hook blocked", verdict.Reason);
    }

    [Fact]
    public async Task JudgeThatRan_WithNoRefusals_Passes_WithNoReason()
    {
        var judge = new VerdictWritingJudge(new PromptResult { Completed = true, IsError = false, Summary = "cursor completed" });
        (GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) = GuardrailFixture(judge);

        GuardrailRunResult result = await RunGuardrailsAsync(runner, task, plan, logDir);

        Assert.True(result.Results.Single().Passed);
        Assert.Null(result.Results.Single().Reason);
    }

    /// <summary>A judge that writes a PASSING verdict to its staged path and returns the given result.</summary>
    private sealed class VerdictWritingJudge(PromptResult result) : IPromptRunner
    {
        public string Name => "judge";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            File.WriteAllText(invocation.Environment["GUARDRAILS_VERDICT_OUT"], """{ "pass": true }""");
            return Task.FromResult(result);
        }
    }

    private static string SingleFeedback(PlanDefinition plan) =>
        Assert.Single(Directory.GetFiles(plan.PlanDirectory, "feedback.md", SearchOption.AllDirectories));

    /// <summary>A prompt runner that returns the same result on every call, counting the calls.</summary>
    private sealed class ScriptedRunner(PromptResult result) : IPromptRunner
    {
        public int Calls { get; private set; }

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private static string CheckFileName => Win ? "01-check.ps1" : "01-check.sh";

    private static RunConfig Config(params (string Name, PromptRunnerKind Kind)[] blocks) => new()
    {
        Version = 1,
        DefaultPromptRunner = blocks[0].Name,
        PromptRunnerNames = new HashSet<string>(blocks.Select(b => b.Name), StringComparer.Ordinal),
        PromptRunners = blocks.ToDictionary(
            b => b.Name,
            b => new PromptRunnerConfig { Name = b.Name, Command = b.Name, Settings = new PromptRunnerSettings(), Kind = b.Kind },
            StringComparer.Ordinal)
    };

    private static void Plant(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "pass": true, "reason": "planted" }""");
    }

    private (GuardrailRunner Runner, TaskNode Task, PlanDefinition Plan, string LogDir) GuardrailFixture(IPromptRunner judge)
    {
        const string taskId = "01-impl";
        string taskDir = Path.Combine(_root, "tasks", taskId);
        string promptPath = Path.Combine(taskDir, "guardrails", "01-check.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        File.WriteAllText(promptPath, "Check the work.\n");

        var task = new TaskNode
        {
            Id = taskId,
            Directory = taskDir,
            Description = "implement",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = [new GuardrailDefinition { Name = "01-check", Path = promptPath, Kind = ActionKind.Prompt }]
        };

        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = Config(("judge", PromptRunnerKind.Claude)),
            Tasks = [task]
        };

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => judge);
        var runner = new GuardrailRunner(
            plan, IRunObserver.Null,
            new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe())),
            new PromptExecutionSupport(registry),
            (_, _) => TimeSpan.FromMinutes(5));

        return (runner, task, plan, Path.Combine(_root, "logs", "run", taskId, "attempt-1"));
    }

    private static Task<GuardrailRunResult> RunGuardrailsAsync(GuardrailRunner runner, TaskNode task, PlanDefinition plan, string logDir) =>
        runner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct);

    private PlanDefinition WritePlan(PromptRunnerKind kind, bool checkPasses = true)
    {
        string planDir = Path.Combine(_root, "plan");
        string kindKey = kind == PromptRunnerKind.Cursor ? "\"kind\": \"cursor\", " : "";
        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultTimeoutSeconds": 60,
              "defaultRetries": 2,
              "promptRunners": { "default": "stub", "stub": { {{kindKey}}"command": "stub" } }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "tamper fixture", "dependsOn": [], "writeScope": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        string check = Path.Combine(taskDir, "guardrails", CheckFileName);
        string exitCode = checkPasses ? "0" : "1";
        Write(check, Win ? $"exit {exitCode}\n" : $"#!/usr/bin/env bash\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(check,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    private static async Task<RunReport> RunSerialAsync(PlanDefinition plan, IPromptRunner runner)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
            stateManager, journal, IRunObserver.Null, registry);
        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        return await scheduler.RunAsync(plan, Ct);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class NamedRunner(string name) : IPromptRunner
    {
        public string Name => name;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("resolution-only fixture");
    }

    /// <summary>A judge that completes and writes NO verdict — the case a planted file would otherwise grade.</summary>
    private sealed class SilentJudge : IPromptRunner
    {
        public string Name => "judge";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "judge completed" });
    }

    /// <summary>A judge that writes a passing verdict to the staged path it was handed.</summary>
    private sealed class PassingJudge : IPromptRunner
    {
        public string Name => "judge";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            File.WriteAllText(invocation.Environment["GUARDRAILS_VERDICT_OUT"], """{ "pass": true }""");
            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "judge completed" });
        }
    }

    /// <summary>An action that completes, optionally appending to one of its own definition files first.</summary>
    private sealed class DefinitionEditingRunner(string? pathToEdit) : IPromptRunner
    {
        public int Calls { get; private set; }

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            if (pathToEdit is not null)
            {
                File.AppendAllText(pathToEdit, "\n# loosened by the agent\n");
            }

            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "done" });
        }
    }
}
