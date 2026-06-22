using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Behavioural tests for the three prompt-runner reliability fixes (issues #115/#114/#119), driven
/// through the real <see cref="TaskExecutor"/> + <see cref="Scheduler"/> with a SEQUENCING fake
/// <see cref="IPromptRunner"/> that emits canned <see cref="PromptResult"/>s (transient / over-cap /
/// timeout) per call, and an INJECTED instant transient delay (no real sleeps — the backoff is gated
/// deterministically). The load-bearing assertions are the new classification AND, for transients,
/// that the RETRY BUDGET IS PRESERVED.
/// </summary>
public sealed class PromptRunnerReliabilityTests
{
    /// <summary>
    /// An <see cref="IPromptRunner"/> that returns a scripted sequence of results (one per call) and
    /// records its invocations. The LAST scripted result repeats once the sequence is exhausted, so a
    /// "transient×N then success" sequence resolves; an "always over-cap" sequence keeps failing.
    /// </summary>
    private sealed class SequencingRunner : IPromptRunner
    {
        private readonly IReadOnlyList<PromptResult> _sequence;
        private int _index = -1;
        public int Calls { get; private set; }
        public List<TimeSpan> Timeouts { get; } = new();

        public SequencingRunner(params PromptResult[] sequence)
        {
            _sequence = sequence;
            Name = "claude";
        }

        public string Name { get; }

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            Timeouts.Add(invocation.Timeout);
            _index = Math.Min(_index + 1, _sequence.Count - 1);
            return Task.FromResult(_sequence[_index]);
        }
    }

    /// <summary>Observer that records every <see cref="IRunObserver.PromptPaused"/> event.</summary>
    private sealed class PauseRecordingObserver : IRunObserver
    {
        public ConcurrentBag<(string Reason, TimeSpan Backoff, int PauseCount)> Pauses { get; } = new();
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
            Pauses.Add((reason, backoff, pauseCount));
    }

    private static PromptResult Transient(string reason, string? resetHint = null) => new()
    {
        Completed = false,
        IsError = true,
        ResultText = reason,
        FailureKind = PromptFailureKind.Transient,
        ResetHint = resetHint,
        Summary = $"claude reported is_error ({reason})"
    };

    private static PromptResult OverCap() => new()
    {
        Completed = false,
        IsError = true,
        ResultText = "API Error: Claude's response exceeded the 32000 output token maximum",
        FailureKind = PromptFailureKind.OutputCap,
        Summary = "claude reported is_error (output cap)"
    };

    private static PromptResult Timeout() => new()
    {
        Completed = false,
        IsError = false,
        FailureKind = PromptFailureKind.Timeout,
        Summary = "claude timed out"
    };

    private static PromptResult Success() => new()
    {
        // A clean success that also writes a fragment under the task id (the fake plan's action does
        // not run claude — the runner is the fake — so the fragment is supplied here is irrelevant;
        // the action writes its own fragment via the prompt's action body below). Mark None.
        Completed = true,
        IsError = false,
        CostUsd = 0.01m,
        FailureKind = PromptFailureKind.None,
        Summary = "claude completed"
    };

    /// <summary>
    /// Build a one-task prompt-action plan (a trivial always-pass deterministic guardrail) and run it
    /// through the real Scheduler with the given fake runner, observer, retry budget and instant
    /// transient delay. Returns the run report + the journal entry for the single task.
    /// </summary>
    private static async Task<(RunReport Report, TaskJournalEntry Entry, SequencingRunner Runner)> RunOneTaskAsync(
        SequencingRunner runner,
        IRunObserver observer,
        int defaultRetries,
        int transientPauseBudgetSeconds = 1800)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tasks", "01-task", "guardrails"));

        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultRetries": {{defaultRetries}},
              "defaultTimeoutSeconds": 60,
              "transientPauseBudgetSeconds": {{transientPauseBudgetSeconds}},
              "promptRunners": { "default": "claude", "claude": { "command": "claude" } }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-task");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "reliability task", "dependsOn": [], "action": { "path": "action.prompt.md" } }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");

        // Trivial always-pass deterministic guardrail (OS-appropriate, directly spawnable).
        bool win = OperatingSystem.IsWindows();
        string guardrailPath = Path.Combine(taskDir, "guardrails", win ? "01-ok.cmd" : "01-ok.sh");
        File.WriteAllText(guardrailPath, win ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        if (!win)
        {
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            PlanLoadResult load = new PlanLoader().Load(root);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

            var stateManager = new StateManager(load.Plan!.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

            var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
            var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

            var executor = new TaskExecutor(
                load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry,
                triage: null,
                // Instant delay — the backoff is exercised but never actually sleeps (TCS-style gate).
                transientDelay: (_, _) => Task.CompletedTask);

            var scheduler = new Scheduler(load.Plan!, executor, journal, observer: observer);
            RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

            JournalDocument doc = JournalReader.Read(RunJournal.PathFor(root));
            return (report, doc.Tasks["01-task"], runner);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #115 — transient rate/overload: pause, DON'T burn the budget, never needs-human
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transient_PausesAndResumes_WithoutConsumingRetryBudget()
    {
        // Budget = 1 (defaultRetries 0): a SINGLE retry slot. Two transient pauses then success must
        // still SUCCEED on the one budgeted attempt — proving the transient pauses did NOT consume it.
        var observer = new PauseRecordingObserver();
        var runner = new SequencingRunner(
            Transient("usage limit reached", resetHint: "11:20am"),
            Transient("overloaded"),
            Success());

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, observer, defaultRetries: 0);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(3, used.Calls);                       // 2 transient + 1 success
        Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);

        // The retry budget was preserved: only ONE attempt was journaled (the paused retries are
        // observe-only — never journaled, never counted), and it is the succeeded one.
        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);

        // Two distinct pause signals were surfaced to the observer; the first carries the reset hint.
        Assert.Equal(2, observer.Pauses.Count);
        Assert.Contains(observer.Pauses, p => p.Reason.Contains("11:20am"));
    }

    [Fact]
    public async Task Transient_NeverMarksNeedsHuman_WhenItEventuallyClears()
    {
        // A long transient streak that still clears: budget 0 (one attempt). Even five pauses do not
        // exhaust the (default 30-min) pause budget with the instant delay, so it succeeds, never
        // needs-human — the core "a rate limit is never needs-human" guarantee.
        var runner = new SequencingRunner(
            Transient("429"), Transient("503"), Transient("529"), Transient("overloaded"), Transient("session limit"),
            Success());

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 0);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);
        Assert.DoesNotContain(entry.Attempts, a => a.Outcome == AttemptOutcome.RateLimited);
    }

    [Fact]
    public async Task Transient_BudgetExhausted_SettlesNeedsHuman_WithDistinctRateLimitReason()
    {
        // A pause budget so tiny it is spent before the (never-clearing) limit resolves: the task must
        // settle needs-human with the DISTINCT rate-limited outcome and a "re-run later" reason — NOT
        // a generic action failure, and NOT a burned retry budget.
        var runner = new SequencingRunner(Transient("session limit · resets 11:20am"));   // never clears

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2,
                transientPauseBudgetSeconds: 1);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains("rate-limited", task.Summary);
        Assert.Contains("re-run later", task.Summary);

        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Contains(entry.Attempts, a => a.Outcome == AttemptOutcome.RateLimited);
        // The action retry budget (3 attempts) was NOT burned re-failing the rate limit.
        Assert.DoesNotContain(entry.Attempts, a => a.Outcome == AttemptOutcome.ActionFailed);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #114 — output-token cap: distinct outcome + actionable feedback, retry NOT a blind re-hit
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutputCap_RecordsDistinctOutcome_AndActionableFeedback_ThenNeedsHuman()
    {
        // Always over-cap, budget 1 (one retry): two attempts, both output-cap, then needs-human. The
        // journal must show the DISTINCT output-cap outcome (not generic action-failed), and the retry
        // feedback must be actionable ("write incrementally / split"), not a blind re-hit.
        var runner = new SequencingRunner(OverCap());

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 1);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.ActionFailed, task.Outcome);
        Assert.Contains("output-token cap", task.Summary);

        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.All(entry.Attempts, a => Assert.Equal(AttemptOutcome.OutputCap, a.Outcome));
        Assert.Equal(2, used.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #119 — timeout: distinct outcome, timeout-specific feedback (continue from partial work)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_RecordsTimeoutOutcome_NotGenericActionFailure()
    {
        var runner = new SequencingRunner(Timeout());

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 1);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.ActionFailed, task.Outcome);
        Assert.Contains("under-sized/under-budgeted", task.Summary);

        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.All(entry.Attempts, a => Assert.Equal(AttemptOutcome.Timeout, a.Outcome));
    }

    [Fact]
    public async Task Timeout_ExtendsTheClock_OnRetry()
    {
        // #119: a same-clock retry just re-times-out, so the retry must get MORE wall-clock. With
        // defaultRetries 2 (3 attempts) all timing out, each successive attempt's timeout must be
        // strictly larger than the previous (1× → 1.5× → 2.25×).
        var runner = new SequencingRunner(Timeout());

        (_, _, SequencingRunner used) = await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        Assert.Equal(3, used.Timeouts.Count);
        Assert.True(used.Timeouts[1] > used.Timeouts[0], "attempt 2 should get a longer clock than attempt 1");
        Assert.True(used.Timeouts[2] > used.Timeouts[1], "attempt 3 should get a longer clock than attempt 2");

        // Concretely: base 60s → 60, 90, 135 (1.5× steps).
        Assert.Equal(60, used.Timeouts[0].TotalSeconds, precision: 0);
        Assert.Equal(90, used.Timeouts[1].TotalSeconds, precision: 0);
        Assert.Equal(135, used.Timeouts[2].TotalSeconds, precision: 0);
    }
}
