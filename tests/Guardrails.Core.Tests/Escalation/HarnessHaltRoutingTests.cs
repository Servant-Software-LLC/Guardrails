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

    /// <summary>The NON-answerable gate a harness halt is filed under (delta review W2-b).</summary>
    private const string HardBlockerGate = "hard-blocker";

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
    [InlineData("structural-wall")]
    [InlineData("task-preflight")]
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
        // The delta review's W2-b: a harness halt is NOT answerable, so it is filed under a gate no answer
        // file and no pick surface can bind. Filing it under `needs-human` invited a firstmate answer into a
        // re-run that cannot succeed — no answer widens a writeScope or grants a blocked path.
        Assert.Contains(decisions, d => d.Decision == DecisionTokens.Escalated && d.Gate == HardBlockerGate);
        Assert.DoesNotContain(decisions, d => d.Decision == DecisionTokens.Escalated && d.Gate == "needs-human");
    }

    /// <summary>
    /// The delta review's W2-a: SEVEN harness producers set NEITHER structured field, and each reached the
    /// dispatch, matched no branch, and vanished — no judge call, no escalation, no <c>decisions[]</c> entry.
    /// A run ended on a blocker with nothing recorded anywhere. These rows are the ones a real producer can
    /// build here; each must escalate through the default-safe branch on the OUTCOME alone.
    /// </summary>
    [Theory]
    [InlineData("task-preflight")] // AttemptJournaler.TaskPreflightFailed — neither field
    [InlineData("cost-cap")]       // Scheduler.CostCapHaltFor — neither field, and the task never launched
    public async Task ANeitherFieldHarnessHalt_StillEscalates_OnTheOutcomeAlone(string halt)
    {
        // Not vacuous: assert the producer really does carry neither field, so this row proves the DEFAULT
        // branch and not some signal set elsewhere. If a later change gives one of these a HardBlocker, this
        // assertion fails loudly rather than letting the row silently stop testing the default.
        TaskResult produced = halt == "cost-cap" ? CostCapHaltShape() : HarnessHaltFromItsRealProducer(halt);
        Assert.Null(produced.NeedsHumanQuestion);
        Assert.Null(produced.HardBlocker);

        (RunJournal journal, CountingJudgeRunner judge, StubExecutor executor) = halt == "cost-cap"
            ? await RunAutonomouslyAsync(HarnessHaltFromItsRealProducer("task-preflight"), costCapHit: true)
            : await RunAutonomouslyAsync(produced);

        IReadOnlyList<DecisionEntry> decisions = journal.Document.Decisions ?? [];
        Assert.Equal(0, judge.Invocations);
        Assert.DoesNotContain(decisions, d => d.Decision == DecisionTokens.ProceededBestGuess);
        Assert.Contains(decisions, d => d.Decision == DecisionTokens.Escalated && d.Gate == HardBlockerGate);
        // The cost cap halts BEFORE the task is launched, so its executor is never asked at all.
        Assert.Equal(halt == "cost-cap" ? 0 : 1, executor.Calls);
    }

    [Fact]
    public void StructuralWallHalt_CarriesThePermissionWallSignal_NotAnEmptyResult()
    {
        // The #325/#329 structural-wall halt is the SAME wall class the #86/#104 site already routes, so it
        // carries the same signal rather than falling to the generic default. Pinned on the producer itself:
        // once the default branch exists, routing alone can no longer tell the two apart.
        TaskResult result = HarnessHaltFromItsRealProducer("structural-wall");

        Assert.Null(result.NeedsHumanQuestion);
        GateSignal blocker = Assert.IsType<GateSignal>(result.HardBlocker);
        Assert.Equal(GateSignalKind.PermissionWall, blocker.Kind);
        Assert.Equal(GateClass.HardBlockerPermanent, GateClassifier.Classify(blocker));
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
                new PermissionWallDecision(
                    Halt: true, StructuralPaths: [], RepeatedPaths: ["src/Locked.cs"],
                    RepeatedCommands: [])).Result,
            "no-route" => journaler.NoRoute(
                task, attemptNumber: 1, DateTimeOffset.UtcNow, RelativeLogDir, logDir, provenance: null,
                reason: "no candidate block serves the 'hard' tier, at it or above it").Result,
            "write-scope-gap" => WriteScopeGapHalt(journaler, task, logDir),
            "structural-wall" => journaler.StructuralWallHalt(
                task, attemptNumber: 1, DateTimeOffset.UtcNow, RelativeLogDir, logDir, Action(),
                AttemptOutcome.GuardrailFailed,
                summary: "guardrail(s) failed: 01-ok — needs human; a .claude/ write was blocked this attempt",
                feedback: "a guardrail failed and a .claude/ write was blocked",
                guardrailResults: [],
                failedGuardrails: [new FailedGuardrail { Name = "01-ok", Reason = "assertion failed" }],
                wall: new PermissionWallDecision(
                    Halt: true, StructuralPaths: [".claude/skills/x/SKILL.md"], RepeatedPaths: [],
                    RepeatedCommands: [])).Result,
            "task-preflight" => journaler.TaskPreflightFailed(
                task, attemptNumber: 1, DateTimeOffset.UtcNow, RelativeLogDir, logDir,
                failedChecks: [new FailedGuardrail { Name = "01-upstream-materialized", Reason = "src/Api.cs absent" }]).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, "unknown harness halt")
        };
    }

    /// <summary>
    /// The cost-cap halt's shape as <c>Scheduler.CostCapHaltFor</c> builds it — a bare needs-human result with
    /// NEITHER structured field. Used only to assert that shape; the ROUTING row drives the real Scheduler
    /// producer via <see cref="RunAutonomouslyAsync"/>'s <c>costCapHit</c>, which never launches the task.
    /// </summary>
    private static TaskResult CostCapHaltShape() => new()
    {
        TaskId = TaskId,
        Outcome = TaskOutcome.NeedsHuman,
        Summary = "cost cap reached: cumulative journaled cost has reached the configured maxCostUsd ($1); task not launched."
    };

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
        TaskResult haltedResult, bool costCapHit = false)
    {
        string planDir = Path.Combine(_root, "plan-" + Guid.NewGuid().ToString("N"));
        var autonomy = new AutonomyConfig { EscalationThreshold = EscalationThreshold.High };
        PlanDefinition basePlan = Plan(planDir, NewTask(planDir));
        PlanDefinition plan = basePlan with
        {
            Config = basePlan.Config with
            {
                AutonomyPolicy = AutonomyPolicy.Auto,
                Autonomy = autonomy,
                // A POSITIVE cap, tripped below by real journaled spend — a zero/negative cap is rejected at
                // load (GR2012), so a run can never actually reach the dispatch carrying one.
                MaxCostUsd = costCapHit ? 1m : null
            }
        };
        new StateManager(plan.PlanDirectory).Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        if (costCapHit)
        {
            // Spend recorded by an EARLIER unit of this run, exactly as a paid attempt records it: the cap is
            // reached before this task is dispatched, so Scheduler.CostCapHaltFor halts it un-launched.
            journal.AddOverheadCost(5m);
        }

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
