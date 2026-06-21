using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests encoding plan 08 §9 (AI triage on needs-human, PO #7 / Decision 8)
/// BEFORE <see cref="NeedsHumanTriage"/> exists. Tests drive the triage step through the
/// <see cref="IPromptRunner"/> seam using a C# fake runner — no real claude process. The plan
/// WILL NOT compile against current code; that compile-failure IS the expected red-bar signal.
/// Do NOT implement the triage step here — tests only, in this one file.
/// </summary>
public sealed class NeedsHumanTriageTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fake IPromptRunner
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A C# <see cref="IPromptRunner"/> that records every invocation and returns a canned result.
    /// Scenario control:
    /// <list type="bullet">
    ///   <item><see cref="CannedResultText"/> — the text returned in <see cref="PromptResult.ResultText"/>.</item>
    ///   <item><see cref="ShouldThrow"/> — throws <see cref="InvalidOperationException"/> on the next call.</item>
    ///   <item><see cref="ShouldReturnError"/> — returns <c>IsError = true</c> instead of a clean result.</item>
    /// </list>
    /// </summary>
    private sealed class RecordingRunner : IPromptRunner
    {
        private readonly List<PromptInvocation> _calls = new();

        public RecordingRunner(string name = "ai-triage") => Name = name;

        public string Name { get; }
        public IReadOnlyList<PromptInvocation> Calls => _calls;
        public string? CannedResultText { get; set; }
        public bool ShouldThrow { get; set; }
        public bool ShouldReturnError { get; set; }

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            _calls.Add(invocation);

            if (ShouldThrow)
                throw new InvalidOperationException("RecordingRunner: configured to throw (advisory test).");

            return Task.FromResult(new PromptResult
            {
                Completed = !ShouldReturnError,
                IsError = ShouldReturnError,
                ResultText = ShouldReturnError ? null : (CannedResultText ?? "triage done"),
                Summary = ShouldReturnError ? "ai-triage is_error" : "ai-triage complete"
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan + scheduler helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run a plan through the full Scheduler with a <see cref="NeedsHumanTriage"/> wired into
    /// <see cref="TaskExecutor"/>. The plan uses script-based actions and guardrails
    /// (no real Claude); the only IPromptRunner present is the one inside <paramref name="triage"/>.
    /// </summary>
    private static async Task<RunReport> RunWithTriageAsync(
        string planDir,
        NeedsHumanTriage? triage,
        CancellationToken ct = default)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        // Script-only plan: no prompt runners needed. The registry is empty; the factory
        // lambda is never called because config.PromptRunners is empty.
        var registry = PromptRunnerRegistry.Build(
            load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners expected in triage test plan."));

        var interpreterMap = new InterpreterMap(
            new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        // The triage parameter is the not-yet-existing addition to TaskExecutor — referencing
        // it here is the compile-coupling that proves this test file drives the implementation.
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry,
            triage: triage);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal, observer: IRunObserver.Null);

        return await scheduler.RunAsync(load.Plan!, ct);
    }

    /// <summary>
    /// The TASK-LEVEL log directory where <c>feedback.md</c> lives after triage:
    /// <c>logs/&lt;runId&gt;/&lt;taskId&gt;/</c> — a sibling of <c>attempt-N/</c>, NOT inside an attempt.
    /// </summary>
    private static string TaskLogDir(string planDir, string taskId)
    {
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(planDir));
        return Path.Combine(planDir, "logs", journal.RunId, taskId);
    }

    /// <summary>
    /// An action-script body that writes a <c>needsHuman</c> fragment to
    /// <c>GUARDRAILS_STATE_OUT</c>, simulating an agent-emitted needs-human short-circuit.
    /// </summary>
    private static string NeedsHumanAction(string question)
    {
        // JSON for the needsHuman key; question is a simple string safe in both quoting styles.
        return StatePlanBuilder.UsePowerShell
            ? $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{{\"needsHuman\": \"{question}\"}}'"
            : $"printf '%s' '{{\"needsHuman\": \"{question}\"}}' > \"$GUARDRAILS_STATE_OUT\"";
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Triage runs EXACTLY ONCE when a task reaches <c>needs-human</c> because it exhausted
    /// its retry budget (action/guardrail failures across all attempts). Assert the fake
    /// <see cref="IPromptRunner"/> received exactly one <c>ai-triage</c>-profile invocation
    /// for that task.
    /// </summary>
    [Fact]
    public async Task Triage_RunsOnAttemptExhaustion()
    {
        var runner = new RecordingRunner("ai-triage");
        var triage = new NeedsHumanTriage(runner);

        // 1 retry → 2 attempts; the guardrail always fails → needs-human via exhaustion.
        using var plan = new StatePlanBuilder(defaultRetries: 1)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("always fails"));

        RunReport report = await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);
        // Exactly one ai-triage invocation at exhaustion.
        Assert.Single(runner.Calls);
    }

    /// <summary>
    /// When the agent itself emitted <c>{"needsHuman": "..."}</c> (a clean short-circuit — the
    /// human is already being asked), triage does NOT run. Assert ZERO <c>ai-triage</c>
    /// invocations.
    /// </summary>
    [Fact]
    public async Task Triage_SkippedOnAgentEmittedNeedsHuman()
    {
        var runner = new RecordingRunner("ai-triage");
        var triage = new NeedsHumanTriage(runner);

        // The action script emits {"needsHuman": "..."} directly — a clean short-circuit that
        // does NOT exhaust retries. Even with retries=3, this escalates on the first attempt.
        using var plan = new StatePlanBuilder(defaultRetries: 3)
            .AddTask("01-self-asks", actionBody: NeedsHumanAction("which approach should I use?"));

        RunReport report = await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);

        // Zero ai-triage invocations — the agent-emitted needsHuman bypasses triage entirely.
        Assert.Empty(runner.Calls);
    }

    /// <summary>
    /// Triage does NOT run between attempts while the task can still retry — only on the
    /// terminal exhaustion transition. Assert no <c>ai-triage</c> invocation occurs on a
    /// non-final failed attempt that is followed by another attempt.
    /// </summary>
    [Fact]
    public async Task Triage_NotRunMidRetry()
    {
        var runner = new RecordingRunner("ai-triage");
        var triage = new NeedsHumanTriage(runner);

        // 2 retries → 3 attempts; all fail the guardrail. Triage fires only on the
        // terminal transition (after attempt 3), NOT between attempts 1→2 or 2→3.
        using var plan = new StatePlanBuilder(defaultRetries: 2)
            .AddTask("01-multi-fail", guardrailBody: StatePlanBuilder.Fail("never passes"));

        RunReport report = await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        Assert.False(report.AllSucceeded);
        // Exactly one invocation across 3 failed attempts — mid-retry triage is never triggered.
        Assert.Single(runner.Calls);
    }

    /// <summary>
    /// Triage writes <c>logs/&lt;runId&gt;/&lt;task-id&gt;/feedback.md</c> containing a diagnosis
    /// classified as either a Guardrails-TOOL problem (warrants a GH issue against the
    /// Guardrails repo) or a problem LOCAL to the current repo. Assert the file exists at the
    /// task-level path and that both diagnosis classes are representable.
    /// </summary>
    [Fact]
    public async Task Triage_WritesFeedbackMdWithToolVsLocalDiagnosis()
    {
        // ── Guardrails-tool problem ───────────────────────────────────────────────────────────
        const string toolResult =
            """{"diagnosis":"guardrails-tool","ghIssueTitle":"Guardrails: retry loop ignores X","ghIssueBody":"Steps: ..."}""";

        var toolRunner = new RecordingRunner("ai-triage") { CannedResultText = toolResult };
        var triageTool = new NeedsHumanTriage(toolRunner);

        using var toolPlan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("fail"));

        await RunWithTriageAsync(toolPlan.PlanDir, triageTool, TestContext.Current.CancellationToken);

        string toolFeedback = Path.Combine(TaskLogDir(toolPlan.PlanDir, "01-doomed"), "feedback.md");
        Assert.True(File.Exists(toolFeedback),
            $"feedback.md must exist at the task-level path for a tool-problem triage: {toolFeedback}");
        Assert.Contains("guardrails-tool", File.ReadAllText(toolFeedback), StringComparison.OrdinalIgnoreCase);

        // ── Local-repo problem ────────────────────────────────────────────────────────────────
        const string localResult =
            """{"diagnosis":"local-repo","analysis":"The guardrail expectations are self-contradictory."}""";

        var localRunner = new RecordingRunner("ai-triage") { CannedResultText = localResult };
        var triageLocal = new NeedsHumanTriage(localRunner);

        using var localPlan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("fail"));

        await RunWithTriageAsync(localPlan.PlanDir, triageLocal, TestContext.Current.CancellationToken);

        string localFeedback = Path.Combine(TaskLogDir(localPlan.PlanDir, "01-doomed"), "feedback.md");
        Assert.True(File.Exists(localFeedback),
            $"feedback.md must exist at the task-level path for a local-problem triage: {localFeedback}");
        Assert.Contains("local-repo", File.ReadAllText(localFeedback), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The task's <c>needs-human</c> message (the text in the run summary / <c>status</c>
    /// render) references the <c>logs/&lt;runId&gt;/&lt;task-id&gt;/feedback.md</c> path.
    /// Assert the surfaced message contains the task-level feedback.md path.
    /// </summary>
    [Fact]
    public async Task Triage_NeedsHumanMessageReferencesFeedbackPath()
    {
        var runner = new RecordingRunner("ai-triage");
        var triage = new NeedsHumanTriage(runner);

        using var plan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("fails"));

        RunReport report = await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        string expectedFeedbackPath = Path.Combine(TaskLogDir(plan.PlanDir, "01-doomed"), "feedback.md");

        // The task's needs-human message (TaskResult.Summary) must reference the feedback.md path
        // so the human lands on the triage diagnosis immediately.
        Assert.Contains(expectedFeedbackPath, task.Summary);
    }

    /// <summary>
    /// Triage is ADVISORY and gates NOTHING. Drive the fake runner to FAIL/throw; assert
    /// (a) the task verdict is STILL <c>needs-human</c> (journal status), (b) the run is NOT
    /// blocked/aborted — independent tasks still complete — and (c) the triage
    /// <see cref="PromptResult.IsError"/>/thrown exception is NEVER read as the verdict.
    /// </summary>
    [Fact]
    public async Task Triage_IsAdvisory_ThrownTriageDoesNotChangeVerdictOrBlock()
    {
        var runner = new RecordingRunner("ai-triage") { ShouldThrow = true };
        var triage = new NeedsHumanTriage(runner);

        // 01-doomed exhausts immediately; 02-independent is unrelated and must still run.
        using var plan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("fails"))
            .AddTask("02-independent");   // no dependsOn: independent of 01-doomed

        RunReport report = await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        // (a) The doomed task's journal status is still NeedsHuman — a thrown triage cannot
        //     change the verdict, re-open the task, or mark it done.
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["01-doomed"].Status);

        // (b) The run was NOT aborted — the independent task completed despite the triage throw.
        TaskResult independent = report.Tasks.Single(t => t.TaskId == "02-independent");
        Assert.Equal(TaskOutcome.Succeeded, independent.Outcome);

        // (c) Triage was attempted (runner was called), but the thrown exception is not the verdict.
        Assert.Single(runner.Calls);

        // A failed triage just means "no feedback.md produced" — assert the file is absent.
        string feedbackPath = Path.Combine(TaskLogDir(plan.PlanDir, "01-doomed"), "feedback.md");
        Assert.False(File.Exists(feedbackPath),
            "A thrown triage must not produce a partial/corrupt feedback.md");
    }

    /// <summary>
    /// With <c>triageAutoFile</c> unset/default (<c>false</c>), triage only DRAFTS the GH
    /// issue (title+body) INTO <c>feedback.md</c> and files NOTHING to a remote. Assert no
    /// auto-file/GH-API side effect occurs by default (drafts only).
    /// </summary>
    [Fact]
    public async Task Triage_AutoFileOffByDefault_DraftsOnly()
    {
        const string toolResult =
            """{"diagnosis":"guardrails-tool","ghIssueTitle":"Guardrails: something broke","ghIssueBody":"Repro: ..."}""";

        var runner = new RecordingRunner("ai-triage") { CannedResultText = toolResult };
        // autoFile = false (the default) — draft into feedback.md, file nothing remotely.
        var triage = new NeedsHumanTriage(runner, autoFile: false);

        using var plan = new StatePlanBuilder(defaultRetries: 0)
            .AddTask("01-doomed", guardrailBody: StatePlanBuilder.Fail("fails"));

        await RunWithTriageAsync(plan.PlanDir, triage, TestContext.Current.CancellationToken);

        string feedbackPath = Path.Combine(TaskLogDir(plan.PlanDir, "01-doomed"), "feedback.md");
        Assert.True(File.Exists(feedbackPath),
            "feedback.md must be written even in draft-only mode");

        string content = File.ReadAllText(feedbackPath);

        // The drafted GH issue title and body are present in feedback.md.
        Assert.Contains("Guardrails: something broke", content);
        Assert.Contains("Repro: ...", content);

        // Draft-only: the runner was called exactly ONCE for triage analysis.
        // No second call was made for auto-filing (which would require a separate runner
        // call or GH-API client — neither of which is wired in the default configuration).
        Assert.Single(runner.Calls);
    }
}
