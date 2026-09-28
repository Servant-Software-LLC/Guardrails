using System.Net;
using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The #815 review fixes on the #811 stall bound. Decisions are asserted on injected clocks; the two wiring tests
/// run real code paths against a live stream (a fake CLI, a fake SSE body) and assert the failure KIND, which a busy
/// machine cannot flip.
/// </summary>
public sealed class StallReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Compacting = """{"type":"system","subtype":"status","status":"compacting"}""";

    // ─── B1: a replay of a REAL session ──────────────────────────────────────────────────

    /// <summary>
    /// <c>TestData/claude-live/stall-replay.jsonl</c> is two trimmed, sanitized segments of real Claude Code captures
    /// (Charter plan 221, run 2026-09-05T11-56-30Z): an 86-line run of <c>system/thinking_tokens</c> between a tool
    /// result and the next assistant message, then a <c>Bash</c> tool call with 20 <c>tool_progress</c> heartbeats
    /// (30 s apart in the capture) before its result. Session ids, uuids, tool ids, text and tool output are replaced.
    /// The captures carry no per-line timestamps, so the replay spaces the thinking lines 5 s apart and the heartbeats
    /// 30 s apart, as captured, against a 2-minute bound: the thinking run lasts about 7 minutes and the tool run 10,
    /// each well past the bound, and both stay alive. A status-only tail then stalls.
    /// </summary>
    [Fact]
    public void ARealSession_StaysAliveThroughLongThinkingAndALongTool_AndStallsOnAStatusOnlyTail()
    {
        string[] lines = File.ReadAllLines(TestPaths.Fixture(Path.Combine("claude-live", "stall-replay.jsonl")));
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(2), clock.Now);

        TimeSpan thinking = TimeSpan.Zero;
        TimeSpan tool = TimeSpan.Zero;
        foreach (string line in lines)
        {
            TimeSpan gap = line.Contains("\"tool_progress\"", StringComparison.Ordinal) ? TimeSpan.FromSeconds(30)
                : line.Contains("\"thinking_tokens\"", StringComparison.Ordinal) ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(1);
            if (gap == TimeSpan.FromSeconds(30)) { tool += gap; }
            if (gap == TimeSpan.FromSeconds(5)) { thinking += gap; }

            AdvanceAndPoll(clock, watch, gap);
            Assert.False(watch.Stalled, $"stalled during the real session at: {line[..Math.Min(80, line.Length)]}");
            StreamProgress.BeatOnStreamJsonProgress(watch, line);
        }

        Assert.True(thinking > TimeSpan.FromMinutes(6), $"the thinking run spans {thinking}");
        Assert.True(tool > TimeSpan.FromMinutes(9), $"the tool run spans {tool}");

        for (int i = 0; i < 60 && !watch.Stalled; i++)
        {
            AdvanceAndPoll(clock, watch, TimeSpan.FromSeconds(5));
            StreamProgress.BeatOnStreamJsonProgress(watch, Compacting);
        }

        Assert.True(watch.Stalled);
    }

    // ─── W1: local backends get the larger band ─────────────────────────────────────────

    [Theory]
    [InlineData(null, 3600, 30 * 60)]       // timeout/2 = 30m, the floor
    [InlineData(null, 5400, 45 * 60)]       // inside the band
    [InlineData(null, 4 * 3600, 60 * 60)]   // capped at 60m
    [InlineData(null, 1800, null)]          // the 30m floor is not shorter than a 30m timeout
    [InlineData(900, 3600, 900)]            // a configured bound wins
    [InlineData(0, 3600, null)]
    public void ALocalBackendGetsTheLargerDerivedBand(int? configured, int timeoutSeconds, int? expectedSeconds)
    {
        TimeSpan? bound = ActionStallBound.Resolve(configured, TimeSpan.FromSeconds(timeoutSeconds), localBackend: true);

        Assert.Equal(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null, bound);
    }

    [Fact]
    public void AGatewayOrOpenAiCompatBlockIsALocalBackend_APlainClaudeOrCursorBlockIsNot()
    {
        Assert.True(ActionStallBound.IsLocalBackend(Block(PromptRunnerKind.Claude, baseUrl: "http://127.0.0.1:4000")));
        Assert.True(ActionStallBound.IsLocalBackend(Block(PromptRunnerKind.OpenAiCompat)));
        Assert.False(ActionStallBound.IsLocalBackend(Block(PromptRunnerKind.Claude)));
        Assert.False(ActionStallBound.IsLocalBackend(Block(PromptRunnerKind.Cursor)));

        Assert.Equal(TimeSpan.FromMinutes(30), ActionStallBound.Resolve(
            Block(PromptRunnerKind.Claude, baseUrl: "http://127.0.0.1:4000"), TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromMinutes(20), ActionStallBound.Resolve(Block(PromptRunnerKind.Claude), TimeSpan.FromHours(1)));
    }

    // ─── W2: partial suspends are credited ──────────────────────────────────────────────

    /// <summary>
    /// Seven DarkWake cycles (30 s awake, 2.5 min asleep): 21 minutes of wall time against a 20-minute bound, with
    /// only 3.5 minutes awake. Each sleep lands inside one 60 s poll, a gap of 3.5x the interval: short of the 4x
    /// reset, so before W2 every minute of it counted as silence and the session was killed.
    /// </summary>
    [Fact]
    public void SevenDarkWakeCycles_DoNotStall()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(20), clock.Now);
        Assert.Equal(TimeSpan.FromMinutes(1), watch.PollInterval);

        for (int cycle = 0; cycle < 7; cycle++)
        {
            // 30 s awake, then the 2.5 min sleep lands inside the next poll: one gap of 3.5 minutes.
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());
            clock.Advance(TimeSpan.FromMinutes(3.5));
            Assert.Equal(StallVerdict.KeepWaiting, watch.Observe());
        }

        Assert.False(watch.Stalled);
        Assert.Equal(0, watch.SuspendsObserved);
        Assert.Equal(TimeSpan.FromMinutes(2.5 * 7), watch.Credited);
    }

    [Fact]
    public void AnOrdinaryPollIsNotCredited()
    {
        var clock = new FakeClock();
        var watch = new StallWatch(TimeSpan.FromMinutes(20), clock.Now);

        for (int i = 0; i < 21; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(80)); // 1.33x the interval: scheduling delay, not a suspend
            watch.Observe();
        }

        Assert.Equal(TimeSpan.Zero, watch.Credited);
        Assert.True(watch.Stalled);
    }

    // ─── W3: the openai-compat SSE wiring ───────────────────────────────────────────────

    /// <summary>
    /// A server that answers with headers and then only SSE keep-alive comments. If the SSE reader went back to beating
    /// on every line, the comments would keep the turn alive until the 60 s timeout; beating only on data frames, it is
    /// abandoned as STALLED once the 2 s bound passes, with no progress at all.
    /// </summary>
    [Fact]
    public async Task SseKeepAlivesAlone_AreStalled_NotTimedOut()
    {
        var config = new PromptRunnerConfig
        {
            Name = "local",
            Command = "local",
            Kind = PromptRunnerKind.OpenAiCompat,
            Endpoint = "http://stall-review-tests.invalid/v1",
            ContextTokens = 1_000_000,
            Settings = new PromptRunnerSettings { Model = "m" }
        };
        var runner = new OpenAiCompatPromptRunner("local", config, new HttpClient(new KeepAliveHandler()));

        PromptResult result = await runner.RunAsync(new PromptInvocation
        {
            ComposedPrompt = "judge this",
            Role = PromptRole.Guardrail,
            WorkingDirectory = "",
            PlanDirectory = "",
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Settings = new PromptRunnerSettings(),
            Timeout = TimeSpan.FromSeconds(60),
            StallBound = TimeSpan.FromSeconds(2),
            StreamLogPath = ""
        }, Ct);

        Assert.Equal(PromptFailureKind.Stalled, result.FailureKind);
        Assert.True(Assert.IsType<StallReport>(result.Stall).NoProgressAtAll);
    }

    // ─── B2 / W4: the retry feedback ────────────────────────────────────────────────────

    [Fact]
    public void AStallInWorktreeMode_DisclosesTheRollback_AndNeverClaimsWorkOnDisk()
    {
        var stall = new StallReport(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15.2), 0, ProgressBeats: 40);

        string feedback = RetryPolicy.ForStalled(TaskFor("05-x"), attempt: 2, stall, fileWritesRolledBack: true);

        Assert.Contains(RetryPolicy.StallHeading, feedback, StringComparison.Ordinal);
        Assert.Contains("## File writes were also rolled back", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("PARTIAL WORK is preserved", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("continue from it", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void AStallWithSalvage_PointsAtTheSalvage()
    {
        var stall = new StallReport(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15.2), 0, ProgressBeats: 40);
        var salvage = new SalvageRef("refs/guardrails/05-x/attempt-1", " a.cs | 3 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForStalled(TaskFor("05-x"), attempt: 2, stall, fileWritesRolledBack: true, salvage);

        Assert.Contains("NOT discarded", feedback, StringComparison.Ordinal);
        Assert.Contains("## Prior attempt work is salvageable", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void AStallInSerialMode_KeepsThePartialWork()
    {
        var stall = new StallReport(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15.2), 0, ProgressBeats: 40);

        string feedback = RetryPolicy.ForStalled(TaskFor("05-x"), attempt: 2, stall);

        Assert.Contains("PARTIAL WORK is preserved", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("rolled back", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void AStallWithNoProgressAtAll_BlamesTheBackend_NotTheApproach()
    {
        var stall = new StallReport(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30.1), 0, ProgressBeats: 0);

        string feedback = RetryPolicy.ForStalled(TaskFor("05-x"), attempt: 2, stall);

        Assert.Contains("runner or its backend", feedback, StringComparison.Ordinal);
        Assert.Contains("Nothing", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("narrow it", feedback, StringComparison.Ordinal);
    }

    // ─── NIT: GR2089 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStallTimeoutUnderGuardrailOverrides_IsAGr2089Warning()
    {
        Assert.Equal("GR2089", DiagnosticCodes.StallTimeoutInGuardrailOverrides);

        string plan = Path.Combine(Path.GetTempPath(), "gr-stall-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            string taskDir = Path.Combine(plan, "tasks", "01-task");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(plan, "guardrails.json"),
                """{ "version": 1, "promptRunners": { "default": "c", "c": { "command": "c", "guardrailOverrides": { "stallTimeoutSeconds": 600 } } } }""");
            File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

            PlanLoadResult load = new PlanLoader().Load(plan);

            Diagnostic warning = Assert.Single(load.Diagnostics, d => d.Code == "GR2089");
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("promptRunners.c.guardrailOverrides.stallTimeoutSeconds does nothing", warning.Message, StringComparison.Ordinal);
            Assert.False(load.HasErrors);
        }
        finally
        {
            try { Directory.Delete(plan, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────────────────

    private static void AdvanceAndPoll(FakeClock clock, StallWatch watch, TimeSpan gap)
    {
        // Poll on the watch's own cadence across the gap, the way the watchdog loop does.
        TimeSpan remaining = gap;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining < watch.PollInterval ? remaining : watch.PollInterval;
            clock.Advance(step);
            remaining -= step;
            watch.Observe();
        }
    }

    private static PromptRunnerConfig Block(PromptRunnerKind kind, string? baseUrl = null) => new()
    {
        Name = "b",
        Command = "b",
        Kind = kind,
        BaseUrl = baseUrl,
        Settings = new PromptRunnerSettings()
    };

    private static TaskNode TaskFor(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = "fixture",
        Action = new ActionDefinition { Path = "action.prompt.md", Kind = ActionKind.Prompt },
        Guardrails = []
    };

    private sealed class FakeClock
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;

        public long Now() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>Answers 200 with an SSE body that only ever sends keep-alive comments.</summary>
    private sealed class KeepAliveHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new KeepAliveStream()) });
    }

    private sealed class KeepAliveStream : Stream
    {
        private static readonly byte[] Comment = Encoding.UTF8.GetBytes(": keep-alive\n\n");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await System.Threading.Tasks.Task.Delay(100, cancellationToken).ConfigureAwait(false);
            int n = Math.Min(buffer.Length, Comment.Length);
            Comment.AsMemory(0, n).CopyTo(buffer);
            return n;
        }

        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
