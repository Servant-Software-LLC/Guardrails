using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// #811: task actions get a silence bound, only PROGRESS lines restart it, a failed compaction is classified, and
/// the stall verdict is persisted. Every test asserts a DECISION (a verdict, a resolved bound, a classification),
/// never a duration: the watchdog runs on an injected clock, so nothing here sleeps.
/// </summary>
public sealed class ActionStallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The shapes from the #811 evidence (claude-stream.jsonl lines 163–236 of the stuck run).
    private const string Compacting =
        """{"type":"system","subtype":"status","status":"compacting","session_id":"s","uuid":"u"}""";

    private const string CompactFailed =
        """{"type":"system","subtype":"status","status":null,"compact_result":"failed","compact_error":"Request timed out","session_id":"s"}""";

    private const string ToolUse =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet build"}}]}}""";

    private const string ToolResult =
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"}]}}""";

    // ─── the resolved bound ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 30 * 60, 15 * 60)]      // timeout/3 = 10m, raised to the 15m floor
    [InlineData(null, 45 * 60, 15 * 60)]      // timeout/3 = 15m exactly
    [InlineData(null, 54 * 60, 18 * 60)]      // timeout/3 inside the band
    [InlineData(null, 60 * 60, 20 * 60)]      // timeout/3 = 20m exactly
    [InlineData(null, 4 * 3600, 20 * 60)]     // capped at the 20m ceiling
    [InlineData(null, 15 * 60, null)]         // the floor is not shorter than the timeout: no bound
    [InlineData(null, 60, null)]              // a one-minute action: the timeout already bounds silence
    [InlineData(0, 4 * 3600, null)]           // 0 disables
    [InlineData(600, 4 * 3600, 600)]          // configured, used as given
    [InlineData(60, 30 * 60, 60)]             // configured below the floor: the operator's choice stands
    [InlineData(7200, 3600, null)]            // configured but not shorter than the timeout: no bound
    public void TheBoundResolvesFromTheConfigAndTheTimeout(int? configured, int timeoutSeconds, int? expectedSeconds)
    {
        TimeSpan? bound = ActionStallBound.Resolve(configured, TimeSpan.FromSeconds(timeoutSeconds));

        Assert.Equal(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null, bound);
    }

    [Fact]
    public void TheDerivedBoundFollowsTheExtendedTimeout_AConfiguredOneDoesNot()
    {
        // A 36-minute base timeout after one prior timeout (1.5x) runs for 54 minutes: the derived bound is 18m, not 12m
        // raised to 15m. A configured bound is an absolute and does not scale.
        TimeSpan extended = TimeSpan.FromMinutes(36 * TaskExecutor.TimeoutMultiplierFor(1));

        Assert.Equal(TimeSpan.FromMinutes(18), ActionStallBound.Resolve(null, extended));
        Assert.Equal(TimeSpan.FromMinutes(10), ActionStallBound.Resolve(600, extended));
    }

    // ─── which lines are progress ────────────────────────────────────────────────────────

    public static TheoryData<string, bool> StreamJsonLines => new()
    {
        { Compacting, false },
        { CompactFailed, false },
        { """{"type":"system","subtype":"init","model":"claude-opus-5-5"}""", false },
        { """{"type":"system","subtype":"compact_boundary"}""", false },
        { """{"type":"system","subtype":"hook_response"}""", false },
        { """{"type":"user","message":{"content":[{"type":"text","text":"This session is being continued"}]}}""", false },
        { """{"type":"rate_limit_event"}""", false },
        { "not json at all", false },
        { "", false },
        { "[1,2]", false },
        { ToolUse, true },
        { """{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it."}]}}""", true },
        { ToolResult, true },
        { """{"type":"result","subtype":"success","is_error":false,"result":"done"}""", true },
        { """{"type":"stream_event","event":{"type":"content_block_delta"}}""", true },
        { """{"type":"tool_call","subtype":"started","call_id":"c1"}""", true },
        { """{"type":"thinking","subtype":"delta","text":"hmm"}""", true },
    };

    [Theory]
    [MemberData(nameof(StreamJsonLines))]
    public void AStreamJsonLineIsProgressOnlyWhenItIsModelOutputOrAToolEvent(string line, bool expected) =>
        Assert.Equal(expected, StreamProgress.IsStreamJsonProgress(line));

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}", true)]
    [InlineData("data: [DONE]", true)]
    [InlineData("{\"choices\":[]}", true)]   // a non-streamed whole body
    [InlineData(": keep-alive", false)]
    [InlineData(":", false)]
    [InlineData("", false)]
    [InlineData("data:", false)]
    [InlineData("data:   ", false)]
    [InlineData("event: ping", false)]
    [InlineData("id: 7", false)]
    [InlineData("retry: 1000", false)]
    public void AnSseLineIsProgressOnlyWhenItCarriesData(string line, bool expected) =>
        Assert.Equal(expected, StreamProgress.IsSseProgress(line));

    // ─── the watchdog's decision on a stream ─────────────────────────────────────────────

    [Fact]
    public void AStreamOfOnlyCompactingLinesStalls()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(15), clock.Now);

        StallVerdict verdict = StallVerdict.KeepWaiting;
        int polls = 0;
        while (verdict != StallVerdict.Stalled && polls < 100)
        {
            clock.Advance(watch.PollInterval);
            StreamProgress.BeatOnStreamJsonProgress(watch, Compacting);
            verdict = watch.Observe();
            polls++;
        }

        Assert.Equal(StallVerdict.Stalled, verdict);
        Assert.True(watch.Stalled);

        // Exactly when the bound elapsed (15m at a 45s poll = 20 polls), with no suspend discounted on the way.
        Assert.Equal(20, polls);
        Assert.Equal(0, watch.SuspendsObserved);
    }

    [Fact]
    public void InterleavedToolUseLinesKeepTheAttemptAlive()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(15), clock.Now);

        // Two hours of compacting status lines every poll, with a tool call every 10 polls (7.5 minutes).
        for (int poll = 1; poll <= 160; poll++)
        {
            clock.Advance(watch.PollInterval);
            StreamProgress.BeatOnStreamJsonProgress(watch, Compacting);
            if (poll % 10 == 0)
            {
                StreamProgress.BeatOnStreamJsonProgress(watch, poll % 20 == 0 ? ToolResult : ToolUse);
            }

            Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());
        }

        Assert.False(watch.Stalled);
    }

    [Fact]
    public void AStatusLineNeverBeats()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(15), clock.Now);

        clock.Advance(TimeSpan.FromMinutes(7));
        StreamProgress.BeatOnStreamJsonProgress(watch, Compacting);
        StreamProgress.BeatOnStreamJsonProgress(watch, CompactFailed);
        StreamProgress.BeatOnStreamJsonProgress(watch, """{"type":"system","subtype":"init"}""");

        Assert.Equal(TimeSpan.FromMinutes(7), watch.SilentFor());

        StreamProgress.BeatOnStreamJsonProgress(watch, ToolUse);

        Assert.Equal(TimeSpan.Zero, watch.SilentFor());
    }

    [Fact]
    public void SseKeepAliveCommentsAloneStall()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(15), clock.Now);

        StallVerdict verdict = StallVerdict.KeepWaiting;
        for (int poll = 0; poll < 100 && verdict != StallVerdict.Stalled; poll++)
        {
            clock.Advance(watch.PollInterval);
            if (StreamProgress.IsSseProgress(": keep-alive"))
            {
                watch.Beat();
            }

            verdict = watch.Observe();
        }

        Assert.Equal(StallVerdict.Stalled, verdict);
    }

    // ─── the compaction classification ───────────────────────────────────────────────────

    [Fact]
    public void AFailedCompactionIsClassified_WithTheRunnersErrorAndACount()
    {
        string stream = string.Join('\n', Enumerable.Repeat(Compacting, 70).Append(CompactFailed).Append(Compacting).Append(CompactFailed));

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        ContextManagementFailure failure = Assert.IsType<ContextManagementFailure>(result.CompactionFailure);
        Assert.Equal(ContextManagementFailureKind.CompactionFailed, failure.Kind);
        Assert.Equal("Request timed out", failure.Detail);
        Assert.Equal(2, failure.Count);
        Assert.Equal("compaction-failed", failure.Token);
        Assert.Equal("context compaction failed 2 times (Request timed out)", failure.Describe());
        Assert.False(result.HasResult);
    }

    [Fact]
    public void CompactingWithoutAFailureIsNotAFailure()
    {
        ClaudeResult result = ClaudeStreamParser.ParseAll(string.Join('\n', Enumerable.Repeat(Compacting, 5)));

        Assert.Null(result.CompactionFailure);
    }

    // ─── GR2088 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCodeIsGr2088() => Assert.Equal("GR2088", DiagnosticCodes.StallTimeoutNegative);

    [Theory]
    [InlineData("-1", true)]
    [InlineData("0", false)]
    [InlineData("900", false)]
    public void ANegativeStallTimeoutIsGr2088(string value, bool expectError)
    {
        string root = Fixture.NewRoot();
        try
        {
            string planDir = Fixture.WritePlanFiles(root, $$"""{ "command": "stub", "stallTimeoutSeconds": {{value}} }""", 60);
            PlanLoadResult load = new PlanLoader().Load(planDir);
            List<Diagnostic> diagnostics = [.. load.Diagnostics];
            if (load.Plan is not null)
            {
                diagnostics.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan));
            }

            if (expectError)
            {
                Diagnostic error = Assert.Single(diagnostics, d => d.Code == "GR2088");
                Assert.Equal(DiagnosticSeverity.Error, error.Severity);
                Assert.Contains("promptRunners.stub.stallTimeoutSeconds is -1", error.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(diagnostics, d => d.Code == "GR2088");
            }
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    // ─── the bound reaches the invocation, from the DISPATCHED block ─────────────────────

    [Theory]
    [InlineData("\"stallTimeoutSeconds\": 30,", 60, 30)]
    [InlineData("", 3600, 20 * 60)]
    [InlineData("", 60, null)]
    [InlineData("\"stallTimeoutSeconds\": 0,", 3600, null)]
    public async Task TheConfiguredOrDerivedBoundReachesTheActionInvocation(string key, int timeoutSeconds, int? expectedSeconds)
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, $$"""{ {{key}} "command": "stub" }""", timeoutSeconds);
            var runner = new CapturingRunner(Succeeded());

            await Fixture.RunSerialAsync(plan, runner, new RecordingObserver(), Ct);

            PromptInvocation invocation = Assert.Single(runner.Invocations);
            Assert.Equal(PromptRole.Action, invocation.Role);
            Assert.Equal(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null, invocation.StallBound);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task TheBoundComesFromTheBlockTheActionIsDispatchedTo()
    {
        string root = Fixture.NewRoot();
        try
        {
            // The default block has no key; the task pins "slow", whose 900s must be the one applied.
            PlanDefinition plan = Fixture.WritePlan(
                root, """{ "command": "stub" }""", 3600,
                extraBlocks: """, "slow": { "command": "stub", "stallTimeoutSeconds": 900 }""",
                actionRunner: "slow");
            var runner = new CapturingRunner(Succeeded());

            await Fixture.RunSerialAsync(plan, runner, new RecordingObserver(), Ct);

            Assert.Equal(TimeSpan.FromSeconds(900), Assert.Single(runner.Invocations).StallBound);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    // ─── a stalled attempt: the verdict row, the summary, the feedback ───────────────────

    [Fact]
    public async Task AStalledAttemptPersistsTheVerdict_AndNamesTheCompactionFailure()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, """{ "command": "stub" }""", 3600);
            var stalled = new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Stalled,
                Stall = new StallReport(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20.5), 1),
                ContextManagement = new ContextManagementFailure(
                    ContextManagementFailureKind.CompactionFailed, "Request timed out", 1),
                Summary = "STALLED — no progress (model output, tool call or tool result) for 20.5m (bound 20m); the session was killed."
            };
            var observer = new RecordingObserver();

            RunReport report = await Fixture.RunSerialAsync(plan, new CapturingRunner(stalled), observer, Ct);

            var call = Assert.Single(observer.Stalls);
            Assert.Equal(("01-task", 1), (call.TaskId, call.Attempt));
            Assert.Equal(TimeSpan.FromMinutes(20), call.Bound);
            Assert.Equal(TimeSpan.FromMinutes(20.5), call.SilentFor);
            Assert.Equal(1, call.Suspends);
            Assert.Equal("compaction-failed", call.Context);
            Assert.Equal("Request timed out", call.Detail);

            // Journalled as an ordinary action failure, never a timeout: a stall must not extend the next clock.
            Assert.Equal(AttemptOutcome.ActionFailed, Assert.Single(observer.Outcomes));

            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Contains("STALLED", settled.Summary, StringComparison.Ordinal);
            Assert.Contains("context compaction failed (Request timed out)", settled.Summary, StringComparison.Ordinal);

            string feedback = File.ReadAllText(
                Assert.Single(Directory.GetFiles(root, "feedback.md", SearchOption.AllDirectories)));
            Assert.Contains(RetryPolicy.StallHeading, feedback, StringComparison.Ordinal);
            Assert.Contains("20.5 minutes", feedback, StringComparison.Ordinal);
            Assert.Contains(RetryPolicy.ContextManagementHeading, feedback, StringComparison.Ordinal);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task AFailedCompactionIsNamedOnAnyFailedAttempt_AndIgnoredOnASucceededOne()
    {
        string root = Fixture.NewRoot();
        try
        {
            var context = new ContextManagementFailure(ContextManagementFailureKind.CompactionFailed, "Request timed out", 1);

            PlanDefinition failingPlan = Fixture.WritePlan(root, """{ "command": "stub" }""", 3600);
            var failed = new PromptResult
            {
                Completed = true, IsError = true, FailureKind = PromptFailureKind.Error,
                ContextManagement = context, Summary = "the agent reported an error"
            };
            var observer = new RecordingObserver();
            RunReport report = await Fixture.RunSerialAsync(failingPlan, new CapturingRunner(failed), observer, Ct);

            Assert.Empty(observer.Stalls);
            Assert.Contains("context compaction failed (Request timed out)", Assert.Single(report.Tasks).Summary, StringComparison.Ordinal);

            string root2 = Fixture.NewRoot();
            try
            {
                PlanDefinition okPlan = Fixture.WritePlan(root2, """{ "command": "stub" }""", 3600);
                var observer2 = new RecordingObserver();
                RunReport okReport = await Fixture.RunSerialAsync(okPlan, new CapturingRunner(Succeeded() with { ContextManagement = context }), observer2, Ct);

                Assert.Equal(AttemptOutcome.Succeeded, Assert.Single(observer2.Outcomes));
                Assert.DoesNotContain("compaction", Assert.Single(okReport.Tasks).Summary, StringComparison.Ordinal);
            }
            finally { Fixture.DeleteBestEffort(root2); }
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    // ─── the events.jsonl row ────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheAttemptStalledRowCarriesTheVerdict_AndOmitsWhatItDoesNotKnow()
    {
        string dir = Fixture.NewRoot();
        Directory.CreateDirectory(dir);
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, "run-811");
            TaskNode task = Fixture.FlatTask("01-task");

            stream.AttemptStalled(task, 2, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(1234.4), 1, "compaction-failed", "Request timed out");
            stream.AttemptStalled(task, 3, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), 0, null, null);

            string[] lines = [.. File.ReadAllLines(Path.Combine(dir, "events.jsonl")).Where(l => l.Length > 0)];
            Assert.Equal(2, lines.Length);

            using (JsonDocument first = JsonDocument.Parse(lines[0]))
            {
                JsonElement row = first.RootElement;
                Assert.Equal(RunEventStream.AttemptStalledKind, row.GetProperty("kind").GetString());
                Assert.Equal("attempt-stalled", row.GetProperty("kind").GetString());
                Assert.Equal("01-task", row.GetProperty("taskId").GetString());
                Assert.Equal(2, row.GetProperty("attempt").GetInt32());
                Assert.Equal(1200, row.GetProperty("boundSeconds").GetInt64());
                Assert.Equal(1234, row.GetProperty("silentSeconds").GetInt64());
                Assert.Equal(1, row.GetProperty("suspends").GetInt32());
                Assert.Equal("compaction-failed", row.GetProperty("contextManagement").GetString());
                Assert.Equal("Request timed out", row.GetProperty("detail").GetString());
                Assert.False(row.TryGetProperty("outcome", out _));
            }

            using JsonDocument second = JsonDocument.Parse(lines[1]);
            Assert.Equal(0, second.RootElement.GetProperty("suspends").GetInt32());
            Assert.False(second.RootElement.TryGetProperty("contextManagement", out _));
            Assert.False(second.RootElement.TryGetProperty("detail", out _));
        }
        finally { Fixture.DeleteBestEffort(dir); }
    }

    // ─── fakes ───────────────────────────────────────────────────────────────────────────

    private static PromptResult Succeeded() => new() { Completed = true, IsError = false, Summary = "done" };

    private sealed class FakeClock
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;

        public long Now() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>The only fake the executor tests use (the SSOT §9 seam): records each invocation, returns a fixed result.</summary>
    private sealed class CapturingRunner(PromptResult result) : IPromptRunner
    {
        public List<PromptInvocation> Invocations { get; } = [];

        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, int Attempt, TimeSpan Bound, TimeSpan SilentFor, int Suspends, string? Context, string? Detail)> Stalls { get; } = [];

        public List<AttemptOutcome> Outcomes { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void AttemptFinished(TaskNode task, AttemptRecord record) => Outcomes.Add(record.Outcome);

        public void AttemptStalled(
            TaskNode task, int attempt, TimeSpan bound, TimeSpan silentFor, int suspendsObserved,
            string? contextManagement, string? contextManagementDetail) =>
            Stalls.Add((task.Id, attempt, bound, silentFor, suspendsObserved, contextManagement, contextManagementDetail));
    }
}

/// <summary>A one-task prompt plan on disk, run through a real serial <see cref="Scheduler"/> and <see cref="TaskExecutor"/>.</summary>
file static class Fixture
{
    private const string TaskId = "01-task";

    private static bool Win => OperatingSystem.IsWindows();

    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "gr-action-stall-" + Guid.NewGuid().ToString("N"));

    public static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
        Guardrails = []
    };

    public static string WritePlanFiles(
        string root, string stubBlock, int timeoutSeconds, string extraBlocks = "", string? actionRunner = null)
    {
        string planDir = Path.Combine(root, "plan");
        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultTimeoutSeconds": {{timeoutSeconds}},
              "defaultRetries": 0,
              "promptRunners": { "default": "stub", "stub": {{stubBlock}}{{extraBlocks}} }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", TaskId);
        string runnerPin = actionRunner is null ? string.Empty : $$""", "runner": "{{actionRunner}}" """;
        Write(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "action-stall fixture", "dependsOn": [], "writeScope": [], "action": { "path": "action.prompt.md"{{runnerPin}} } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        string check = Path.Combine(taskDir, "guardrails", Win ? "01-check.ps1" : "01-check.sh");
        Write(check, Win ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(check,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }

    public static PlanDefinition WritePlan(
        string root, string stubBlock, int timeoutSeconds, string extraBlocks = "", string? actionRunner = null)
    {
        PlanLoadResult load = new PlanLoader().Load(WritePlanFiles(root, stubBlock, timeoutSeconds, extraBlocks, actionRunner));
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    public static async Task<RunReport> RunSerialAsync(PlanDefinition plan, IPromptRunner runner, IRunObserver observer, CancellationToken ct)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(plan, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);

        return await new Scheduler(plan, executor, journal, maxParallelism: 1).RunAsync(plan, ct);
    }

    public static void DeleteBestEffort(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
