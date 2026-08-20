using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #452 — the overwatcher was a no-op in practice, and it was a <b>silent, billed</b> one.
///
/// <para>On a real run the diagnose fired, spent \$0.66 across 11 turns, and recorded ZERO decisions,
/// because its own tool calls were permission-denied: it inherited
/// <see cref="PromptRunnerSettings.AllowedTools"/>'s record default (an EMPTY list) and was then asked to
/// read attempt logs it had no granted tool to open. It terminated <c>error_max_turns</c> with no verdict,
/// and NOTHING said so — not the task table, not the summary, not <c>run.json</c>. The only trace was an
/// <c>is_error: true</c> buried in a per-attempt JSONL nobody opens unless they already suspect a problem.</para>
///
/// <para><b>The regression that matters is the OBSERVABILITY one.</b> The original defect was invisible
/// precisely because nothing observed it, so the load-bearing tests here are the ones asserting that a
/// diagnose which comes back with nothing produces a recorded <c>decisions[]</c> failure, an
/// <c>overwatch.jsonl</c> record AND a visible operator line — not merely that the tool list has the right
/// contents. A permission-list test would have passed happily on any list; only "the failure is reported"
/// catches the whole class.</para>
///
/// <para>The counterweight test matters just as much: an overwatcher that was NOT consulted (no runner,
/// cost cap reached) must still say nothing. Reporting a non-event as a failure would train the operator to
/// ignore the line, which is how a warning becomes silence again.</para>
/// </summary>
public sealed class OverwatchNoVerdictTests : IDisposable
{
    private readonly string _planDir;
    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;
    private readonly TaskNode _task;
    private readonly string _taskLogDir;

    public OverwatchNoVerdictTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-overwatch-nv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");

        string taskDir = Path.Combine(_planDir, "tasks", "02-implement");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        _task = new TaskNode
        {
            Id = "02-implement",
            Directory = taskDir,
            Description = "implement the runner kind",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.prompt.md"), Kind = ActionKind.Prompt },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        };

        _plan = new PlanDefinition
        {
            PlanDirectory = _planDir,
            Workspace = _planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [_task]
        };

        _journal = RunJournal.LoadOrCreate(_plan);
        _taskLogDir = Path.Combine(_planDir, "logs", _journal.Document.RunId, _task.Id);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A runner that reproduces the #452 terminal shape: the denial abort (or, with different ctor args,
    /// a raw <c>error_max_turns</c>). It also CAPTURES the invocation so the tool profile the harness
    /// composed can be asserted at the source instead of inferred.
    /// </summary>
    private sealed class FailingRunner(bool completed, bool isError, string summary, string? resultText = null)
        : IPromptRunner
    {
        public PromptInvocation? Seen { get; private set; }

        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new PromptResult
            {
                Completed = completed,
                IsError = isError,
                ResultText = resultText,
                CostUsd = 0.66m,
                Summary = summary
            });
        }
    }

    /// <summary>A runner that must never be called (the "not consulted" assertions).</summary>
    private sealed class ExplodingRunner : IPromptRunner
    {
        public string Name => "overwatch";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the diagnose must not have been invoked");
    }

    /// <summary>Records the operator-visible surfaces so "was anything said at all" is assertable.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, string Reason)> NoVerdicts { get; } = [];

        public List<DecisionEntry> Decisions { get; } = [];

        public void OverwatchNoVerdict(string taskId, string reason) => NoVerdicts.Add((taskId, reason));

        public void DecisionRecorded(DecisionEntry entry) => Decisions.Add(entry);

        // The three required members; everything else on IRunObserver is a default no-op.
        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }

    /// <summary>The observed #452 abort summary — the runner's own wording for "every call was refused".</summary>
    private const string DenialAbortSummary =
        "aborted after 3 consecutive permission-denied tool calls — the prompt has no granted tool for " +
        "what it was asked to do (refused: cd \"<logdir>\" && python - <<'EOF')";

    private Task<OverwatchDecision> EvaluateAsync(Overwatch overwatch, IRunObserver observer) =>
        overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, _task, _plan, attempt: 2, _taskLogDir, _journal, observer,
            TestContext.Current.CancellationToken);

    private IReadOnlyList<DecisionEntry> JournalledDecisions() =>
        RunJournal.LoadOrCreate(_plan).Document.Decisions ?? [];

    // ── The regression that matters: a denied-tool diagnose is RECORDED and VISIBLE ─────────────────

    [Fact]
    public async Task DenialAbortedDiagnose_RecordsANoVerdictDecision_AndEmitsAVisibleLine()
    {
        var observer = new RecordingObserver();
        var overwatch = new Overwatch(
            new FailingRunner(completed: false, isError: true, DenialAbortSummary),
            terminalTriage: null, AutonomyPolicy.Prompt);

        OverwatchDecision decision = await EvaluateAsync(overwatch, observer);

        // (1) The operator SEES it. This is the assertion the original bug would have failed: the whole
        // failure mode was that this list stayed empty while money was spent.
        (string taskId, string reason) = Assert.Single(observer.NoVerdicts);
        Assert.Equal(_task.Id, taskId);
        Assert.Equal(DenialAbortSummary, reason);

        // (2) It is DURABLE — a decisions[] entry survives in run.json, so a post-hoc reader can tell a
        // supervised run from an unsupervised one without opening a per-attempt JSONL.
        DecisionEntry entry = Assert.Single(JournalledDecisions());
        Assert.Equal(DecisionTokens.NoVerdict, entry.Decision);
        Assert.Equal("task", entry.Boundary);
        Assert.Equal(_task.Id, entry.Subject);
        Assert.Contains("no verdict", entry.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DenialAbortSummary, entry.Detail);

        // (3) And the per-fire detail stream carries it too.
        string jsonl = await File.ReadAllTextAsync(
            Path.Combine(_taskLogDir, "overwatch.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("\"decision\":\"no-verdict\"", jsonl);

        // (4) Still ADVISORY. Reporting the supervisor's failure must not start gating tasks on it.
        Assert.Equal(OverwatchDecisionKind.NoAction, decision.Kind);

        // (5) One event, one line: the yellow advisory replaces the green decision line rather than
        // printing beside it.
        Assert.Empty(observer.Decisions);
    }

    [Fact]
    public async Task MaxTurnsExhaustedDiagnose_IsReported_NotSwallowed()
    {
        // The literal #452 evidence shape: subtype error_max_turns, is_error true, no parseable body.
        var observer = new RecordingObserver();
        var overwatch = new Overwatch(
            new FailingRunner(completed: false, isError: true, "claude hit the max-turns ceiling (11 turn(s))"),
            terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, observer);

        Assert.Single(observer.NoVerdicts);
        Assert.Contains("max-turns", observer.NoVerdicts[0].Reason);
        Assert.Equal(DecisionTokens.NoVerdict, Assert.Single(JournalledDecisions()).Decision);
    }

    [Fact]
    public async Task UnparseableVerdict_IsReported_NotSwallowed()
    {
        // A diagnose that RAN clean but answered in prose spent exactly as much as one that answered in
        // JSON. "It replied, but not with a verdict" is a supervisor failure, not a quiet success.
        var observer = new RecordingObserver();
        var overwatch = new Overwatch(
            new FailingRunner(completed: true, isError: false, "ok", resultText: "I think you should try again."),
            terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, observer);

        Assert.Single(observer.NoVerdicts);
        Assert.Contains("parseable verdict", observer.NoVerdicts[0].Reason);
        Assert.Equal(DecisionTokens.NoVerdict, Assert.Single(JournalledDecisions()).Decision);
    }

    [Fact]
    public async Task ThrownDiagnose_IsReported_WithTheExceptionType_AndNeverPropagates()
    {
        var observer = new RecordingObserver();
        var overwatch = new Overwatch(new ExplodingRunner(), terminalTriage: null, AutonomyPolicy.Prompt);

        OverwatchDecision decision = await EvaluateAsync(overwatch, observer);

        Assert.Equal(OverwatchDecisionKind.NoAction, decision.Kind);
        Assert.Contains("InvalidOperationException", Assert.Single(observer.NoVerdicts).Reason);
    }

    // ── The counterweight: NOT consulted ⇒ still silent (a non-event must not be reported) ──────────

    [Fact]
    public async Task NoDiagnoseRunner_StaysSilent_NothingRanAndNothingWasBilled()
    {
        var observer = new RecordingObserver();
        var overwatch = new Overwatch(diagnoseRunner: null, terminalTriage: null, AutonomyPolicy.Prompt);

        OverwatchDecision decision = await EvaluateAsync(overwatch, observer);

        Assert.Equal(OverwatchDecisionKind.NoAction, decision.Kind);
        Assert.Empty(observer.NoVerdicts);
        Assert.Empty(JournalledDecisions());
        Assert.False(File.Exists(Path.Combine(_taskLogDir, "overwatch.jsonl")));
    }

    [Fact]
    public async Task CostCapAlreadyReached_StaysSilent_AndNeverInvokesTheDiagnose()
    {
        // The cost bound short-circuits BEFORE the runner: an ExplodingRunner proves the diagnose was
        // never launched, so there is no spend to report and nothing to say.
        var cappedPlan = _plan with { Config = new RunConfig { Version = 1, MaxCostUsd = 1.0m } };
        _journal.AddOverheadCost(2.0m);

        var observer = new RecordingObserver();
        var overwatch = new Overwatch(new ExplodingRunner(), terminalTriage: null, AutonomyPolicy.Prompt);

        OverwatchDecision decision = await overwatch.EvaluateAsync(
            OverwatchTrigger.EagerAttempt, _task, cappedPlan, attempt: 2, _taskLogDir, _journal,
            observer, TestContext.Current.CancellationToken);

        Assert.Equal(OverwatchDecisionKind.NoAction, decision.Kind);
        Assert.Empty(observer.NoVerdicts);
        Assert.Empty(JournalledDecisions());
    }

    // ── The permission profile itself, asserted at the composition point ────────────────────────────

    [Fact]
    public async Task DiagnoseInvocation_GrantsReadGlobGrep_AndNoWriteOrShellTool()
    {
        var runner = new FailingRunner(completed: false, isError: true, DenialAbortSummary);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, new RecordingObserver());

        Assert.NotNull(runner.Seen);
        PromptInvocation seen = runner.Seen;

        // The defect was an EMPTY list inherited from the record default — assert non-empty first, since
        // that single fact is the whole of #452's root cause.
        Assert.NotEmpty(seen.Settings.AllowedTools);
        Assert.Equal(["Read", "Glob", "Grep"], seen.Settings.AllowedTools);

        // Write-none is structural, not advisory: the judge has no mechanism to edit anything.
        Assert.DoesNotContain("Write", seen.Settings.AllowedTools);
        Assert.DoesNotContain("Edit", seen.Settings.AllowedTools);
        Assert.DoesNotContain("Bash", seen.Settings.AllowedTools);
    }

    [Fact]
    public async Task DiagnoseInvocation_DeclaresTheFailFastDenialBound()
    {
        var runner = new FailingRunner(completed: false, isError: true, DenialAbortSummary);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, new RecordingObserver());

        Assert.NotNull(runner.Seen);
        Assert.Equal(3, runner.Seen.AbortAfterConsecutiveToolDenials);
    }

    // ── The brief: concrete evidence, not a template the judge has to resolve ───────────────────────

    [Fact]
    public async Task DiagnosePrompt_NamesTheResolvedLogDirectory_NotTheLiteralRunIdPlaceholder()
    {
        // The shipped brief said "logs/<runId>/<taskId>/" with the run id never substituted, against a
        // workspace that is not even where the plan folder lives. Guessing where its own input lived is
        // what the refused shell calls were FOR.
        var runner = new FailingRunner(completed: false, isError: true, DenialAbortSummary);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, new RecordingObserver());

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;
        Assert.Contains(_taskLogDir, prompt);
        Assert.DoesNotContain("<runId>", prompt);
    }

    [Fact]
    public async Task DiagnosePrompt_StatesTheJournalledAttemptOutcomes_SoTheCutOffShapeIsVisible()
    {
        // The #94 "a bigger budget would finish" shape: every attempt ends max-turns with NO failed
        // guardrails. That is a deterministic fact the harness already holds, so the brief STATES it
        // rather than making the judge reconstruct it from logs — which is the difference between a
        // supervisor that errors and one that runs but still says nothing useful.
        RecordMaxTurnsAttempt(1);
        RecordMaxTurnsAttempt(2);

        var runner = new FailingRunner(completed: false, isError: true, DenialAbortSummary);
        var overwatch = new Overwatch(runner, terminalTriage: null, AutonomyPolicy.Prompt);

        await EvaluateAsync(overwatch, new RecordingObserver());

        Assert.NotNull(runner.Seen);
        string prompt = runner.Seen.ComposedPrompt;
        Assert.Contains("| 1 | max-turns | (none) |", prompt);
        Assert.Contains("| 2 | max-turns | (none) |", prompt);
        Assert.Contains("CUT OFF", prompt);
        Assert.Contains("BUDGET lever", prompt);
    }

    private void RecordMaxTurnsAttempt(int attempt) =>
        _journal.RecordAttempt(
            _task.Id,
            new AttemptRecord
            {
                Attempt = attempt,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.MaxTurns,
                LogDir = $"logs/run/{_task.Id}/attempt-{attempt}"
            },
            Journal.TaskStatus.Running);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup — a locked file must never fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
