using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The #269 overwatcher (SSOT §9.2, doc 11): the active AI supervisor that subsumes the terminal
/// needs-human triage and adds the eager / short-circuit / permission-wall triggers. These tests pin the
/// load-bearing v1 behavior the maintainer's adversarial pass scrutinizes:
/// <list type="bullet">
///   <item>ADVISORY-NEVER-GATES: a malformed/absent/errored diagnose = no action; the deterministic policy stands (verdict from files).</item>
///   <item>NO SANCTIONED CHANGE ⇒ NO GRANT ⇒ HONEST HALT: it can never grant "keep trying, unchanged" — that is the floor's domain.</item>
///   <item>The tier mapping onto <see cref="AutonomyPolicy"/> (halt / prompt / auto-degrades-to-prompt) + interactive apply vs honest halt.</item>
///   <item>Reporting: a <c>task</c>-boundary <c>decisions[]</c> entry + a per-fire <c>overwatch.jsonl</c> record.</item>
///   <item>Trigger determinism: EAGER at attempt ≥ 2, at most ONCE per attempt; the deterministic floor stays the floor.</item>
/// </list>
/// The diagnose runs through the <see cref="IPromptRunner"/> seam with a C# fake (no real claude).
/// </summary>
public sealed class OverwatchTests
{
    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingRunner : IPromptRunner
    {
        private readonly List<PromptInvocation> _calls = new();
        public RecordingRunner(string name) => Name = name;
        public string Name { get; }
        public IReadOnlyList<PromptInvocation> Calls => _calls;
        public string? CannedResultText { get; set; }
        public bool ShouldThrow { get; set; }
        public bool ShouldReturnError { get; set; }
        public decimal? Cost { get; set; }

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            _calls.Add(invocation);
            if (ShouldThrow)
                throw new InvalidOperationException("RecordingRunner: configured to throw (advisory test).");
            return Task.FromResult(new PromptResult
            {
                Completed = !ShouldReturnError,
                IsError = ShouldReturnError,
                ResultText = ShouldReturnError ? null : CannedResultText,
                CostUsd = Cost,
                Summary = "fake"
            });
        }
    }

    private sealed class FakeInteraction : IOverwatchInteraction
    {
        private readonly OverwatchInteractionResult _result;
        public int Calls { get; private set; }
        public FakeInteraction(OverwatchInteractionResult result) => _result = result;

        public OverwatchInteractionResult ConfirmApply(
            OverwatchProposal proposal, TaskNode task, OverwatchTrigger trigger, string sanctionedChangeSummary)
        {
            Calls++;
            return _result;
        }
    }

    private sealed class CapturingObserver : IRunObserver
    {
        private readonly List<DecisionEntry> _decisions = new();
        public IReadOnlyList<DecisionEntry> Decisions { get { lock (_decisions) { return _decisions.ToList(); } } }
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void DecisionRecorded(DecisionEntry entry) { lock (_decisions) { _decisions.Add(entry); } }
    }

    // ── Diagnose result builders ─────────────────────────────────────────────────────────────────

    private static string GuidanceProposal(string classification = "retryable") =>
        $$"""{"classification":"{{classification}}","diagnosis":"the action never emits the required token","fixes":[{"kind":"guidance","guidance":"emit the required token before exiting"}]}""";

    private static string BudgetProposal() =>
        """{"classification":"retryable","diagnosis":"needs one more attempt","fixes":[{"kind":"budget","field":"retries","value":1}]}""";

    private static string DenylistOnlyProposal(string guardrailBodyPath) =>
        $$"""{"classification":"retryable","diagnosis":"the guardrail expectation is wrong","fixes":[{"kind":"file-edit","path":{{JsonSerializer.Serialize(guardrailBodyPath)}}}]}""";

    private static string DoomedProposal() =>
        """{"classification":"doomed","diagnosis":"the requirement is self-contradictory","fixes":[]}""";

    // ── Direct-EvaluateAsync harness (real loaded plan + journal) ─────────────────────────────────

    private static (PlanDefinition Plan, RunJournal Journal, TaskNode Task, string TaskLogDir) LoadFirstTask(
        StatePlanBuilder plan, decimal? maxCostUsd = null)
    {
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        PlanDefinition planDef = load.Plan!;
        if (maxCostUsd is { } cap)
        {
            planDef = planDef with { Config = planDef.Config with { MaxCostUsd = cap } };
        }

        new StateManager(planDef.PlanDirectory).Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(planDef);
        TaskNode task = planDef.Tasks[0];
        string taskLogDir = Path.Combine(planDef.PlanDirectory, "logs", journal.Document.RunId, task.Id);
        return (planDef, journal, task, taskLogDir);
    }

    private static Overwatch BuildOverwatch(
        RecordingRunner? diagnoseRunner,
        AutonomyPolicy policy,
        OverwatchInteractionResult interaction = OverwatchInteractionResult.NonInteractive,
        RecordingRunner? triageRunner = null)
    {
        NeedsHumanTriage? triage = triageRunner is null ? null : new NeedsHumanTriage(triageRunner);
        return new Overwatch(diagnoseRunner, triage, policy, new FakeInteraction(interaction));
    }

    private static string OverwatchJsonl(string taskLogDir) => Path.Combine(taskLogDir, "overwatch.jsonl");

    // ── ADVISORY-NEVER-GATES ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advisory_NoDiagnoseRunner_ReturnsNoAction_NoRecords()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        Overwatch overwatch = BuildOverwatch(diagnoseRunner: null, AutonomyPolicy.Prompt);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Empty(observer.Decisions);
        Assert.False(File.Exists(OverwatchJsonl(taskLogDir)));
    }

    [Fact]
    public async Task Advisory_MalformedProposal_ReturnsNoAction_NoRecords()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = "not json at all" };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Single(runner.Calls);                 // it DID try (advisory), but the malformed body is ignored
        Assert.Empty(observer.Decisions);            // no decision recorded — deterministic policy stands
        Assert.False(File.Exists(OverwatchJsonl(taskLogDir)));
    }

    [Fact]
    public async Task Advisory_ThrownDiagnose_ReturnsNoAction_DoesNotThrow()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { ShouldThrow = true };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Empty(observer.Decisions);
    }

    [Fact]
    public async Task Advisory_ErrorResult_ReturnsNoAction()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { ShouldReturnError = true };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Empty(observer.Decisions);
    }

    // ── TIER MAPPING onto autonomyPolicy ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HaltPolicy_AlwaysHalts_EvenWithGuidanceAndApprove()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        // policy=halt but interaction would approve — the policy wins: no grant.
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Halt, OverwatchInteractionResult.Apply);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);
        DecisionEntry entry = Assert.Single(observer.Decisions);
        Assert.Equal("task", entry.Boundary);
        Assert.Equal("halted", entry.Decision);
        Assert.Equal("halt", entry.Policy);
    }

    [Fact]
    public async Task Doomed_Halts_EvenWithGuidanceAndApprove()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = DoomedProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Apply);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);
        Assert.Equal("halted", Assert.Single(observer.Decisions).Decision);
    }

    // ── NO SANCTIONED CHANGE ⇒ NO GRANT ⇒ HONEST HALT ────────────────────────────────────────────

    [Fact]
    public async Task NoSanctionedChange_DenylistOnlyProposal_Halts_NeverConsultsInteraction()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();

        // The judge proposes ONLY a guardrail-body edit (the verdict surface — denylist). No allowlist lever.
        string guardrailBody = Path.Combine(task.Directory, "guardrails", StatePlanBuilder.GuardrailFileName);
        var runner = new RecordingRunner("overwatch") { CannedResultText = DenylistOnlyProposal(guardrailBody) };
        var interaction = new FakeInteraction(OverwatchInteractionResult.Apply);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt, interaction);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);                 // floor stands
        Assert.Equal(0, interaction.Calls);                               // never even offered — no sanctioned change
        DecisionEntry entry = Assert.Single(observer.Decisions);
        Assert.Equal("halted", entry.Decision);

        // overwatch.jsonl records the proposed fix + the DENYLIST authority the classifier assigned it.
        string jsonl = await File.ReadAllTextAsync(OverwatchJsonl(taskLogDir), TestContext.Current.CancellationToken);
        Assert.Contains("\"authority\":\"denylist\"", jsonl);
        Assert.Contains("\"kind\":\"file-edit\"", jsonl);
    }

    [Fact]
    public async Task SanctionedGuidance_NonInteractive_Halts()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.NonInteractive);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);
        Assert.Equal("halted", Assert.Single(observer.Decisions).Decision);
    }

    [Fact]
    public async Task SanctionedGuidance_Declined_Halts()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Declined);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);
        Assert.Equal("prompted-declined", Assert.Single(observer.Decisions).Decision);
    }

    // ── GRANT (a sanctioned change) at a floor boundary ──────────────────────────────────────────

    [Fact]
    public async Task SanctionedGuidance_Approved_Grants_WithGuidanceInjection()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Apply);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Grant, d.Kind);
        Assert.False(string.IsNullOrEmpty(d.GuidanceInjection));
        Assert.Equal("prompted-approved", Assert.Single(observer.Decisions).Decision);
    }

    [Fact]
    public async Task SanctionedBudget_Approved_Grants_WithExtraRetries()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = BudgetProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Apply);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.Grant, d.Kind);
        Assert.Equal(1, d.ExtraRetries);
    }

    [Fact]
    public async Task Auto_DegradesToPrompt_InV1()
    {
        // Under `auto` (a v2 value for the overwatcher's own fixes), v1 treats it like `prompt`: it PROPOSES
        // (consults the interaction) rather than silently auto-applying. A NonInteractive context ⇒ honest halt.
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        var interaction = new FakeInteraction(OverwatchInteractionResult.NonInteractive);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Auto, interaction);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(1, interaction.Calls);            // it PROPOSED (did not silently auto-apply)
        Assert.Equal(OverwatchDecisionKind.Halt, d.Kind);
    }

    // ── EAGER (non-floor) is ADVISORY: never gates a task the floor would keep retrying ───────────

    [Fact]
    public async Task Eager_NonGrant_IsAdvisory_NotHalt()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.NonInteractive);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        // Eager + non-grant must NOT be a Halt (that would gate a task the deterministic policy would keep
        // retrying). It is advisory: recorded, but the loop keeps going.
        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Equal("advisory", Assert.Single(observer.Decisions).Decision);
    }

    // ── COST BOUND (the eager cost mitigation) ───────────────────────────────────────────────────

    [Fact]
    public async Task CostBound_AtCap_SkipsDiagnose_NoRunnerCall()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan, maxCostUsd: 0.50m);
        var observer = new CapturingObserver();

        // Journal a cost that reaches the cap, so the overwatcher must not spend more on a diagnose.
        journal.RecordAttempt(task.Id, new AttemptRecord
        {
            Attempt = 1,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.GuardrailFailed,
            CostUsd = 0.75m,
            LogDir = "logs/x"
        }, JournalTaskStatus.Running);

        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Apply);

        OverwatchDecision d = await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, d.Kind);
        Assert.Empty(runner.Calls);          // the diagnose prompt was never spent — bounded by maxCostUsd
    }

    // ── WEAK-1: diagnose spend is journaled → counts toward the cap AND the reported total ────────

    [Fact]
    public async Task DiagnoseCost_IsJournaled_AdvancesCurrentCostUsd()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal(), Cost = 0.03m };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.NonInteractive);

        Assert.Equal(0m, journal.CurrentCostUsd());

        await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        // The diagnose spend now advances the run's cumulative cost — so it is BOTH gate-visible and
        // reported (JournalCost.Total folds OverwatchCostUsd in).
        Assert.Equal(0.03m, journal.CurrentCostUsd());
        Assert.Equal(0.03m, JournalCost.Total(JournalReader.Read(RunJournal.PathFor(planDef.PlanDirectory))));
    }

    [Fact]
    public async Task DiagnoseCost_CountsTowardCap_SkipsTheNextDiagnose()
    {
        // maxCostUsd is above zero cost initially, so the FIRST diagnose runs and reports a cost that reaches
        // the cap; the SECOND fire is then skipped because the journaled diagnose spend tripped the cap.
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan, maxCostUsd: 0.50m);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal(), Cost = 0.60m };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.NonInteractive);

        await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);
        Assert.Single(runner.Calls);                          // first diagnose ran (cost 0 < cap 0.50 at entry)
        Assert.True(journal.CurrentCostUsd() >= 0.50m);       // its spend was journaled and reached the cap

        await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, task, planDef, 3, taskLogDir, journal, observer, TestContext.Current.CancellationToken);
        Assert.Single(runner.Calls);                          // second diagnose SKIPPED — bounded by maxCostUsd
    }

    // ── REPORTING: decisions[] boundary "task" + overwatch.jsonl ─────────────────────────────────

    [Fact]
    public async Task Records_TaskBoundaryDecision_AndOverwatchJsonl_OnAFire()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var runner = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        Overwatch overwatch = BuildOverwatch(runner, AutonomyPolicy.Prompt, OverwatchInteractionResult.Apply);

        await overwatch.EvaluateAsync(
            OverwatchTrigger.NoOpDeadlock, task, planDef, 2, taskLogDir, journal, observer, TestContext.Current.CancellationToken);

        // decisions[] durable audit (task boundary) — both observed and persisted to run.json.
        DecisionEntry observed = Assert.Single(observer.Decisions);
        Assert.Equal("task", observed.Boundary);
        Assert.Equal(task.Id, observed.Subject);

        JournalDocument reloaded = JournalReader.Read(RunJournal.PathFor(planDef.PlanDirectory));
        Assert.NotNull(reloaded.Decisions);
        Assert.Contains(reloaded.Decisions!, e => e.Boundary == "task" && e.Subject == task.Id);

        // overwatch.jsonl per-fire detail stream.
        string jsonl = await File.ReadAllTextAsync(OverwatchJsonl(taskLogDir), TestContext.Current.CancellationToken);
        Assert.Contains("\"trigger\":\"no-op-deadlock\"", jsonl);
        Assert.Contains("\"guidance\":true", jsonl);   // applied.guidance
    }

    // ── TERMINAL exhaustion delegates to triage + records ────────────────────────────────────────

    [Fact]
    public async Task Terminal_DelegatesToTriage_AndRecordsTaskBoundaryHalt()
    {
        using var plan = new StatePlanBuilder().AddTask("01-x", guardrailBody: StatePlanBuilder.Fail("f"));
        (PlanDefinition planDef, RunJournal journal, TaskNode task, string taskLogDir) = LoadFirstTask(plan);
        var observer = new CapturingObserver();
        var triageRunner = new RecordingRunner("ai-triage")
        {
            CannedResultText = """{"diagnosis":"local-repo","analysis":"guardrail is self-contradictory"}"""
        };
        var overwatch = new Overwatch(diagnoseRunner: null, new NeedsHumanTriage(triageRunner), AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.NonInteractive));

        string? feedbackPath = await overwatch.EvaluateTerminalAsync(
            task, planDef, taskLogDir, planDef.PlanDirectory, planDef.Workspace, journal, observer,
            autoFile: false, TestContext.Current.CancellationToken);

        // Triage ran (feedback.md written); the terminal case still delegates to the shipped triage.
        Assert.NotNull(feedbackPath);
        Assert.True(File.Exists(feedbackPath!));
        Assert.Single(triageRunner.Calls);

        // The halt is recorded to decisions[] (task boundary) + overwatch.jsonl (terminal-exhaustion).
        DecisionEntry entry = Assert.Single(observer.Decisions);
        Assert.Equal("task", entry.Boundary);
        Assert.Equal("halted", entry.Decision);
        string jsonl = await File.ReadAllTextAsync(OverwatchJsonl(taskLogDir), TestContext.Current.CancellationToken);
        Assert.Contains("\"trigger\":\"terminal-exhaustion\"", jsonl);
    }

    // ── FULL-LOOP: trigger determinism + reconciliation with the deterministic floor ─────────────

    private static async Task<RunReport> RunWithOverwatchAsync(string planDir, Overwatch overwatch, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(
            load.Plan!.Config, _ => throw new InvalidOperationException("no prompt runners in fixture"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry,
            overwatch: overwatch);
        var scheduler = new Scheduler(load.Plan!, executor, journal, observer: IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, ct);
    }

    private static string ChangingOutputAction() => StatePlanBuilder.UsePowerShell
        ? """
          $f = Join-Path $env:GUARDRAILS_PLAN_DIR 'a.count'
          Add-Content -Path $f -Value 'x'
          Write-Output ("run " + (Get-Content $f).Count)
          exit 0
          """
        : """
          f="$GUARDRAILS_PLAN_DIR/a.count"
          echo x >> "$f"
          echo "run $(wc -l < "$f" | tr -d '[:space:]')"
          exit 0
          """;

    private static int AttemptCount(string planDir, string taskId)
    {
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));
        return doc.Tasks[taskId].Attempts.Count;
    }

    private static JournalTaskStatus JournalStatus(string planDir, string taskId)
    {
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));
        return doc.Tasks[taskId].Status;
    }

    [Fact]
    public async Task Eager_FiresOnceAtAttempt2_ThenTerminal_NeverGatesEarly()
    {
        // 3 attempts (retries=2), changing output so no short-circuit fires. Eager fires ONCE (attempt 2,
        // non-final ≥ 2); the final attempt exhausts to the terminal case. Advisory: it never halts early.
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-multi", actionBody: ChangingOutputAction(), guardrailBody: StatePlanBuilder.Fail("never passes"));

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        var triage = new RecordingRunner("ai-triage") { CannedResultText = "prose" };
        var overwatch = new Overwatch(diagnose, new NeedsHumanTriage(triage), AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.NonInteractive));

        RunReport report = await RunWithOverwatchAsync(plan.PlanDir, overwatch, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);
        Assert.False(report.Tasks.Single().IsGreen);
        // Exhausted → journal settles needs-human (the TaskResult.Outcome carries the last GuardrailFailed).
        Assert.Equal(JournalTaskStatus.NeedsHuman, JournalStatus(plan.PlanDir, "01-multi"));
        Assert.Equal(3, AttemptCount(plan.PlanDir, "01-multi"));   // ran the full budget — eager never gated early
        Assert.Single(diagnose.Calls);                            // eager fired exactly once (attempt 2)
        Assert.Single(triage.Calls);                              // terminal case fired once (attempt 3 exhaustion)
    }

    [Fact]
    public async Task NoOpShortCircuit_NoSanctionedChange_FloorStandsAtAttempt2()
    {
        // A no-op action (exit 0, no output/fragment) + always-failing guardrail = the #182 no-op deadlock.
        // The overwatcher proposes only a DENYLIST edit (no sanctioned change) ⇒ the floor stands: the task
        // short-circuits to needs-human at attempt 2, NOT the full 4-attempt budget.
        using var plan = new StatePlanBuilder(defaultRetries: 3)
            .AddTask("01-noop", guardrailBody: StatePlanBuilder.Fail("always"));
        PlanLoadResult probe = new PlanLoader().Load(plan.PlanDir);
        string guardrailBody = Path.Combine(probe.Plan!.Tasks[0].Directory, "guardrails", StatePlanBuilder.GuardrailFileName);

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = DenylistOnlyProposal(guardrailBody) };
        var overwatch = new Overwatch(diagnose, terminalTriage: null, AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.Apply));

        RunReport report = await RunWithOverwatchAsync(plan.PlanDir, overwatch, TestContext.Current.CancellationToken);

        Assert.Equal(TaskOutcome.NeedsHuman, report.Tasks.Single().Outcome);
        Assert.Equal(2, AttemptCount(plan.PlanDir, "01-noop"));   // short-circuit stood — the floor is the floor
    }

    [Fact]
    public async Task NoOpShortCircuit_ApprovedGuidanceGrant_UnHalts_RunsPastAttempt2()
    {
        // Same no-op deadlock, but the overwatcher proposes GUIDANCE (an allowlist lever) and the interaction
        // APPROVES ⇒ it un-halts the short-circuit (a sanctioned change makes the next attempt materially
        // different) and the task runs past attempt 2, up to the budget, before honest exhaustion.
        using var plan = new StatePlanBuilder(defaultRetries: 3)
            .AddTask("01-noop", guardrailBody: StatePlanBuilder.Fail("always"));

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        var triage = new RecordingRunner("ai-triage") { CannedResultText = "prose" };
        var overwatch = new Overwatch(diagnose, new NeedsHumanTriage(triage), AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.Apply));

        RunReport report = await RunWithOverwatchAsync(plan.PlanDir, overwatch, TestContext.Current.CancellationToken);

        Assert.False(report.Tasks.Single().IsGreen);
        Assert.Equal(JournalTaskStatus.NeedsHuman, JournalStatus(plan.PlanDir, "01-noop"));
        Assert.True(AttemptCount(plan.PlanDir, "01-noop") > 2,
            "an approved sanctioned change must un-halt the #174/#264 short-circuit and run past attempt 2");
    }

    [Fact]
    public async Task ApprovedBudgetGrants_CannotGrowBudgetPastTheCumulativeCeiling()
    {
        // WEAK-2: every approved grant proposes a BUDGET bump (retries:2). Without a cumulative ceiling this
        // would never terminate (each attempt grants +2 while the index advances +1). With the hard cumulative
        // ceiling (MaxCumulativeGrantedRetries = 4), the total attempts settle at the ORIGINAL budget (3) +
        // the ceiling (4) = 7 — repeated grants can never grow the budget without limit. That this test
        // TERMINATES is itself the proof the ceiling holds.
        using var plan = new StatePlanBuilder(defaultRetries: 2)   // original budget = 3
            .AddTask("01-noop", guardrailBody: StatePlanBuilder.Fail("always"));

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = BudgetProposal() };
        var overwatch = new Overwatch(diagnose, terminalTriage: null, AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.Apply));

        RunReport report = await RunWithOverwatchAsync(plan.PlanDir, overwatch, TestContext.Current.CancellationToken);

        Assert.Equal(JournalTaskStatus.NeedsHuman, JournalStatus(plan.PlanDir, "01-noop"));
        Assert.Equal(3 + 4, AttemptCount(plan.PlanDir, "01-noop"));  // original budget + cumulative ceiling, no more
    }

    [Fact]
    public async Task Advisory_MalformedDiagnose_DoesNotGate_FloorStandsAndRunNotAborted()
    {
        using var plan = new StatePlanBuilder(defaultRetries: 3)
            .AddTask("01-noop", guardrailBody: StatePlanBuilder.Fail("always"))
            .AddTask("02-independent");   // must still complete despite the advisory overwatch

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = "not json" };
        var overwatch = new Overwatch(diagnose, terminalTriage: null, AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.Apply));

        RunReport report = await RunWithOverwatchAsync(plan.PlanDir, overwatch, TestContext.Current.CancellationToken);

        Assert.False(report.Aborted);
        Assert.Equal(TaskOutcome.NeedsHuman, report.Tasks.Single(t => t.TaskId == "01-noop").Outcome);
        Assert.Equal(2, AttemptCount(plan.PlanDir, "01-noop"));  // malformed diagnose ⇒ floor stands
        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single(t => t.TaskId == "02-independent").Outcome);
    }

    [Fact]
    public async Task DriftDisjoint_OverwatchOnFailingTask_IsTaskBoundary_NotDrift()
    {
        // The overwatcher acts on a FAILING task in-run; definition-drift acts on an already-SUCCEEDED task
        // at resume. They are disjoint by task state. A failing task's overwatch fire is a `task`-boundary
        // decision and never a `drift`-boundary halt.
        using var plan = new StatePlanBuilder(defaultRetries: 3)
            .AddTask("01-noop", guardrailBody: StatePlanBuilder.Fail("always"));

        var diagnose = new RecordingRunner("overwatch") { CannedResultText = GuidanceProposal() };
        var observer = new CapturingObserver();

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => throw new InvalidOperationException("none"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var overwatch = new Overwatch(diagnose, terminalTriage: null, AutonomyPolicy.Prompt,
            new FakeInteraction(OverwatchInteractionResult.NonInteractive));
        var executor = new TaskExecutor(load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal,
            observer, registry, overwatch: overwatch);
        var scheduler = new Scheduler(load.Plan!, executor, journal, observer: observer);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        Assert.False(report.HasDefinitionDrift);
        Assert.NotEmpty(observer.Decisions);
        Assert.All(observer.Decisions, e => Assert.Equal("task", e.Boundary));
        Assert.DoesNotContain(observer.Decisions, e => e.Boundary == "drift");
    }
}
