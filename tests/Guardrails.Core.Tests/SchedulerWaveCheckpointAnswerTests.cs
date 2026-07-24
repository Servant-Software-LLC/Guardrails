using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The wave-boundary half of the firstmate reply channel (issue #375, doc 12 §7.4): the <c>wave-checkpoint</c>
/// answer channel is now LIVE in production. Drives the REAL <see cref="Scheduler"/> wave loop (the #120
/// composition-root discipline — NOT a hand-injected consumer in isolation) through the JIT between-wave
/// checkpoint of a waved plan whose next wave is an unauthored (empty <c>tasks/</c>) stub with NO
/// <c>brief.md</c> — so the checkpoint ESCALATES a <c>wave-checkpoint</c> gate through the wired
/// <see cref="FileEscalationSink"/> + advisory <see cref="CriticalityJudge"/> (a FAKE overwatch runner supplies
/// the assessment) rather than auto-breaking-down. On resume, <see cref="Scheduler.TryConsumeWaveProceed"/>
/// consumes a valid <c>wave-proceed</c> answer BEFORE re-classifying.
///
/// <para>Mirrors <see cref="SchedulerReviewGateTests"/> (the Option-P authored-wave-runs shape) +
/// <see cref="SchedulerWaveExecutionTests"/> (the two-run resume shape): a real on-disk waved plan
/// (<see cref="WavePlanBuilder"/>) + a real <see cref="RunJournal"/> + <see cref="RecordingWorktreeProvider"/>
/// (no git/process) + a STUB breakdown runner that authors a valid <c>tasks/</c>. Three cases: a NON-clamped
/// (low) checkpoint under <c>proceed-unreviewed</c> consumes+runs the wave (a <c>proceed</c> answer); a CLAMPED
/// (critical) one under the SAME posture is rejected and the wave does not run (§5.2/§7.3 Blocker 1); and a
/// valid <c>hold</c> answer forces a DEFINITIVE honest-halt with NO re-classification / no new escalation.</para>
/// </summary>
public sealed class SchedulerWaveCheckpointAnswerTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";
    private const string Wave2Task = "wave-02-build/01-compile";

    // Canned advisory assessments the FAKE overwatch runner returns (parsed by CriticalityJudge). CRITICAL is
    // clamped under proceed-unreviewed; LOW (escalated only because the dial is 'low') is NOT.
    private const string AssessCritical =
        "{\"criticality\":\"critical\",\"confidence\":\"high\",\"bestGuess\":\"halt and ask a human\"," +
        "\"rationale\":\"an irreversible schema migration\"}";

    private const string AssessLow =
        "{\"criticality\":\"low\",\"confidence\":\"high\",\"bestGuess\":\"keep the existing default\"," +
        "\"rationale\":\"a reversible cosmetic default\"}";

    private static readonly JsonSerializerOptions AnswerJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── 1. proceed-unreviewed + a NON-clamped (low) checkpoint: a proceed answer breaks down + RUNS the wave ──

    [Fact]
    public async Task ProceedUnreviewed_NonClampedCheckpoint_ProceedAnswer_OnResume_BreaksDownAndRunsWave()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithWave2Stub();
        using WavePlanBuilder _ = b;
        // Dial 'low' so an assessed-LOW wave-checkpoint escalates (answerable) — and review-gate
        // proceed-unreviewed so a consumed proceed RUNS the authored wave (Option P). LOW is NOT clamped.
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.Low,
            GateThresholds = new GateThresholds { ReviewGate = ReviewGateDecision.ProceedUnreviewed }
        };
        PlanDefinition autoPlan = WithAutonomy(plan, autonomy);

        // Run 1: wave-1 runs; the wave-2 JIT checkpoint (no brief) assesses LOW → escalates a wave-checkpoint
        // (criticality low) → honest-halt. An OPEN escalation record is written.
        var e1 = new RecordingExecutor();
        RunJournal j1 = RunJournal.LoadOrCreate(autoPlan);
        await NewScheduler(autoPlan, e1, j1, b, autonomy, AssessLow).RunAsync(autoPlan, Ct);

        string recordPath = Assert.Single(
            Directory.GetFiles(EscalationsDir(b.PlanDir, j1), "*-wave-checkpoint.json"));
        DropWaveProceedAnswer(recordPath, decision: "proceed");

        // Resume: the wired consume-point runs proceedUnreviewed=true; a LOW criticality is not clamped ⇒ the
        // proceed answer is consumed and the wave breaks down + RUNS (Option P), NOT a re-halt.
        var e2 = new RecordingExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(autoPlan);
        await NewScheduler(autoPlan, e2, j2, b, autonomy, AssessLow).RunAsync(autoPlan, Ct);

        // The freshly-authored wave RAN (its task started).
        Assert.Contains(Wave2Task, e2.Started);

        // An answer-injected decision for the wave-checkpoint + the Option-P proceeded-unreviewed decision.
        Assert.Contains(j2.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.AnswerInjected && d.Gate == "wave-checkpoint" && d.Subject == Wave2);
        Assert.Contains(j2.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.ProceededUnreviewed && d.Subject == Wave2);

        // The consumed escalation record's status flipped open → consumed (once-only, §7.6).
        Assert.Equal("consumed", ReadStatus(recordPath));
    }

    // ── 2. The clamp: proceed-unreviewed + a CRITICAL checkpoint rejects the proceed answer; wave NOT run ─────

    [Fact]
    public async Task ProceedUnreviewed_ClampedCriticalCheckpoint_ProceedAnswer_OnResume_Rejected_WaveNotRun()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithWave2Stub();
        using WavePlanBuilder _ = b;
        // The SAME proceed-unreviewed posture, but the checkpoint is assessed CRITICAL → clamped: a high/critical
        // hard call under proceed-unreviewed is NON-answerable (§5.2/§7.3 Blocker 1). Dial 'high' + review-gate
        // proceed-unreviewed is legal (no reachable-critical best-guess declared; the clamp is the backstop).
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.High,
            GateThresholds = new GateThresholds { ReviewGate = ReviewGateDecision.ProceedUnreviewed }
        };
        PlanDefinition autoPlan = WithAutonomy(plan, autonomy);

        var e1 = new RecordingExecutor();
        RunJournal j1 = RunJournal.LoadOrCreate(autoPlan);
        await NewScheduler(autoPlan, e1, j1, b, autonomy, AssessCritical).RunAsync(autoPlan, Ct);

        string recordPath = Assert.Single(
            Directory.GetFiles(EscalationsDir(b.PlanDir, j1), "*-wave-checkpoint.json"));
        DropWaveProceedAnswer(recordPath, decision: "proceed");

        // Resume: proceedUnreviewed=true is still passed, and a CRITICAL criticality IS clamped ⇒ the answer is
        // rejected and re-escalated; the wave is never broken down / run.
        var e2 = new RecordingExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(autoPlan);
        await NewScheduler(autoPlan, e2, j2, b, autonomy, AssessCritical).RunAsync(autoPlan, Ct);

        // The clamp held: the wave was NOT broken down / run.
        Assert.DoesNotContain(Wave2Task, e2.Started);

        // No answer-injected decision for the wave-checkpoint.
        Assert.DoesNotContain(j2.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.AnswerInjected && d.Subject == Wave2);

        // The answered record's status stays OPEN (re-escalated, never consumed).
        Assert.Equal("open", ReadStatus(recordPath));
    }

    // ── 3. A valid `hold` forces a DEFINITIVE honest-halt — no re-classify, no NEW escalation ────────────────

    [Fact]
    public async Task HoldAnswer_OnResume_HonestHaltsDefinitively_NoReClassify_NoNewEscalation()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlanWithWave2Stub();
        using WavePlanBuilder _ = b;
        // A NON-clamped (low) checkpoint so the answer is consumable — the point here is the `hold` decision, not
        // the clamp. review-gate proceed-unreviewed is present but irrelevant to a hold (it never runs the wave).
        var autonomy = new AutonomyConfig
        {
            EscalationThreshold = EscalationThreshold.Low,
            GateThresholds = new GateThresholds { ReviewGate = ReviewGateDecision.ProceedUnreviewed }
        };
        PlanDefinition autoPlan = WithAutonomy(plan, autonomy);

        // Run 1: wave-2 JIT checkpoint escalates (criticality low) → honest-halt. One OPEN escalation record.
        var e1 = new RecordingExecutor();
        RunJournal j1 = RunJournal.LoadOrCreate(autoPlan);
        await NewScheduler(autoPlan, e1, j1, b, autonomy, AssessLow).RunAsync(autoPlan, Ct);

        string escDir = EscalationsDir(b.PlanDir, j1);
        string recordPath = Assert.Single(Directory.GetFiles(escDir, "*-wave-checkpoint.json"));
        DropWaveProceedAnswer(recordPath, decision: "hold");

        // Resume: the wired consume-point consumes the `hold` and DEFINITIVELY honest-halts — it must NOT
        // re-classify (which could best-guess-and-proceed) and must NOT raise a new wave-checkpoint escalation.
        var e2 = new RecordingExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(autoPlan);
        RunReport report = await NewScheduler(autoPlan, e2, j2, b, autonomy, AssessLow).RunAsync(autoPlan, Ct);

        // The wave did NOT run.
        Assert.DoesNotContain(Wave2Task, e2.Started);

        // The run honest-halted at the (still-unauthored) wave-2 checkpoint.
        Assert.False(report.AllSucceeded);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Equal(Wave2, report.WaveHalt.WaveDir);

        // The hold WAS consumed (an answer-injected decision is recorded) and the record flipped to consumed…
        Assert.Contains(j2.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.AnswerInjected && d.Gate == "wave-checkpoint" && d.Subject == Wave2);
        Assert.Equal("consumed", ReadStatus(recordPath));

        // …but NO re-classification happened — exactly ONE wave-checkpoint record exists (no NEW escalation was
        // raised beyond the consumed one). This is the proof that `hold` is definitive, not a re-classify.
        Assert.Single(Directory.GetFiles(escDir, "*-wave-checkpoint.json"));

        // The wave never ran unreviewed either — no proceeded-unreviewed decision (that is the `proceed` path).
        Assert.DoesNotContain(j2.Document.Decisions ?? [],
            d => d.Decision == DecisionTokens.ProceededUnreviewed && d.Subject == Wave2);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    /// <summary>A recording fake executor: every task settles green through the Scheduler's B1 (as a real worktree run does), and every started task id is captured so a test can assert whether the unreviewed wave RAN.</summary>
    private sealed class RecordingExecutor : ITaskExecutor
    {
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
        }
    }

    /// <summary>A STUB breakdown runner that authors a VALID single-task wave-02 (task.json + action + a guardrail) and returns a canned success — NO real Claude process is spawned.</summary>
    private sealed class StubBreakdownRunner : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            string taskDir = Path.Combine(invocation.WorkingDirectory, Wave2, "tasks", "01-compile");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "compile", "writeScope": [] }""");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored wave-02",
                CostUsd = 0m,
                Summary = "breakdown authored wave-02"
            });
        }
    }

    /// <summary>A FAKE overwatch runner: returns a canned criticality assessment as the prompt result text (the CriticalityJudge parses it) — NO real prompt call.</summary>
    private sealed class FakeOverwatchRunner(string assessment) : IPromptRunner
    {
        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = assessment,
                CostUsd = 0m,
                Summary = "assessed"
            });
    }

    /// <summary>A waved plan with wave-01 authored + wave-02 an empty JIT stub (NO brief.md ⇒ the checkpoint escalates rather than auto-breaking-down).</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) WavedPlanWithWave2Stub()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.RootDir(Path.Combine(Wave2, "tasks")); // wave-02 folder present, tasks/ empty = JIT stub, no brief
        return (b, b.Load().Plan!);
    }

    /// <summary>The plan under <c>autonomyPolicy: auto</c> with the given dial/gate-threshold block engaged.</summary>
    private static PlanDefinition WithAutonomy(PlanDefinition plan, AutonomyConfig autonomy) =>
        plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto, Autonomy = autonomy } };

    private static Scheduler NewScheduler(
        PlanDefinition plan, ITaskExecutor exec, RunJournal journal, WavePlanBuilder b,
        AutonomyConfig autonomy, string overwatchAssessment) =>
        new(plan, exec, journal,
            worktreeProvider: new RecordingWorktreeProvider(),
            observer: IRunObserver.Null,
            maxParallelism: 4,
            reVerifier: null,
            breakdownInvoker: new WaveBreakdownInvoker(new StubBreakdownRunner()),
            escalationSink: new FileEscalationSink(
                Path.Combine(b.PlanDir, "logs"), journal, IRunObserver.Null,
                autonomy.EscalationThreshold.ToString().ToLowerInvariant()),
            criticalityJudge: new CriticalityJudge(new FakeOverwatchRunner(overwatchAssessment), autonomy));

    private static string EscalationsDir(string planDir, RunJournal journal) =>
        Path.Combine(planDir, "logs", journal.Document.RunId, "escalations");

    /// <summary>Drop a valid <c>wave-proceed</c> answer beside the escalation record, echoing its binding identity + captured hash VERBATIM (the legitimate out-of-band reply channel).</summary>
    private static void DropWaveProceedAnswer(string recordPath, string decision)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(recordPath));
        JsonElement root = doc.RootElement;
        JsonElement id = root.GetProperty("id");
        var answer = new AnswerFile
        {
            RunId = id.GetProperty("runId").GetString()!,
            Seq = id.GetProperty("seq").GetInt32(),
            Gate = "wave-checkpoint",
            Subject = id.GetProperty("subject").GetString()!,
            DefinitionHash = root.GetProperty("definitionHash").GetString()!,
            AnsweredBy = "integration-test-human",
            AnsweredAt = DateTimeOffset.UtcNow,
            Answer = new AnswerPayload { Kind = AnswerKinds.WaveProceed, Decision = decision }
        };
        string answerPath = recordPath[..^".json".Length] + ".answer.json";
        File.WriteAllText(answerPath, JsonSerializer.Serialize(answer, AnswerJson));
    }

    private static string? ReadStatus(string recordPath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(recordPath));
        return doc.RootElement.GetProperty("status").GetString();
    }
}
