using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.Escalation;

/// <summary>
/// How an autonomous run routes a needs-human stop the HARNESS decided (#707 review W2). Three harness halts
/// carry the same <c>needs human: </c> summary prefix as an agent's own question: the permission wall (#86/#104),
/// the no-route settle (#201 §6.2) and the write-scope-gap halt (#707). The Scheduler recognized "the agent asked"
/// by that prefix, so all three were routed as judgment calls. The criticality judge ran; below the threshold its
/// best-guess re-drove the task with a fresh budget, and the recorded <c>proceeded-best-guess</c> turned delivery
/// off. That defeated each halt in exactly the autonomous mode plan 40 ran in.
///
/// <para>Each halt's <see cref="TaskResult"/> here is built by its REAL producer on <see cref="AttemptJournaler"/>,
/// never assembled by the test. It is handed to a real <see cref="Scheduler"/> wired with a
/// <see cref="FileEscalationSink"/> and a <see cref="CriticalityJudge"/> whose runner answers LOW, below the
/// <c>high</c> dial. A consulted judge therefore always proceeds on a best-guess, and none of these halts may
/// consult it.</para>
/// </summary>
public sealed class HarnessHaltRoutingTests : IDisposable
{
    private const string TaskId = "01-implement";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-halt-routing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }

    [Theory]
    [InlineData("permission-wall")]
    [InlineData("no-route")]
    [InlineData("write-scope-gap")]
    public async Task AHarnessHalt_IsAHardBlocker_ItNeverReachesTheJudge_NorProceedsOnABestGuess(string halt)
    {
        TaskResult result = HarnessHaltFromItsRealProducer(halt);

        (RunJournal journal, CountingJudgeRunner judge, StubExecutor executor) = await RunAutonomouslyAsync(result);

        IReadOnlyList<DecisionEntry> decisions = journal.Document.Decisions ?? [];
        Assert.Equal(0, judge.Invocations);
        Assert.DoesNotContain(decisions, d => d.Decision == DecisionTokens.ProceededBestGuess);
        Assert.Equal(1, executor.Calls); // no best-guess re-drive with a fresh budget
        // Not vacuous: the stop DID reach the classify-then-act dispatch, and escalated as a blocker.
        Assert.Contains(decisions, d => d.Decision == DecisionTokens.Escalated && d.Subject == TaskId);
    }

    [Fact]
    public async Task AnAgentsOwnNeedsHuman_StillReachesTheJudge_AndProceedsOnItsBestGuess()
    {
        // CONTROL: the agent's own question is the judgment call the dial exists for, and must stay one.
        TaskResult result = Journaler(out TaskNode task, out string logDir).NeedsHuman(
            task, attemptNumber: 1, DateTimeOffset.UtcNow, RelativeLogDir, logDir, Action(),
            question: "which accent color should the banner use?", options: []).Result;

        (RunJournal journal, CountingJudgeRunner judge, _) = await RunAutonomouslyAsync(result);

        Assert.True(judge.Invocations >= 1, "the agent's own question must be assessed by the criticality judge");
        Assert.Contains(journal.Document.Decisions ?? [], d => d.Decision == DecisionTokens.ProceededBestGuess);
    }

    // ── the producers ────────────────────────────────────────────────────────────────────────────

    private TaskResult HarnessHaltFromItsRealProducer(string halt)
    {
        AttemptJournaler journaler = Journaler(out TaskNode task, out string logDir);
        return halt switch
        {
            "permission-wall" => journaler.PermissionWall(
                task, attemptNumber: 2, DateTimeOffset.UtcNow, RelativeLogDir, logDir, Action(),
                new PermissionWallDecision(Halt: true, StructuralPaths: [], RepeatedPaths: ["src/Locked.cs"])).Result,
            "no-route" => journaler.NoRoute(
                task, attemptNumber: 1, DateTimeOffset.UtcNow, RelativeLogDir, logDir, provenance: null,
                reason: "no candidate block serves the 'hard' tier, at it or above it").Result,
            "write-scope-gap" => WriteScopeGapHalt(journaler, task, logDir),
            _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, "unknown harness halt")
        };
    }

    private static TaskResult WriteScopeGapHalt(AttemptJournaler journaler, TaskNode task, string logDir)
    {
        var scopeCheck = new WriteScopeCheckResult
        {
            Passed = false,
            Scope = ["src/Impl.cs"],
            OffendingPaths = [new WriteScopeOffense { Path = "src/Stub.cs", Status = 'M' }],
            InScopePaths = []
        };
        var gap = new WriteScopeGap(["src/Stub.cs"], new Dictionary<string, string>());
        return journaler.WriteScopeGapHalt(
            task, attemptNumber: 2, DateTimeOffset.UtcNow, RelativeLogDir, logDir, Action(),
            RetryPolicy.ForWriteScopeGapHalt(task, 2, scopeCheck, gap),
            RetryPolicy.WriteScopeGapSummary(task, gap)).Result;
    }

    /// <summary>A journaler over its OWN throwaway plan dir, so producing a result never touches the run under test.</summary>
    private AttemptJournaler Journaler(out TaskNode task, out string logDir)
    {
        string producerDir = Path.Combine(_root, "producer-" + Guid.NewGuid().ToString("N"));
        task = NewTask(producerDir);
        PlanDefinition plan = Plan(producerDir, task);
        var state = new StateManager(plan.PlanDirectory);
        state.Initialize();
        logDir = Path.Combine(producerDir, "logs", "attempt");
        Directory.CreateDirectory(logDir);
        return new AttemptJournaler(state, RunJournal.LoadOrCreate(plan), IRunObserver.Null);
    }

    private const string RelativeLogDir = "logs/attempt";

    private static ActionRun Action() => ActionRun.FromScript(
        new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "", TimedOut = false, Duration = TimeSpan.Zero },
        needsHuman: null);

    // ── the autonomous run ───────────────────────────────────────────────────────────────────────

    private async Task<(RunJournal Journal, CountingJudgeRunner Judge, StubExecutor Executor)> RunAutonomouslyAsync(
        TaskResult haltedResult)
    {
        string planDir = Path.Combine(_root, "plan-" + Guid.NewGuid().ToString("N"));
        var autonomy = new AutonomyConfig { EscalationThreshold = EscalationThreshold.High };
        PlanDefinition basePlan = Plan(planDir, NewTask(planDir));
        PlanDefinition plan = basePlan with
        {
            Config = basePlan.Config with { AutonomyPolicy = AutonomyPolicy.Auto, Autonomy = autonomy }
        };
        new StateManager(plan.PlanDirectory).Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var judge = new CountingJudgeRunner();
        var executor = new StubExecutor(haltedResult);
        var scheduler = new Scheduler(plan, executor, journal,
            observer: IRunObserver.Null,
            maxParallelism: 1,
            escalationSink: new FileEscalationSink(Path.Combine(planDir, "logs"), journal, IRunObserver.Null, "high"),
            criticalityJudge: new CriticalityJudge(judge, autonomy));

        await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);
        return (journal, judge, executor);
    }

    private static TaskNode NewTask(string planDir) => new()
    {
        Id = TaskId,
        Directory = Path.Combine(planDir, "tasks", TaskId),
        Description = "a task whose attempt stopped at a needs-human gate",
        Action = new ActionDefinition { Path = Path.Combine(planDir, "tasks", TaskId, "action.prompt.md"), Kind = ActionKind.Prompt },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-ok",
                Path = Path.Combine(planDir, "tasks", TaskId, "guardrails", "01-ok.sh"),
                Kind = ActionKind.Script
            }
        ],
        WriteScope = ["src/Impl.cs"]
    };

    private static PlanDefinition Plan(string planDir, TaskNode task)
    {
        Directory.CreateDirectory(planDir);
        return new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };
    }

    /// <summary>Hands the Scheduler the halted result every time, and counts how often it was asked (a re-drive asks twice).</summary>
    private sealed class StubExecutor(TaskResult result) : ITaskExecutor
    {
        public int Calls { get; private set; }

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>The overwatch-profile runner the judge assesses through: always LOW (below the `high` dial), counted.</summary>
    private sealed class CountingJudgeRunner : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "{\"criticality\":\"low\",\"confidence\":\"high\",\"bestGuess\":\"proceed with the default\","
                             + "\"rationale\":\"reversible\"}",
                CostUsd = 0m,
                Summary = "assessed"
            });
        }
    }
}
