using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`: introducing that nested namespace
// anywhere in this assembly shadows the production `Guardrails.Core.Journal` namespace for every
// unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves an enclosing
// nested namespace before a `using`-imported one) — see OverwatchNoVerdictTests.cs's
// `Journal.TaskStatus.Running`, which is out of this task's write scope to fix.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 28 §11 finding 3 — a verifier-only v1 with no judge measurement is unfalsifiable, which
/// disqualifies a plan whose thesis is that measurement decides the v2 bets. Today
/// <c>grep "CostUsd\|Usage" GuardrailRunner.cs</c> returns NOTHING: <see cref="GuardrailRunner"/> resolves
/// a judge and runs it, but the <see cref="PromptResult.CostUsd"/>/<see cref="PromptResult.Usage"/> that
/// invocation reports are discarded rather than carried onto <see cref="AttemptJudge"/>. This suite pins
/// two things a future implementation must do, and one it must NOT.
///
/// <para><b>Read from <c>run.json</c>'s BYTES, not the in-memory <see cref="PromptResult"/>.</b>
/// <see cref="AttemptJudge"/> has no <c>CostUsd</c>/<c>Usage</c> member today, so there is no C# property
/// to assert on even after driving the real runner — the only place the fact can be checked is the
/// SERIALIZED journal, exactly as <c>JudgeProvenanceSchemaTests</c> already does for the rest of the
/// <c>judge</c> object. Every test below therefore drives the REAL <see cref="GuardrailRunner"/> against a
/// fake <see cref="IPromptRunner"/> that reports a real cost and usage (the <c>PromptRoleSeamTests</c>
/// idiom), writes the resulting <see cref="AttemptRecord"/> through the REAL <see cref="JournalJson.Options"/>
/// serializer, and inspects the emitted TEXT with <see cref="JsonDocument"/>.</para>
///
/// <para><b>TDD red.</b> <see cref="JudgeCostUsd_ReachesRunJsonBytes"/> and
/// <see cref="JudgeUsage_ReachesRunJsonBytes"/> fail today because the <c>judge</c> object never grows a
/// <c>costUsd</c> or <c>usage</c> key at all — there is no member to carry either value onto.
/// <see cref="JournalCost_Total_IsUnchangedByJudgeSpend"/> is the LOAD-BEARING test (§11 finding 3): its
/// first assertion is the same precondition as the two tests above (today unmet, so it fails there too),
/// and once a future change satisfies that precondition, the assertion that follows — that
/// <see cref="JournalCost.Total"/> still equals the actor's own spend alone — becomes the regression guard
/// that catches an implementation that folds judge spend into the run total instead of recording it beside
/// it. Folding it in would make <c>maxCostUsd</c> trip earlier on every existing Claude run and silently
/// change the <c>--autonomous</c> brake's behaviour — a semantic change to the liveness floor this plan
/// does not make.</para>
/// </summary>
public sealed class JudgeSpendRecordingTests : IDisposable
{
    private const string JudgeRunnerName = "judge-runner";
    private const decimal JudgeCostUsd = 0.42m;
    private const int JudgeInputTokens = 1_000;
    private const int JudgeOutputTokens = 250;

    /// <summary>The task's OWN attempt cost — unrelated to the judge, and the only figure
    /// <see cref="JournalCost.Total"/> may reflect once judge spend is also recorded.</summary>
    private const decimal ActorCostUsd = 2.00m;

    private const string TaskId = "01-impl";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-judge-spend-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JudgeSpendRecordingTests() => Directory.CreateDirectory(_root);

    // --- 1. costUsd reaches the judge object, in run.json's BYTES ------------------------------

    [Fact]
    public async Task JudgeCostUsd_ReachesRunJsonBytes()
    {
        string json = await RunGuardrailAndWriteJournalAsync();

        JsonElement judgeElement = JudgeElement(json);

        // Positive control FIRST: the judge object itself is already emitted today (runner/kind/model/…),
        // so the negative assertion below cannot pass vacuously off a judge that was never written at all.
        Assert.True(judgeElement.TryGetProperty("runner", out _));

        Assert.True(
            judgeElement.TryGetProperty("costUsd", out JsonElement costUsd),
            "the judge's costUsd never reached run.json — grep \"CostUsd\" GuardrailRunner.cs is empty " +
            "(plan 28 §11 finding 3): the invocation's PromptResult.CostUsd is computed and discarded.");
        Assert.Equal(JudgeCostUsd, costUsd.GetDecimal());
    }

    // --- 2. usage reaches the judge object, in run.json's BYTES ---------------------------------

    [Fact]
    public async Task JudgeUsage_ReachesRunJsonBytes()
    {
        string json = await RunGuardrailAndWriteJournalAsync();

        JsonElement judgeElement = JudgeElement(json);

        Assert.True(
            judgeElement.TryGetProperty("usage", out JsonElement usage),
            "the judge's usage never reached run.json — a costless verifier and a silent one are different " +
            "facts, and today neither is recorded (AttemptJudge carries no usage member).");
        Assert.Equal(JudgeInputTokens, usage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(JudgeOutputTokens, usage.GetProperty("outputTokens").GetInt32());
    }

    // --- 3. THE LOAD-BEARING TEST: recording judge spend must not move JournalCost.Total --------

    [Fact]
    public async Task JournalCost_Total_IsUnchangedByJudgeSpend()
    {
        string json = await RunGuardrailAndWriteJournalAsync();

        // The precondition this test is really about: judge spend must have reached the journal at all.
        // Today it has not (see the two tests above), so this assertion fails FIRST — a total that hasn't
        // moved proves nothing when there was never anything to fold in. Once a future change satisfies
        // this precondition, the assertion below is what actually holds the line.
        Assert.True(
            JudgeElement(json).TryGetProperty("costUsd", out _),
            "judge costUsd must reach the journal before 'JournalCost.Total is unchanged' can mean anything");

        JournalDocument document = JournalReader.Read(WrittenPath!);
        decimal? total = JournalCost.Total(document);

        // Actor spend ONLY (plan 28 §11 finding 3): the total is never allowed to also pick up the judge's
        // $0.42, however it ends up recorded. The two numbers are deliberately separate — the total is
        // actor spend, the judge column is verifier spend.
        Assert.Equal(ActorCostUsd, total);
    }

    // --- fixture: drive the REAL GuardrailRunner, then the REAL journal writer -------------------

    /// <summary>Absolute path of the <c>run.json</c> the fixture below wrote — set once per test run.</summary>
    private string? WrittenPath;

    /// <summary>
    /// A fake <see cref="IPromptRunner"/> that reports a real cost and usage, exactly as a genuine judge
    /// runner would — the only difference from <c>PromptRoleSeamTests.CapturingRunner</c> that matters
    /// here. Nothing downstream should have to special-case a judge over any other prompt runner.
    /// </summary>
    private sealed class CostReportingRunner : IPromptRunner
    {
        public string Name => JudgeRunnerName;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "judge completed",
                CostUsd = JudgeCostUsd,
                Usage = new PromptUsage { InputTokens = JudgeInputTokens, OutputTokens = JudgeOutputTokens }
            });
    }

    private static RunConfig BuildRunConfig() => new()
    {
        Version = 1,
        DefaultPromptRunner = JudgeRunnerName,
        PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { JudgeRunnerName },
        PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
        {
            [JudgeRunnerName] = new PromptRunnerConfig
            {
                Name = JudgeRunnerName,
                Command = JudgeRunnerName,
                Settings = new PromptRunnerSettings()
            }
        }
    };

    /// <summary>
    /// Drives the REAL <see cref="GuardrailRunner"/> over one prompt guardrail (the
    /// <c>PromptRoleSeamTests.GuardrailRunner_PassesGuardrailRole</c> fixture, reused verbatim) whose runner
    /// reports a real cost and usage, folds the resulting <see cref="AttemptJudge"/> into an
    /// <see cref="AttemptRecord"/> beside a known actor cost, and writes it through the REAL
    /// <see cref="JournalJson.Options"/> serializer. Returns the written bytes; <see cref="WrittenPath"/>
    /// is left pointing at the file for callers that also want to re-read it as a
    /// <see cref="JournalDocument"/>.
    /// </summary>
    private async Task<string> RunGuardrailAndWriteJournalAsync()
    {
        string taskDir = Path.Combine(_root, "tasks", TaskId);
        Directory.CreateDirectory(taskDir);
        string guardrailPromptPath = Path.Combine(taskDir, "guardrails", "01-check.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(guardrailPromptPath)!);
        File.WriteAllText(guardrailPromptPath, "Check the work.\n");

        var task = new TaskNode
        {
            Id = TaskId,
            Directory = taskDir,
            Description = "implement the feature",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails =
            [
                new GuardrailDefinition { Name = "01-check", Path = guardrailPromptPath, Kind = ActionKind.Prompt }
            ]
        };

        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = BuildRunConfig(),
            Tasks = [task]
        };

        var runner = new CostReportingRunner();
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var promptSupport = new PromptExecutionSupport(registry);
        var scriptRunner = new ScriptUnitRunner(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

        var guardrailRunner = new GuardrailRunner(
            plan, IRunObserver.Null, scriptRunner, promptSupport, (_, _) => TimeSpan.FromMinutes(5));

        string logDir = Path.Combine(plan.PlanDirectory, "logs", "run", task.Id, "attempt-1");
        GuardrailRunResult guardrails = await guardrailRunner.RunAsync(
            task,
            workspace: plan.Workspace,
            env: new Dictionary<string, string>(StringComparer.Ordinal),
            snapshotPath: Path.Combine(plan.PlanDirectory, "state.json"),
            logDir: logDir,
            route: null,
            cancellationToken: Ct);

        // Sanity precondition, already true today: SOME judge resolves (runner/kind/model already ship).
        // It is the cost/usage half this suite pins as missing — asserted below, never here, so a failure
        // here would be a fixture bug rather than the behaviour under test.
        Assert.NotNull(guardrails.Judge);

        var attempt = new AttemptRecord
        {
            Attempt = 1,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Succeeded,
            CostUsd = ActorCostUsd,
            LogDir = $"logs/test-run/{TaskId}/attempt-1",
            Provenance = new AttemptProvenance { Judge = guardrails.Judge }
        };

        var document = new JournalDocument
        {
            RunId = "test-run",
            PlanHash = "sha256:test",
            NextMergeSequence = 2,
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                [TaskId] = new() { Status = JournalTaskStatus.Succeeded, MergeSequence = 1, Attempts = [attempt] }
            }
        };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);
        WrittenPath = Path.Combine(_root, "run.json");
        AtomicFile.WriteAllText(WrittenPath, json);

        return json;
    }

    private static JsonElement JudgeElement(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone().GetProperty("tasks").GetProperty(TaskId)
            .GetProperty("attempts")[0]
            .GetProperty("provenance").GetProperty("judge");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
