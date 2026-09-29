using System.Text.Json;
using Guardrails.Core.Bundle;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// #817: compactions are counted per attempt (in episodes, not status lines), recorded on the attempt's provenance and
/// its <c>attempt-finished</c> row, shown in the bundle SUMMARY, and a failed attempt that compacted heavily for its
/// turns is diagnosed as CONTEXT THRASH rather than an under-sized turn budget or clock.
/// </summary>
public sealed class ContextThrashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Compacting = """{"type":"system","subtype":"status","status":"compacting","session_id":"s"}""";
    private const string Succeeded = """{"type":"system","subtype":"status","status":null,"compact_result":"success","session_id":"s"}""";
    private const string Boundary = """{"type":"system","subtype":"compact_boundary","session_id":"s"}""";
    private const string Failed = """{"type":"system","subtype":"status","status":null,"compact_result":"failed","compact_error":"Request timed out"}""";
    private const string Assistant = """{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"reading"}]}}""";
    private const string User = """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""";

    // ─── counting: episodes, not status lines ──────────────────────────────────────────

    [Fact]
    public void RepeatedCompactingLines_AreOneCompaction_AndItsResultAndBoundaryAreNotCountedAgain()
    {
        CompactionCounts counts = Parse([.. Repeat(Compacting, 22), Succeeded, Boundary, User, Assistant]);

        Assert.Equal(new CompactionCounts(1, 0), counts);
    }

    [Fact]
    public void TheDogfoodShape_CountsSevenEpisodes_NotTheRawStatusLines()
    {
        // The shape of the #817 attempt-1 stream: 114 `compacting` lines, 5 successes, 1 failure, and a compaction still
        // running when the stream ended (the attempt timed out mid-compaction).
        var lines = new List<string>();
        foreach (int run in new[] { 22, 6, 6, 3, 3 })
        {
            lines.AddRange(Repeat(Compacting, run));
            lines.AddRange([Succeeded, Boundary, User, Assistant, User]);
            if (run == 22)
            {
                lines.AddRange(Repeat(Compacting, 73));
                lines.AddRange([Failed, Assistant, User]);
            }
        }

        lines.Add(Compacting);

        Assert.Equal(114, lines.Count(l => l == Compacting));
        Assert.Equal(new CompactionCounts(7, 1), Parse(lines));
    }

    [Fact]
    public void AFailedCompaction_IsACompactionAndAFailure_AndStillFeedsTheContextManagementFailure()
    {
        ClaudeResult result = ClaudeStreamParser.ParseAll(string.Join('\n', Compacting, Compacting, Failed, Assistant));

        Assert.Equal(new CompactionCounts(1, 1), result.Compactions);
        Assert.Equal(1, result.CompactionFailure!.Count);
    }

    [Fact]
    public void ACloseSeenWithoutItsOpening_StillCounts_OnceForAResultAndBoundaryPair()
    {
        Assert.Equal(new CompactionCounts(1, 0), Parse([Assistant, Succeeded, Boundary, Assistant]));
        Assert.Equal(new CompactionCounts(1, 0), Parse([Assistant, Boundary, Assistant]));
        Assert.Equal(new CompactionCounts(2, 0), Parse([Boundary, Assistant, Boundary]));
        Assert.Equal(new CompactionCounts(1, 1), Parse([Failed]));
    }

    [Fact]
    public void ASessionThatNeverCompacted_ReportsNoCounts()
    {
        Assert.Null(ClaudeStreamParser.ParseAll(string.Join('\n', Assistant, User, Assistant)).Compactions);
    }

    // ─── the threshold ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(9, 76, true)]    // #817 attempt 3: max-turns after 9 compactions
    [InlineData(8, 81, true)]
    [InlineData(3, 36, true)]    // exactly one per 12 turns
    [InlineData(3, 37, false)]
    [InlineData(3, 51, false)]   // a session that compacted a few times and worked normally
    [InlineData(2, 36, false)]
    [InlineData(2, 2, false)]    // below the floor, however few the turns
    [InlineData(7, null, true)]  // a timeout ends with no result, so no turn count: the floor alone decides
    [InlineData(3, null, true)]
    [InlineData(2, null, false)]
    public void TheThreshold_IsAFloorOfThree_AndOneCompactionPerTwelveTurns(int compactions, int? turns, bool thrash) =>
        Assert.Equal(thrash, ContextThrash.IsThrash(compactions, turns));

    [Theory]
    [InlineData(PromptFailureKind.MaxTurns, true)]
    [InlineData(PromptFailureKind.Timeout, true)]
    [InlineData(PromptFailureKind.Stalled, true)]
    [InlineData(PromptFailureKind.Error, true)]
    [InlineData(PromptFailureKind.OutputCap, false)]
    [InlineData(PromptFailureKind.ContextExhausted, false)]
    [InlineData(PromptFailureKind.ContextOverflow, false)]
    [InlineData(PromptFailureKind.Transient, false)]
    [InlineData(PromptFailureKind.RunnerConfiguration, false)]
    public void OnlyABudgetOrGenericFailure_IsDiagnosed_NeverASuccessOrAMoreSpecificCause(PromptFailureKind kind, bool diagnosed)
    {
        ActionRun failed = ActionRun.FromPrompt(Thrashing(kind), needsHuman: null);
        Assert.Equal(diagnosed, ContextThrash.Diagnose(failed) is not null);

        ActionRun succeeded = ActionRun.FromPrompt(
            new PromptResult { Completed = true, IsError = false, NumTurns = 20, Compactions = new CompactionCounts(9, 0), Summary = "done" },
            needsHuman: null);
        Assert.Null(ContextThrash.Diagnose(succeeded));
    }

    // ─── feedback text ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheThrashFeedback_NamesTheCause_AndDoesNotBlameTheTurnBudget()
    {
        var thrash = new ContextThrash(9, 1, 76);
        string feedback = RetryPolicy.ForContextThrash(
            TaskFor("02-x"), attempt: 3, thrash, PromptFailureKind.MaxTurns, ThrashFixture.GatewayBlock());

        Assert.Contains(RetryPolicy.ContextThrashHeading, feedback, StringComparison.Ordinal);
        Assert.Contains("ran out of turns", feedback, StringComparison.Ordinal);
        Assert.Contains("9 compactions in 76 turns, 1 failed", feedback, StringComparison.Ordinal);
        Assert.Contains("NOT raised either", feedback, StringComparison.Ordinal);
        Assert.Contains("offset and a limit", feedback, StringComparison.Ordinal);
        Assert.Contains("contextTokens (now 65,536)", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISED the turn budget", feedback, StringComparison.Ordinal);
    }

    // ─── the executor ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMaxTurnsAttemptAboveTheThreshold_IsContextThrash_AndTheTurnBudgetIsNotRaised()
    {
        string root = ThrashFixture.NewRoot();
        try
        {
            PlanDefinition plan = ThrashFixture.WritePlan(root, retries: 1);
            PromptResult thrashed = MaxTurns(compactions: 9, failures: 1, turns: 76);
            var runner = new SequenceRunner(thrashed, thrashed);

            RunReport report = await ThrashFixture.RunSerialAsync(plan, runner, Ct);

            Assert.Equal(2, runner.Invocations.Count);
            Assert.Equal(75, runner.Invocations[0].Settings.MaxTurns);
            Assert.Equal(75, runner.Invocations[1].Settings.MaxTurns);

            string feedback = ThrashFixture.AttemptFile(root, "feedback.md", attempt: 1);
            Assert.Contains(RetryPolicy.ContextThrashHeading, feedback, StringComparison.Ordinal);
            Assert.DoesNotContain("## The previous attempt ran out of turns", feedback, StringComparison.Ordinal);
            Assert.DoesNotContain(RetryPolicy.ContextManagementHeading, feedback, StringComparison.Ordinal);

            JsonElement first = ThrashFixture.Attempts(plan)[0];
            Assert.Equal("max-turns", first.GetProperty("outcome").GetString());
            Assert.Equal(9, first.GetProperty("provenance").GetProperty("compactions").GetInt32());
            Assert.Equal(1, first.GetProperty("provenance").GetProperty("compactionFailures").GetInt32());

            // The settled summary (the final attempt's) names the cause and the operator's levers, never the turn budget.
            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Contains("CONTEXT THRASH (9 compactions in 76 turns, 1 failed)", settled.Summary, StringComparison.Ordinal);
            Assert.Contains("contextTokens (now 65,536)", settled.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("turn budget auto-raised", settled.Summary, StringComparison.Ordinal);
        }
        finally { ThrashFixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task AMaxTurnsAttemptBelowTheThreshold_KeepsTheTurnBudgetDiagnosis_AndRaisesTheBudget()
    {
        string root = ThrashFixture.NewRoot();
        try
        {
            PlanDefinition plan = ThrashFixture.WritePlan(root, retries: 1);
            PromptResult budget = MaxTurns(compactions: 2, failures: 0, turns: 76);
            var runner = new SequenceRunner(budget, budget);

            RunReport report = await ThrashFixture.RunSerialAsync(plan, runner, Ct);

            Assert.True(runner.Invocations[1].Settings.MaxTurns > runner.Invocations[0].Settings.MaxTurns);

            string feedback = ThrashFixture.AttemptFile(root, "feedback.md", attempt: 1);
            Assert.Contains("## The previous attempt ran out of turns", feedback, StringComparison.Ordinal);
            Assert.DoesNotContain(RetryPolicy.ContextThrashHeading, feedback, StringComparison.Ordinal);

            JsonElement first = ThrashFixture.Attempts(plan)[0];
            Assert.Equal(2, first.GetProperty("provenance").GetProperty("compactions").GetInt32());
            Assert.False(first.GetProperty("provenance").TryGetProperty("compactionFailures", out _));

            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Contains("turn budget auto-raised", settled.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("CONTEXT THRASH", settled.Summary, StringComparison.Ordinal);
        }
        finally { ThrashFixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task ATimedOutThrash_DoesNotExtendTheClock_AndTheFinalSummaryNamesTheLevers()
    {
        string root = ThrashFixture.NewRoot();
        try
        {
            PlanDefinition plan = ThrashFixture.WritePlan(root, retries: 1);
            var timedOut = new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Timeout,
                Compactions = new CompactionCounts(7, 1),
                Summary = "timed out"
            };
            var runner = new SequenceRunner(timedOut, timedOut);

            RunReport report = await ThrashFixture.RunSerialAsync(plan, runner, Ct);

            Assert.Equal(runner.Invocations[0].Timeout, runner.Invocations[1].Timeout);
            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Equal(TaskOutcome.ActionFailed, settled.Outcome);
            Assert.Contains("CONTEXT THRASH (7 compactions, 1 failed)", settled.Summary, StringComparison.Ordinal);
            Assert.Contains("contextTokens (now 65,536)", settled.Summary, StringComparison.Ordinal);
        }
        finally { ThrashFixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task ASuccessfulAttemptThatCompacted_RecordsItsCounts_AndANonCompactingOneRecordsNoKey()
    {
        string root = ThrashFixture.NewRoot();
        try
        {
            PlanDefinition plan = ThrashFixture.WritePlan(root, retries: 0);
            var runner = new SequenceRunner(Done() with { Compactions = new CompactionCounts(2, 0) });
            await ThrashFixture.RunSerialAsync(plan, runner, Ct);
            JsonElement provenance = ThrashFixture.Attempts(plan)[0].GetProperty("provenance");
            Assert.Equal(2, provenance.GetProperty("compactions").GetInt32());
        }
        finally { ThrashFixture.DeleteBestEffort(root); }

        root = ThrashFixture.NewRoot();
        try
        {
            PlanDefinition plan = ThrashFixture.WritePlan(root, retries: 0);
            await ThrashFixture.RunSerialAsync(plan, new SequenceRunner(Done()), Ct);
            JsonElement provenance = ThrashFixture.Attempts(plan)[0].GetProperty("provenance");
            Assert.False(provenance.TryGetProperty("compactions", out _));
        }
        finally { ThrashFixture.DeleteBestEffort(root); }
    }

    // ─── journal, event row, bundle ────────────────────────────────────────────────────

    [Fact]
    public void TheProvenanceFields_RoundTrip_AndAPre817ProvenanceStillLoads()
    {
        var with = new AttemptProvenance { Model = "Qwen", Compactions = 9, CompactionFailures = 1 };
        string json = JsonSerializer.Serialize(with, JournalJson.Options);
        Assert.Contains("\"compactions\": 9", json, StringComparison.Ordinal);
        Assert.Contains("\"compactionFailures\": 1", json, StringComparison.Ordinal);
        Assert.Equal(with, JsonSerializer.Deserialize<AttemptProvenance>(json, JournalJson.Options));

        string without = JsonSerializer.Serialize(new AttemptProvenance { Model = "Qwen" }, JournalJson.Options);
        Assert.DoesNotContain("compaction", without, StringComparison.Ordinal);

        AttemptProvenance old = JsonSerializer.Deserialize<AttemptProvenance>("""{ "model": "claude-x", "runner": "claude" }""", JournalJson.Options)!;
        Assert.Null(old.Compactions);
        Assert.Null(old.CompactionFailures);
    }

    [Fact]
    public void TheAttemptFinishedRow_CarriesTheCounts_OnlyWhenTheAttemptCompacted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr817-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var observer = new RunEventStream(IRunObserver.Null, dir, "run-817");
            TaskNode task = TaskFor("01-task");
            ((IRunObserver)observer).AttemptFinished(task, Record(1, new AttemptProvenance { Model = "Qwen", Compactions = 9, CompactionFailures = 1 }));
            ((IRunObserver)observer).AttemptFinished(task, Record(2, new AttemptProvenance { Model = "Qwen" }));

            string[] lines = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
            using (JsonDocument first = JsonDocument.Parse(lines[0]))
            {
                Assert.Equal(9, first.RootElement.GetProperty("compactions").GetInt32());
                Assert.Equal(1, first.RootElement.GetProperty("compactionFailures").GetInt32());
            }

            using JsonDocument second = JsonDocument.Parse(lines[1]);
            Assert.False(second.RootElement.TryGetProperty("compactions", out _));
            Assert.False(second.RootElement.TryGetProperty("compactionFailures", out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void TheBundleSummary_ShowsTheCountsInTheOutcomeCell_AndNothingForAnAttemptThatDidNotCompact()
    {
        using var fixture = new BundlePlanFixture();
        AttemptRecord thrashed = BundlePlanFixture.Attempt(1, AttemptOutcome.MaxTurns) with
        {
            Turns = 76,
            Provenance = new AttemptProvenance { Model = "fixture-model", Runner = "claude", Compactions = 9, CompactionFailures = 1 }
        };
        fixture.WriteJournal(BundlePlanFixture.Journal(
            secondStatus: JournalTaskStatus.Failed,
            secondAttempts: [thrashed, BundlePlanFixture.Attempt(2, AttemptOutcome.ActionFailed)]));
        fixture.PromptAttempt("02-second", 1, "one");
        fixture.PromptAttempt("02-second", 2, "two");

        string summary = fixture.Build().Text("SUMMARY.md")!;

        Assert.Contains("| 1 | max-turns (9 compactions in 76 turns, 1 failed) |", summary, StringComparison.Ordinal);
        Assert.Contains("| 2 | action-failed |", summary, StringComparison.Ordinal);
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────────

    private static CompactionCounts Parse(IEnumerable<string> lines) =>
        ClaudeStreamParser.ParseAll(string.Join('\n', lines)).Compactions!;

    private static IEnumerable<string> Repeat(string line, int count) => Enumerable.Repeat(line, count);

    private static PromptResult Thrashing(PromptFailureKind kind) => new()
    {
        Completed = false,
        IsError = true,
        FailureKind = kind,
        NumTurns = 40,
        Compactions = new CompactionCounts(9, 0),
        Summary = "failed"
    };

    private static PromptResult MaxTurns(int compactions, int failures, int turns) => new()
    {
        Completed = true,
        IsError = true,
        FailureKind = PromptFailureKind.MaxTurns,
        NumTurns = turns,
        Compactions = new CompactionCounts(compactions, failures),
        ContextManagement = failures > 0
            ? new ContextManagementFailure(ContextManagementFailureKind.CompactionFailed, "summarization produced empty response", failures)
            : null,
        Summary = $"hit the max-turns cap ({turns} turns)"
    };

    private static PromptResult Done() => new() { Completed = true, IsError = false, Summary = "done" };

    private static AttemptRecord Record(int attempt, AttemptProvenance provenance) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = AttemptOutcome.MaxTurns,
        LogDir = $"logs/x/attempt-{attempt}",
        Turns = 76,
        Provenance = provenance
    };

    private static TaskNode TaskFor(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = "fixture",
        Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
        Guardrails = []
    };

    private sealed class SequenceRunner(params PromptResult[] results) : IPromptRunner
    {
        public List<PromptInvocation> Invocations { get; } = [];

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(results[Math.Min(Invocations.Count, results.Length) - 1]);
        }
    }
}

/// <summary>A one-task plan on a claude GATEWAY block (contextTokens 65536), run serially through the real executor.</summary>
file static class ThrashFixture
{
    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "gr817-" + Guid.NewGuid().ToString("N"));

    public static PromptRunnerConfig GatewayBlock() => new()
    {
        Name = "qwen", Command = "claude", Kind = PromptRunnerKind.Claude, BaseUrl = "http://127.0.0.1:4000",
        ContextTokens = 65536, Settings = new PromptRunnerSettings()
    };

    public static PlanDefinition WritePlan(string root, int retries)
    {
        string planDir = Path.Combine(root, "plan");
        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultTimeoutSeconds": 3600,
              "defaultRetries": {{retries}},
              "promptRunners": { "default": "qwen", "qwen": { "command": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen", "contextTokens": 65536, "maxTurns": 75 } }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "thrash fixture", "dependsOn": [], "writeScope": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        string check = Path.Combine(taskDir, "guardrails", OperatingSystem.IsWindows() ? "01-check.ps1" : "01-check.sh");
        Write(check, OperatingSystem.IsWindows() ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
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

    public static async Task<RunReport> RunSerialAsync(PlanDefinition plan, IPromptRunner runner, CancellationToken ct)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        return await new Scheduler(plan, executor, journal, maxParallelism: 1).RunAsync(plan, ct);
    }

    /// <summary>The task's journalled attempts, read from the bytes of <c>run.json</c>.</summary>
    public static JsonElement[] Attempts(PlanDefinition plan)
    {
        using JsonDocument run = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(plan.PlanDirectory)));
        return [.. run.RootElement.GetProperty("tasks").GetProperty("01-task").GetProperty("attempts").EnumerateArray()
            .Select(a => a.Clone())];
    }

    /// <summary>One attempt's log file, by name.</summary>
    public static string AttemptFile(string root, string name, int attempt)
    {
        string marker = $"attempt-{attempt}";
        string path = Assert.Single(
            Directory.GetFiles(root, name, SearchOption.AllDirectories),
            p => Path.GetFileName(Path.GetDirectoryName(p)) == marker);
        return File.ReadAllText(path);
    }

    public static void DeleteBestEffort(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
