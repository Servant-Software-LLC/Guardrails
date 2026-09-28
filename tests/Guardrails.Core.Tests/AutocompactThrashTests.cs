using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// #800: Claude Code's "Autocompact is thrashing" give-up is recognized, ends the attempt at once, gets targeted retry
/// feedback, and escalates on repeat. The stream shapes come from a real capture (<c>TestData/claude-live/
/// autocompact-thrash.jsonl</c>: the last two lines of a local-Qwen gateway attempt on the maintainer's machine, with
/// the session id, uuids and per-model usage removed).
/// </summary>
public sealed class AutocompactThrashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] RealLines() =>
        File.ReadAllLines(TestPaths.Fixture(Path.Combine("claude-live", "autocompact-thrash.jsonl")));

    // ─── recognition ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRealGiveUpIsRecognized_FromTheAssistantLineAndFromTheResultLine()
    {
        string[] lines = RealLines();

        ClaudeResult both = ClaudeStreamParser.ParseAll(string.Join('\n', lines), recognizeThrash: true);
        ContextManagementFailure thrash = Assert.IsType<ContextManagementFailure>(both.Thrashing);
        Assert.Equal(ContextManagementFailureKind.AutocompactThrashing, thrash.Kind);
        Assert.Equal("autocompact-thrashing", thrash.Token);
        Assert.StartsWith("Autocompact is thrashing", thrash.Detail, StringComparison.Ordinal);

        // Either line alone is enough: the CLI does not always get as far as its result.
        Assert.NotNull(ClaudeStreamParser.ParseAll(lines[0], recognizeThrash: true).Thrashing);
        Assert.NotNull(ClaudeStreamParser.ParseAll(lines[1], recognizeThrash: true).Thrashing);

        // Only the claude dialect recognises it: a parser that was not asked to (Cursor's) never reports it.
        Assert.Null(ClaudeStreamParser.ParseAll(string.Join('\n', lines)).Thrashing);

        var parser = new ClaudeStreamParser(recognizeThrash: true);
        Assert.False(parser.ContextExhausted);
        parser.Feed(lines[0]);
        Assert.True(parser.ContextExhausted);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Autocompact is thrashing: the context refilled"}""", true)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"terminal_reason":"rapid_refill_breaker"}""", true)]
    [InlineData("""{"type":"assistant","api_error":"autocompact_thrashing","message":{"content":[]}}""", true)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: 500"}""", false)]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"Autocompact is thrashing detection is now wired into the parser."}""", false)]
    [InlineData("""{"type":"result","subtype":"success","result":"Autocompact is thrashing: the context refilled"}""", false)]
    [InlineData("""{"type":"assistant","api_error":"autocompact_thrashing","parent_tool_use_id":"toolu_sub","message":{"content":[]}}""", false)]
    [InlineData("""{"type":"assistant","api_error":"autocompact_thrashing","parent_tool_use_id":null,"message":{"content":[]}}""", true)]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"Autocompact is thrashing, apparently"}]}}""", false)]
    [InlineData("""{"type":"system","subtype":"status","status":"compacting"}""", false)]
    public void OnlyTheStructuredSignalsOrTheResultTextCount(string line, bool thrashing) =>
        Assert.Equal(thrashing, ClaudeStreamParser.ParseAll(line, recognizeThrash: true).Thrashing is not null);

    // ─── feedback and levers ────────────────────────────────────────────────────────────

    [Fact]
    public void TheRetryIsToldHowToStayInsideTheWindow()
    {
        string feedback = RetryPolicy.ForContextExhausted(TaskFor("07-x"), attempt: 2);

        Assert.Contains(RetryPolicy.ContextExhaustedHeading, feedback, StringComparison.Ordinal);
        Assert.Contains("offset and a limit", feedback, StringComparison.Ordinal);
        Assert.Contains("Do NOT print whole", feedback, StringComparison.Ordinal);
        Assert.Contains("\"Wasted call", feedback, StringComparison.Ordinal);
        Assert.Contains("cache hit, not an error", feedback, StringComparison.Ordinal);
        Assert.Contains("PARTIAL WORK is preserved", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void InWorktreeMode_TheFeedbackDisclosesTheRollback()
    {
        string feedback = RetryPolicy.ForContextExhausted(TaskFor("07-x"), attempt: 2, fileWritesRolledBack: true);

        Assert.Contains("## File writes were also rolled back", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("PARTIAL WORK is preserved", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeversNameContextTokensWithItsValue_OnALocalBlock_AndTheTaskOnACloudOne()
    {
        var gateway = new PromptRunnerConfig
        {
            Name = "qwen", Command = "claude", Kind = PromptRunnerKind.Claude, BaseUrl = "http://127.0.0.1:4000",
            ContextTokens = 65536, Settings = new PromptRunnerSettings()
        };
        var cloud = new PromptRunnerConfig
        {
            Name = "claude", Command = "claude", Kind = PromptRunnerKind.Claude, Settings = new PromptRunnerSettings()
        };

        string local = RetryPolicy.ContextLevers(gateway);
        Assert.Contains("contextTokens (now 65,536)", local, StringComparison.Ordinal);
        Assert.Contains("Bash(dotnet *)", local, StringComparison.Ordinal);

        string hosted = RetryPolicy.ContextLevers(cloud);
        Assert.DoesNotContain("contextTokens", hosted, StringComparison.Ordinal);
        Assert.StartsWith("Split the task", hosted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContextTokensFigureIsTheSameInEveryCulture()
    {
        var gateway = new PromptRunnerConfig
        {
            Name = "qwen", Command = "claude", Kind = PromptRunnerKind.Claude, BaseUrl = "http://127.0.0.1:4000",
            ContextTokens = 65536, Settings = new PromptRunnerSettings()
        };
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("(now 65,536)", RetryPolicy.ContextLevers(gateway), StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ─── the executor: classified, retried with targeted feedback, escalated on repeat ──

    [Fact]
    public async Task OneThrash_IsClassified_AndRetriedWithTargetedFeedback()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, retries: 1);
            var runner = new SequenceRunner(
                Thrashed() with { InFlightToolCalls = [new InFlightToolCall("Bash", "cat -n src/Big.cs")] }, Succeeded());

            RunReport report = await Fixture.RunSerialAsync(plan, runner, Ct);

            Assert.Equal(2, runner.Invocations.Count);
            Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);

            // Never a timeout: the retry runs on the same clock, not an extended one.
            Assert.Equal(runner.Invocations[0].Timeout, runner.Invocations[1].Timeout);

            string feedback = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "feedback.md", SearchOption.AllDirectories)));
            Assert.Contains(RetryPolicy.ContextExhaustedHeading, feedback, StringComparison.Ordinal);
            Assert.DoesNotContain(RetryPolicy.ContextManagementHeading, feedback, StringComparison.Ordinal);
            Assert.Contains(RetryPolicy.InFlightCallsHeading, feedback, StringComparison.Ordinal);
            Assert.Contains("cat -n src/Big.cs", feedback, StringComparison.Ordinal);

            string actionResult = File.ReadAllText(
                Assert.Single(Directory.GetFiles(root, "action-result.json", SearchOption.AllDirectories),
                    path => path.Contains("attempt-1", StringComparison.Ordinal)));
            Assert.Contains("context exhausted", actionResult, StringComparison.Ordinal);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task TwoThrashesInARow_SettleNeedsHuman_WithTheLevers()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, retries: 2);
            var runner = new SequenceRunner(Thrashed(), Thrashed(), Thrashed());

            RunReport report = await Fixture.RunSerialAsync(plan, runner, Ct);

            Assert.Equal(2, runner.Invocations.Count);
            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Equal(TaskOutcome.NeedsHuman, settled.Outcome);
            Assert.Contains("second consecutive attempt that ran out of context", settled.Summary, StringComparison.Ordinal);
            Assert.Contains("contextTokens (now 65,536)", settled.Summary, StringComparison.Ordinal);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    [Fact]
    public async Task AThrashCountResets_WhenAnAttemptEndsAnotherWay()
    {
        string root = Fixture.NewRoot();
        try
        {
            PlanDefinition plan = Fixture.WritePlan(root, retries: 2);
            var error = new PromptResult { Completed = true, IsError = true, FailureKind = PromptFailureKind.Error, Summary = "error" };
            var runner = new SequenceRunner(Thrashed(), error, Thrashed());

            RunReport report = await Fixture.RunSerialAsync(plan, runner, Ct);

            Assert.Equal(3, runner.Invocations.Count);
            Assert.Equal(TaskOutcome.ActionFailed, Assert.Single(report.Tasks).Outcome);
        }
        finally { Fixture.DeleteBestEffort(root); }
    }

    private static PromptResult Thrashed() => new()
    {
        Completed = false,
        IsError = true,
        FailureKind = PromptFailureKind.ContextExhausted,
        ContextManagement = new ContextManagementFailure(
            ContextManagementFailureKind.AutocompactThrashing, "Autocompact is thrashing: …", 1),
        Summary = "context exhausted — Claude Code reported that autocompact is thrashing; the harness ended the session at once"
    };

    private static PromptResult Succeeded() => new() { Completed = true, IsError = false, Summary = "done" };

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
file static class Fixture
{
    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "gr-thrash-" + Guid.NewGuid().ToString("N"));

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
              "promptRunners": { "default": "qwen", "qwen": { "command": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen", "contextTokens": 65536 } }
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
