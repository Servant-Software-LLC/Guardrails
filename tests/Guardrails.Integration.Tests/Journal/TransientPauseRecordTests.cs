using System.Text.Json;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.Journal;

/// <summary>
/// Issue #515 — a class-(b) transient that PAUSED and then RESOLVED left no durable trace anywhere.
///
/// <para><b>The gap.</b> Only the EXHAUSTED path (<c>AttemptJournaler.RateLimitExhausted</c>, when the
/// per-task pause budget runs out) wrote anything to <c>run.json</c>. The pause that CLEARED — the #115
/// happy path, and the entire point of the feature — reached <see cref="IRunObserver.PromptPaused"/> and
/// stopped there. So a task that quietly paused six times and then went green was, in every durable record,
/// byte-identical to one that ran clean, and "did this run hit provider trouble?" became unanswerable the
/// moment the console scrolled away — the difference between "the model is flaky today" and "my plan is
/// wrong".</para>
///
/// <para><b>It also defeats the feature's own justification.</b> #115 pauses WITHOUT consuming retry budget
/// because a provider stall is not the task's fault. That trade is only auditable if the pauses are
/// counted.</para>
///
/// <para><b>The assertions read the RAW JSON, not the typed model</b> (the <c>DeliveryRecordTests</c>
/// precedent): asserting through <see cref="JournalDocument"/> would let a <c>[JsonIgnore]</c> regression
/// pass while the field never reached disk, and disk is the whole point of this issue.</para>
///
/// <para><b>No wall-clock assertion appears anywhere below.</b> The backoff wait is injected and returns
/// immediately; what is asserted is the DECISION <see cref="TransientBackoff"/> made — its first delay is
/// <see cref="TransientBackoff.BaseDelay"/> — never how long anything actually took.</para>
/// </summary>
public sealed class TransientPauseRecordTests
{
    /// <summary>
    /// A runner that reports a rate limit on its first <paramref name="transientRuns"/> invocations and
    /// then succeeds — the shape of a provider stall that clears. It carries a <c>ResetHint</c> so the
    /// machine-readable half of the record has something real to record.
    /// </summary>
    private sealed class StallsThenSucceedsRunner(int transientRuns) : IPromptRunner
    {
        private int _calls;

        public string Name => "claude";

        public int Calls => _calls;

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);

            return Task.FromResult(call <= transientRuns
                ? new PromptResult
                {
                    Completed = true,
                    IsError = true,
                    FailureKind = PromptFailureKind.Transient,
                    ResetHint = "11:20am",
                    Summary = "usage limit reached"
                }
                : new PromptResult
                {
                    Completed = true,
                    IsError = false,
                    ResultText = "done",
                    FailureKind = PromptFailureKind.None,
                    Summary = "claude completed"
                });
        }
    }

    private sealed record RunOutcome(string Root, RunReport Report, string RunJson);

    /// <summary>
    /// Run a one-task prompt plan through the REAL <see cref="TaskExecutor"/> + <see cref="Scheduler"/>,
    /// with the transient backoff's wait INJECTED as a completed task so the pauses are taken without any
    /// real sleep (the codebase's TCS/no-sleep concurrency doctrine). Returns the run report and the raw
    /// bytes of <c>state/run.json</c>.
    /// </summary>
    private static async Task<RunOutcome> RunOneTaskAsync(int transientRuns)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "01-task", "guardrails"));

        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            """
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 60,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "model": "claude-sonnet-5" }
              }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-task");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "a task the provider stalls on", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-check.cmd" : "01-check.sh");
        File.WriteAllText(guardrailPath, win ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        PlanLoadResult load = new PlanLoader().Load(root);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var runner = new StallsThenSucceedsRunner(transientRuns);
        PromptRunnerRegistry registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry,
            // The whole pause schedule, taken instantly. Nothing below reads a clock.
            transientDelay: (_, _) => Task.CompletedTask);
        var scheduler = new Scheduler(load.Plan!, executor, journal, observer: IRunObserver.Null);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        // The fixture is only meaningful if the stall actually happened: transientRuns stalls + 1 success.
        Assert.Equal(transientRuns + 1, runner.Calls);

        return new RunOutcome(root, report, File.ReadAllText(RunJournal.PathFor(root)));
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// THE case this issue was filed from. Two transients, both cleared, the task green — and the durable
    /// record must carry every pause with the four facts the issue names: the reason, the pause ordinal,
    /// the backoff duration, and the parsed reset hint.
    /// </summary>
    [Fact]
    public async Task ATransientThatPausesAndResolves_IsJournaledAtThePause_WithReasonOrdinalWaitAndResetHint()
    {
        RunOutcome run = await RunOneTaskAsync(transientRuns: 2);
        try
        {
            // The run went GREEN — this is the happy path, not the exhaustion path.
            Assert.True(run.Report.AllSucceeded, run.Report.Tasks[0].Summary);

            using JsonDocument doc = JsonDocument.Parse(run.RunJson);
            JsonElement task = doc.RootElement.GetProperty("tasks").GetProperty("01-task");

            Assert.True(
                task.TryGetProperty("transientPauses", out JsonElement pauses),
                "run.json recorded NO transientPauses for a task that paused twice — the #515 defect: the "
                + "pause reached the observer and nothing durable.");

            Assert.Equal(2, pauses.GetArrayLength());

            JsonElement first = pauses[0];
            Assert.Equal(1, first.GetProperty("pause").GetInt32());
            // The paused attempt re-runs under the SAME number — that IS the no-retry-consumed contract.
            Assert.Equal(1, first.GetProperty("attempt").GetInt32());
            Assert.Contains("usage limit reached", first.GetProperty("reason").GetString()!, StringComparison.Ordinal);
            Assert.Equal("11:20am", first.GetProperty("resetHint").GetString());

            // The DECISION the backoff made, not a stopwatch reading: the first delay of the bounded
            // exponential schedule is TransientBackoff.BaseDelay, the second is twice it.
            Assert.Equal(TransientBackoff.BaseDelay.TotalSeconds, first.GetProperty("waitSeconds").GetDouble());
            Assert.Equal(
                TransientBackoff.BaseDelay.TotalSeconds * 2,
                pauses[1].GetProperty("waitSeconds").GetDouble());

            Assert.Equal(2, pauses[1].GetProperty("pause").GetInt32());

            // Present and parseable rather than empty prose — a post-mortem sorts on this.
            Assert.True(first.GetProperty("at").TryGetDateTimeOffset(out _));
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The load-bearing negative, and the one that keeps the record honest: a task that never met a
    /// transient must carry NO <c>transientPauses</c> key at all — absent, never <c>null</c> noise and
    /// never an empty array. Nearly every task in every run takes this path, so a key here would be a new
    /// line in every entry of every journal ever written.
    /// </summary>
    [Fact]
    public async Task ATaskThatNeverPaused_CarriesNoTransientPausesKeyAtAll()
    {
        RunOutcome run = await RunOneTaskAsync(transientRuns: 0);
        try
        {
            Assert.True(run.Report.AllSucceeded, run.Report.Tasks[0].Summary);

            using JsonDocument doc = JsonDocument.Parse(run.RunJson);
            JsonElement task = doc.RootElement.GetProperty("tasks").GetProperty("01-task");

            Assert.False(
                task.TryGetProperty("transientPauses", out _),
                "a task that never paused grew a transientPauses key — absent, never null noise.");
        }
        finally
        {
            Cleanup(run.Root);
        }
    }

    /// <summary>
    /// The record survives the round-trip that makes it durable, in BOTH directions — a reader coming back
    /// to an old journal (<c>guardrails status</c>, the log-site export, a post-mortem tool) deserializes
    /// through the typed model, so a write-only field would still be half a feature.
    /// </summary>
    [Fact]
    public void TheRecordRoundTrips_ThroughTheJournalSerializer()
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-05T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["01-task"] = new TaskJournalEntry
                {
                    Status = Core.Journal.TaskStatus.Succeeded,
                    TransientPauses =
                    [
                        new TransientPauseRecord
                        {
                            Pause = 1,
                            Attempt = 3,
                            At = DateTimeOffset.Parse("2026-09-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                            Reason = "overloaded (resets 11:20am)",
                            WaitSeconds = 4,
                            ResetHint = "11:20am"
                        }
                    ]
                }
            }
        };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);
        Assert.Contains("\"transientPauses\"", json, StringComparison.Ordinal);

        JournalDocument back = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        TransientPauseRecord pause = Assert.Single(back.Tasks["01-task"].TransientPauses!);

        Assert.Equal(1, pause.Pause);
        Assert.Equal(3, pause.Attempt);
        Assert.Equal(4, pause.WaitSeconds);
        Assert.Equal("11:20am", pause.ResetHint);
    }

    /// <summary>
    /// A provider that named no reset time must leave the field ABSENT rather than empty: its presence is
    /// the signal that a hint was actually parsed, and an always-written key destroys that signal exactly
    /// as <c>provenance.requestedModel</c>'s doc comment argues one hop over.
    /// </summary>
    [Fact]
    public void APauseWithNoResetHint_OmitsTheKey()
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-05T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["01-task"] = new TaskJournalEntry
                {
                    Status = Core.Journal.TaskStatus.Succeeded,
                    TransientPauses =
                    [
                        new TransientPauseRecord
                        {
                            Pause = 1,
                            Attempt = 1,
                            At = DateTimeOffset.UnixEpoch,
                            Reason = "HTTP 529 overloaded",
                            WaitSeconds = 2
                        }
                    ]
                }
            }
        };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);

        Assert.Contains("\"transientPauses\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("resetHint", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run summary must SAY a green run paused. A green run that waited out three rate limits is a
    /// materially different result from one that sailed through, and before #515 the end-of-run verdict —
    /// the thing an operator actually reads — was silent about it.
    /// </summary>
    [Fact]
    public void TheRunSummary_NamesTheProviderPauses_OnAnOtherwiseGreenRun()
    {
        IReadOnlyList<TaskResult> tasks =
        [
            new TaskResult
            {
                TaskId = "01-task", Outcome = TaskOutcome.Succeeded, Summary = "ok",
                ResolvedTransient = new ResolvedTransient { Pauses = 3, Waited = TimeSpan.FromSeconds(14) }
            },
            new TaskResult { TaskId = "02-task", Outcome = TaskOutcome.Succeeded, Summary = "ok" }
        ];

        string line = Assert.IsType<string>(RunCommand.TransientPauseLine(tasks));

        Assert.Contains("3", line, StringComparison.Ordinal);
        Assert.Contains("1 task(s)", line, StringComparison.Ordinal);
        Assert.Contains("14s", line, StringComparison.Ordinal);
        Assert.Contains("transientPauses", line, StringComparison.Ordinal);
    }

    /// <summary>The silent majority: no pause, no line. A permanently-present advisory teaches nothing.</summary>
    [Fact]
    public void TheRunSummary_SaysNothingWhenNothingPaused()
    {
        IReadOnlyList<TaskResult> tasks =
            [new TaskResult { TaskId = "01-task", Outcome = TaskOutcome.Succeeded, Summary = "ok" }];

        Assert.Null(RunCommand.TransientPauseLine(tasks));
    }

    /// <summary>
    /// <c>guardrails status</c> reads ONLY the journal, so it is the surface that proves the record is
    /// durable rather than merely in-memory: it answers "did this run hit provider trouble?" days later,
    /// which is the question the issue is about.
    /// </summary>
    [Fact]
    public void Status_ReportsThePausesPerTask_AndNothingWhenNoneAreRecorded()
    {
        var withPauses = new JournalDocument
        {
            RunId = "r", PlanHash = "sha256:abc",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["02-second"] = new TaskJournalEntry { Status = Core.Journal.TaskStatus.Succeeded },
                ["01-first"] = new TaskJournalEntry
                {
                    Status = Core.Journal.TaskStatus.Succeeded,
                    TransientPauses =
                    [
                        new TransientPauseRecord
                        {
                            Pause = 1, Attempt = 1, At = DateTimeOffset.UnixEpoch,
                            Reason = "usage limit reached (resets 11:20am)", WaitSeconds = 2
                        },
                        new TransientPauseRecord
                        {
                            Pause = 2, Attempt = 1, At = DateTimeOffset.UnixEpoch,
                            Reason = "usage limit reached (resets 11:20am)", WaitSeconds = 4
                        }
                    ]
                }
            }
        };

        string line = Assert.Single(StatusCommand.TransientPauseLines(withPauses));
        Assert.Contains("01-first", line, StringComparison.Ordinal);
        Assert.Contains("2 pause(s)", line, StringComparison.Ordinal);
        Assert.Contains("6s waited", line, StringComparison.Ordinal);
        Assert.Contains("usage limit reached", line, StringComparison.Ordinal);

        var clean = new JournalDocument
        {
            RunId = "r", PlanHash = "sha256:abc",
            Tasks = new Dictionary<string, TaskJournalEntry>
            {
                ["01-first"] = new TaskJournalEntry { Status = Core.Journal.TaskStatus.Succeeded }
            }
        };
        Assert.Empty(StatusCommand.TransientPauseLines(clean));
    }
}
