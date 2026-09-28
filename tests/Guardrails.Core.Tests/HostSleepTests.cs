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
/// #810: host sleep is detected (wall clock against an awake clock), recorded on the in-flight attempts, reported, and
/// stated in both numbers; it never counts against a timeout. Every clock here is injected and moved by hand: nothing
/// sleeps and nothing asserts elapsed time.
/// </summary>
public sealed class HostSleepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ─── detection ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AwakeTimeIsNotSleep_AndAGapOfAMinuteOrMoreIs()
    {
        var clocks = new Clocks();
        var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);

        clocks.Run(TimeSpan.FromMinutes(10));
        Assert.Null(monitor.Check());

        clocks.Run(TimeSpan.FromSeconds(15));
        clocks.Sleep(TimeSpan.FromSeconds(59));
        Assert.Null(monitor.Check());                              // counted, but not reported

        DateTimeOffset before = clocks.Now;
        clocks.Run(TimeSpan.FromSeconds(15));
        clocks.Sleep(TimeSpan.FromHours(2));
        HostSleepEvent sleep = Assert.IsType<HostSleepEvent>(monitor.Check());

        Assert.Equal(TimeSpan.FromHours(2), sleep.SleptFor);
        Assert.True(sleep.Reported);
        Assert.Equal(before, sleep.From);
        Assert.Equal(clocks.Now, sleep.To);
        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(59), monitor.TotalSlept);
    }

    /// <summary>
    /// #810 review W3: several sub-minute sleeps (a Mac cycling through short DarkWakes) are each below the reporting
    /// threshold, yet together they are time the attempt's timeout did not count, so they are all in the totals and the
    /// awake figure matches what the timeout measured. None is reported as an event.
    /// </summary>
    [Fact]
    public void SeveralFiftyNineSecondSleeps_AddUp_WithoutBeingReported()
    {
        var clocks = new Clocks();
        var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
        var raised = new List<HostSleepEvent>();
        monitor.Slept += raised.Add;
        HostSleepMark mark = monitor.Mark();

        for (int i = 0; i < 3; i++)
        {
            clocks.Run(TimeSpan.FromSeconds(15));
            clocks.Sleep(TimeSpan.FromSeconds(59));
            Assert.Null(monitor.Check());
        }

        clocks.Run(TimeSpan.FromSeconds(0.5));
        clocks.Sleep(TimeSpan.FromSeconds(0.5));                    // jitter: not sleep at all

        (TimeSpan wall, TimeSpan slept, TimeSpan awake) = monitor.Since(mark);
        Assert.Equal(TimeSpan.FromSeconds(177), slept);
        Assert.Equal(TimeSpan.FromSeconds(46), awake);                // the 0.5 s of jitter is awake time
        Assert.Equal(TimeSpan.FromSeconds(223), wall);
        Assert.Equal(3, raised.Count);
        Assert.All(raised, e => Assert.False(e.Reported));
    }

    [Fact]
    public void ABackwardWallStepIsNotASleep()
    {
        var clocks = new Clocks();
        var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);

        clocks.Run(TimeSpan.FromMinutes(1));
        clocks.StepWall(TimeSpan.FromHours(-1));

        Assert.Null(monitor.Check());
        Assert.Equal(TimeSpan.Zero, monitor.TotalSlept);
    }

    [Fact]
    public void SinceAMark_GivesWallSleptAndAwake_CountingASleepThatJustEnded()
    {
        var clocks = new Clocks();
        var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
        HostSleepMark mark = monitor.Mark();

        clocks.Run(TimeSpan.FromMinutes(20));
        clocks.Sleep(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(48));
        clocks.Run(TimeSpan.FromMinutes(40));

        (TimeSpan wall, TimeSpan slept, TimeSpan awake) = monitor.Since(mark);

        Assert.Equal(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(48), wall);
        Assert.Equal(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(48), slept);
        Assert.Equal(TimeSpan.FromHours(1), awake);
    }

    [Fact]
    public async Task TheHeartbeatChecksOnEachInterval_AndAHandlerFaultDoesNotStopIt()
    {
        var clocks = new Clocks();
        var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
        var seen = new List<TimeSpan>();
        int faults = 0;
        monitor.Slept += e =>
        {
            seen.Add(e.SleptFor);
            if (faults++ == 0)
            {
                throw new IOException("run.json is locked");
            }
        };

        using var cts = new CancellationTokenSource();
        int polls = 0;
        await monitor.WatchAsync(
            HostSleepMonitor.DefaultInterval,
            (interval, _) =>
            {
                polls++;
                clocks.Run(interval);
                if (polls is 2 or 4)
                {
                    clocks.Sleep(TimeSpan.FromMinutes(polls * 10));
                }

                if (polls == 6)
                {
                    cts.Cancel();
                    throw new OperationCanceledException();
                }

                return Task.CompletedTask;
            },
            cts.Token);

        Assert.Equal([TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40)], seen);
    }

    [Fact]
    public void TheSharedLineNamesTheSleepTheWindowAndTheAttempts()
    {
        string line = HostSleepText.Line(
            new DateTimeOffset(2026, 9, 26, 15, 3, 52, TimeSpan.Zero), new DateTimeOffset(2026, 9, 26, 21, 4, 10, TimeSpan.Zero),
            TimeSpan.FromHours(6), ["02-x/attempt-1"]);

        Assert.Equal(
            "the host was asleep for 6h00m (between 15:03:52 and 21:04:10 UTC); in flight: 02-x/attempt-1 — sleep does not " +
            "count against an attempt's timeout",
            line);
    }

    [Fact]
    public void TheAwakeClockDoesNotGoBackwards()
    {
        TimeSpan first = AwakeClock.Now();
        TimeSpan second = AwakeClock.Now();
        Assert.True(second >= first);
    }

    // ─── the journal ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SleepAccumulatesOnTheInFlightMarker_SurvivesAPhaseChange_AndMovesOntoTheRecord()
    {
        string root = NewRoot();
        try
        {
            PlanDefinition plan = WritePlan(root, retries: 0);
            RunJournal journal = RunJournal.LoadOrCreate(plan);

            Assert.Empty(journal.AddSleepToInFlightAttempts(TimeSpan.FromMinutes(5)));

            journal.MarkAttemptInFlight("01-task", 1, InFlightPhase.Action);
            Assert.Equal([("01-task", 1)], journal.AddSleepToInFlightAttempts(TimeSpan.FromMinutes(5)));
            journal.MarkAttemptInFlight("01-task", 1, InFlightPhase.Guardrails);
            journal.AddSleepToInFlightAttempts(TimeSpan.FromSeconds(90));

            Assert.Equal(TimeSpan.FromSeconds(390), journal.InFlightSleep("01-task"));

            journal.RecordAttempt("01-task", new AttemptRecord
            {
                Attempt = 1, StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch,
                Outcome = AttemptOutcome.ActionFailed, LogDir = "logs/x"
            }, JournalTaskStatus.Pending);

            AttemptRecord settled = Assert.Single(journal.Document.Tasks["01-task"].Attempts);
            Assert.Equal(390, settled.SleptSeconds);

            journal.MarkAttemptInFlight("01-task", 2, InFlightPhase.Action);
            Assert.Equal(TimeSpan.Zero, journal.InFlightSleep("01-task"));
        }
        finally { DeleteBestEffort(root); }
    }

    // ─── the executor: reported, recorded, and both numbers in the summary ──────────────

    [Fact]
    public async Task ATimeoutTheHostSleptThrough_StatesAwakeAndWallTime_AndRecordsTheSleep()
    {
        string root = NewRoot();
        try
        {
            PlanDefinition plan = WritePlan(root, retries: 0);
            var clocks = new Clocks();
            var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
            var runner = new ScriptedRunner(() =>
            {
                // 1h awake against a 1h timeout, around 7h48m of sleep: 8h48m of wall time (the #810 evidence).
                clocks.Run(TimeSpan.FromMinutes(20));
                clocks.Sleep(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(48));
                clocks.Run(TimeSpan.FromMinutes(40));
                return new PromptResult { Completed = false, IsError = true, FailureKind = PromptFailureKind.Timeout, Summary = "claude timed out" };
            });
            var observer = new Recorder();

            RunReport report = await RunSerialAsync(plan, runner, observer, monitor);

            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Contains("timed out after 1h00m awake (8h48m wall; host slept 7h48m): claude timed out", settled.Summary, StringComparison.Ordinal);

            (DateTimeOffset _, DateTimeOffset _, TimeSpan sleptFor, IReadOnlyList<string> inFlight) = Assert.Single(observer.Sleeps);
            Assert.Equal(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(48), sleptFor);
            Assert.Equal(["01-task/attempt-1"], inFlight);

            AttemptRecord record = Assert.Single(RunJournal.LoadOrCreate(plan).Document.Tasks["01-task"].Attempts);
            Assert.Equal((long)TimeSpan.FromMinutes(468).TotalSeconds, record.SleptSeconds);
        }
        finally { DeleteBestEffort(root); }
    }

    /// <summary>
    /// #810 review B1/B2, the first reproduction: attempt 1 sleeps 2h and its action succeeds, its guardrail fails, and
    /// the retry times out. The sleep belongs to attempt 1 (detected when it settles, not charged to attempt 2), and the
    /// event names attempt-1.
    /// </summary>
    [Fact]
    public async Task ASleepDuringAnAttemptThatSettlesOnAGuardrail_IsChargedToThatAttempt_NotTheRetry()
    {
        string root = NewRoot();
        try
        {
            PlanDefinition plan = WritePlan(root, retries: 1, guardrailFailsOnFirstAttempt: true);
            var clocks = new Clocks();
            var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
            int calls = 0;
            var runner = new ScriptedRunner(() =>
            {
                if (++calls == 1)
                {
                    clocks.Run(TimeSpan.FromMinutes(5));
                    clocks.Sleep(TimeSpan.FromHours(2));
                    return new PromptResult { Completed = true, IsError = false, Summary = "done" };
                }

                clocks.Run(TimeSpan.FromHours(1));
                return new PromptResult { Completed = false, IsError = true, FailureKind = PromptFailureKind.Timeout, Summary = "claude timed out" };
            });
            var observer = new Recorder();

            await RunSerialAsync(plan, runner, observer, monitor);

            IReadOnlyList<AttemptRecord> attempts = RunJournal.LoadOrCreate(plan).Document.Tasks["01-task"].Attempts;
            Assert.Equal(2, attempts.Count);
            Assert.Equal(7200, attempts[0].SleptSeconds);
            Assert.Null(attempts[1].SleptSeconds);
            Assert.Equal(["01-task/attempt-1"], Assert.Single(observer.Sleeps).InFlight);
        }
        finally { DeleteBestEffort(root); }
    }

    /// <summary>
    /// #810 review B1/B2, the second reproduction: the host slept an hour, woke, and an attempt started three seconds
    /// later and ran thirty. None of that sleep is the attempt's.
    /// </summary>
    [Fact]
    public async Task ASleepThatEndedBeforeTheAttemptStarted_IsNotChargedToIt()
    {
        string root = NewRoot();
        try
        {
            PlanDefinition plan = WritePlan(root, retries: 0);
            var clocks = new Clocks();
            var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
            clocks.Sleep(TimeSpan.FromHours(1));
            clocks.Run(TimeSpan.FromSeconds(3));
            var runner = new ScriptedRunner(() =>
            {
                clocks.Run(TimeSpan.FromSeconds(30));
                return new PromptResult { Completed = false, IsError = true, FailureKind = PromptFailureKind.Timeout, Summary = "claude timed out" };
            });
            var observer = new Recorder();

            RunReport report = await RunSerialAsync(plan, runner, observer, monitor);

            Assert.DoesNotContain("slept", Assert.Single(report.Tasks).Summary, StringComparison.Ordinal);
            Assert.Null(Assert.Single(RunJournal.LoadOrCreate(plan).Document.Tasks["01-task"].Attempts).SleptSeconds);
            Assert.Empty(Assert.Single(observer.Sleeps).InFlight);    // reported, with no attempt in flight
        }
        finally { DeleteBestEffort(root); }
    }

    /// <summary>
    /// #810 review W4: on Windows the awake clock must be the timer queue's own clock, or the "awake" figure would not be
    /// what the attempt timeout measured. Read through reflection, so a runtime that renames it skips with the reason.
    /// </summary>
    [Fact]
    public void OnWindows_TheAwakeClockIsTheTimerQueueClock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the timer-queue clock comparison applies to Windows only");

        System.Reflection.MethodInfo? tick = typeof(System.Threading.Timer).Assembly
            .GetType("System.Threading.TimerQueue")
            ?.GetMethod("get_TickCount64", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.SkipWhen(tick is null, "this runtime has no System.Threading.TimerQueue.TickCount64 to compare against");

        long timerQueueMs = (long)tick!.Invoke(null, null)!;
        double awakeMs = AwakeClock.Now().TotalMilliseconds;

        Assert.True(Math.Abs(awakeMs - timerQueueMs) < 1000,
            $"AwakeClock {awakeMs:F0} ms differs from the timer queue's {timerQueueMs} ms by more than 1 s");
    }

    [Fact]
    public async Task AnAttemptTheHostDidNotSleepThrough_SaysNothingAboutSleep()
    {
        string root = NewRoot();
        try
        {
            PlanDefinition plan = WritePlan(root, retries: 0);
            var clocks = new Clocks();
            var monitor = new HostSleepMonitor(clocks.Wall, clocks.Awake);
            var runner = new ScriptedRunner(() =>
            {
                clocks.Run(TimeSpan.FromHours(1));
                return new PromptResult { Completed = false, IsError = true, FailureKind = PromptFailureKind.Timeout, Summary = "claude timed out" };
            });
            var observer = new Recorder();

            RunReport report = await RunSerialAsync(plan, runner, observer, monitor);

            Assert.DoesNotContain("slept", Assert.Single(report.Tasks).Summary, StringComparison.Ordinal);
            Assert.Empty(observer.Sleeps);
            Assert.Null(Assert.Single(RunJournal.LoadOrCreate(plan).Document.Tasks["01-task"].Attempts).SleptSeconds);
        }
        finally { DeleteBestEffort(root); }
    }

    [Theory]
    [InlineData(true, "timed out after 1h00m awake (8h48m wall; host slept 7h48m): timed out after 60.0m waiting on http://x/v1. Raise timeoutSeconds.")]
    [InlineData(false, "timed out after 60.0m waiting on http://x/v1. Raise timeoutSeconds; host slept 7h48m during the attempt (1h00m awake, 8h48m wall)")]
    public void ATimeoutLeadsWithItsAwakeTime_AnythingElseTrailsIt(bool timedOut, string expected) =>
        Assert.Equal(expected, TaskExecutor.WithSleep(
            "timed out after 60.0m waiting on http://x/v1. Raise timeoutSeconds.",
            (TimeSpan.FromMinutes(528), TimeSpan.FromMinutes(468), TimeSpan.FromMinutes(60)), timedOut));

    [Fact]
    public void WithoutSleepTheCauseIsUnchanged() =>
        Assert.Equal("claude exited 1", TaskExecutor.WithSleep("claude exited 1", (TimeSpan.FromMinutes(5), TimeSpan.Zero, TimeSpan.FromMinutes(5)), timedOut: false));

    // ─── the event row ──────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheHostSleptRowIsRunScoped_AndNamesTheAttemptsInFlight()
    {
        string dir = NewRoot();
        Directory.CreateDirectory(dir);
        try
        {
            var stream = new RunEventStream(IRunObserver.Null, dir, "run-810");
            var from = new DateTimeOffset(2026, 9, 26, 15, 3, 52, TimeSpan.Zero);

            stream.HostSlept(from, from.AddMinutes(9), TimeSpan.FromSeconds(514.4), ["02-x/attempt-1"]);
            stream.HostSlept(from, from.AddMinutes(3), TimeSpan.FromSeconds(120), []);

            string[] lines = [.. File.ReadAllLines(Path.Combine(dir, "events.jsonl")).Where(l => l.Length > 0)];
            using (JsonDocument first = JsonDocument.Parse(lines[0]))
            {
                JsonElement row = first.RootElement;
                Assert.Equal("host-slept", row.GetProperty("kind").GetString());
                Assert.Equal(RunEventStream.HostSleptKind, row.GetProperty("kind").GetString());
                Assert.False(row.TryGetProperty("taskId", out _));
                Assert.Equal(514, row.GetProperty("sleptForSeconds").GetInt64());
                Assert.Equal(from, row.GetProperty("from").GetDateTimeOffset());
                Assert.Equal("02-x/attempt-1", Assert.Single(row.GetProperty("inFlight").EnumerateArray()).GetString());
            }

            using JsonDocument second = JsonDocument.Parse(lines[1]);
            Assert.False(second.RootElement.TryGetProperty("inFlight", out _));
        }
        finally { DeleteBestEffort(dir); }
    }

    // ─── the bundle ─────────────────────────────────────────────────────────────────────

    private const string PmsetLog = """
        2026-09-26 10:00:00 -0400 Wake                	Wake from Normal Sleep [CDNVA] : due to UserActivity
        2026-09-26 11:03:52 -0400 Sleep               	Entering Sleep state due to 'Maintenance Sleep':TCPKeepAlive=active Using Batt (Charge:86%) 514 secs
        2026-09-26 11:05:00 -0400 Assertions          	PID 123(caffeinate) Created PreventUserIdleSystemSleep
        2026-09-26 11:06:00 -0400 Wake Requests       	[process=mDNSResponder request=Maintenance deltaSecs=7200]
        2026-09-26 11:12:26 -0400 DarkWake            	DarkWake from Deep Idle [CDNP] : due to SMC.OutboxNotEmpty/Maintenance Using Batt (Charge:86%) 45 secs
        2026-09-26 11:21:26 -0400 Sleep               	Entering Sleep state due to 'Maintenance Sleep' Using Batt (Charge:85%) 762 secs
        2026-09-26 23:00:00 -0400 Wake                	Wake from Normal Sleep [CDNVA] : due to UserActivity
        not a pmset line at all
        """;

    [Fact]
    public void ThePmsetFilterKeepsSleepWakeAndDarkWakeInsideTheWindow()
    {
        var from = new DateTimeOffset(2026, 9, 26, 11, 0, 0, TimeSpan.FromHours(-4));
        var to = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(-4));

        IReadOnlyList<string> lines = BundleSleepWake.Filter(PmsetLog, from, to);

        Assert.Equal(3, lines.Count);
        Assert.Contains("11:03:52 -0400 Sleep", lines[0], StringComparison.Ordinal);
        Assert.Contains("DarkWake", lines[1], StringComparison.Ordinal);
        Assert.Contains("11:21:26 -0400 Sleep", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundleCarriesTheSleepWakeHistory_AndTheSummaryNamesTheSleep()
    {
        using var fixture = new BundlePlanFixture();
        fixture.WriteJournal(BundlePlanFixture.Journal(
            secondStatus: JournalTaskStatus.Running,
            secondAttempts: [BundlePlanFixture.Attempt(1, AttemptOutcome.Timeout) with { SleptSeconds = 1800 }],
            inFlight: new InFlightAttemptRecord
            {
                Attempt = 2, StartedAt = BundlePlanFixture.Clock.AddHours(1), Phase = "action", SleptSeconds = 21600
            }));
        fixture.PromptAttempt("02-second", 1, "first");
        fixture.PromptAttempt("02-second", 2, "second");
        fixture.Log("events.jsonl",
            """{"kind":"host-slept","seq":1,"runId":"r","sleptForSeconds":1800}""" + "\n" +
            """{"kind":"host-slept","seq":2,"runId":"r","sleptForSeconds":21600}""" + "\n");
        DateTimeOffset asked = default;
        BundleProbes probes = fixture.Probes() with
        {
            SleepWake = (from, to) =>
            {
                asked = from;
                return new BundleSleepWakeLog(["2026-09-27 08:03:52 -0400 Sleep   Entering Sleep state"], null);
            }
        };

        BundleOutcome outcome = fixture.Build(probes: probes);

        string history = outcome.Text("host/sleep-wake.log")!;
        Assert.Contains("Entering Sleep state", history, StringComparison.Ordinal);
        Assert.Equal(BundlePlanFixture.Clock.AddMinutes(-5), asked);   // five minutes before the owner process started

        string summary = outcome.Text("SUMMARY.md")!;
        // The same duration format the live line uses.
        Assert.Contains("- Host sleep: 2 sleep(s), 6h30m in all", summary, StringComparison.Ordinal);
        Assert.Contains("- Host slept 30m00s during attempt 1", summary, StringComparison.Ordinal);
        Assert.Contains("- Host slept 6h00m during in-flight attempt 2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAProbeTheBundleHasNoSleepWakeFile()
    {
        using var fixture = new BundlePlanFixture();
        fixture.WriteJournal(BundlePlanFixture.Journal());

        Assert.False(fixture.Build().Has("host/sleep-wake.log"));
    }

    // ─── fixtures ───────────────────────────────────────────────────────────────────────

    /// <summary>A wall clock and an awake clock: running moves both, sleeping moves only the wall clock.</summary>
    private sealed class Clocks
    {
        private DateTimeOffset _wall = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);
        private TimeSpan _awake = TimeSpan.FromHours(100);

        public DateTimeOffset Now => _wall;

        public DateTimeOffset Wall() => _wall;

        public TimeSpan Awake() => _awake;

        public void Run(TimeSpan by)
        {
            _wall += by;
            _awake += by;
        }

        public void Sleep(TimeSpan by) => _wall += by;

        public void StepWall(TimeSpan by) => _wall += by;
    }

    private sealed class ScriptedRunner(Func<PromptResult> run) : IPromptRunner
    {
        public string Name => "stub";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(run());
    }

    private sealed class Recorder : IRunObserver
    {
        public List<(DateTimeOffset From, DateTimeOffset To, TimeSpan SleptFor, IReadOnlyList<string> InFlight)> Sleeps { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void HostSlept(DateTimeOffset from, DateTimeOffset to, TimeSpan sleptFor, IReadOnlyList<string> inFlight) =>
            Sleeps.Add((from, to, sleptFor, inFlight));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "gr-host-sleep-" + Guid.NewGuid().ToString("N"));

    private static PlanDefinition WritePlan(string root, int retries, bool guardrailFailsOnFirstAttempt = false)
    {
        string planDir = Path.Combine(root, "plan");
        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            { "version": 1, "workspace": ".", "maxParallelism": 1, "defaultTimeoutSeconds": 3600, "defaultRetries": {{retries}},
              "promptRunners": { "default": "stub", "stub": { "command": "stub" } } }
            """);
        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Write(Path.Combine(taskDir, "task.json"),
            """{ "description": "sleep fixture", "dependsOn": [], "writeScope": [], "action": { "path": "action.prompt.md" } }""");
        Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        string check = Path.Combine(taskDir, "guardrails", OperatingSystem.IsWindows() ? "01-check.ps1" : "01-check.sh");
        string body = !guardrailFailsOnFirstAttempt ? "exit 0"
            : OperatingSystem.IsWindows()
                ? "if ($env:GUARDRAILS_ATTEMPT -eq '1') { exit 1 } else { exit 0 }"
                : "if [ \"$GUARDRAILS_ATTEMPT\" = \"1\" ]; then exit 1; else exit 0; fi";
        Write(check, OperatingSystem.IsWindows() ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n");
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

    private static async Task<RunReport> RunSerialAsync(
        PlanDefinition plan, IPromptRunner runner, IRunObserver observer, HostSleepMonitor monitor)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry, hostSleep: monitor);
        return await new Scheduler(plan, executor, journal, maxParallelism: 1).RunAsync(plan, Ct);
    }

    private static void DeleteBestEffort(string root)
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
