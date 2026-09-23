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
        public List<int> MaxTurnsSeen { get; } = new();

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
            // Capture the effective per-attempt turn budget so a test can assert the auto-escalation
            // raised it on retry (issue #129 / #94).
            MaxTurnsSeen.Add(invocation.Settings.MaxTurns);
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
        private int _guardrailsFinished;

        /// <summary>How many guardrail runs finished (#708 pins that a succeeded action's guardrails RAN).</summary>
        public int GuardrailsFinished => _guardrailsFinished;

        public void GuardrailFinished(TaskNode task, GuardrailResult result) => Interlocked.Increment(ref _guardrailsFinished);
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

    private static PromptResult MaxTurns() => new()
    {
        // Mirrors a Claude error_max_turns terminal result: completed the process, is_error true, the
        // result text is the turn-budget message, and the CLI quarantine classified it MaxTurns.
        Completed = false,
        IsError = true,
        ResultText = "Reached maximum number of turns (50)",
        FailureKind = PromptFailureKind.MaxTurns,
        NumTurns = 50,
        Summary = "claude reached the turn limit (50 turn(s))"
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
    /// A run that hit a PERMISSION WALL on the given paths (issues #86 / #104). Modelled as a generic
    /// error result (the agent kept trying and eventually reported failure) carrying the runner-agnostic
    /// <see cref="PromptResult.BlockedWritePaths"/> the scanner would have mined from the stream. Whether
    /// the result is "completed" is irrelevant — the harness routes on the blocked-paths list.
    /// </summary>
    private static PromptResult Blocked(params string[] paths) => new()
    {
        Completed = false,
        IsError = true,
        ResultText = "I could not write the required file(s) — the writes were refused.",
        FailureKind = PromptFailureKind.Error,
        BlockedWritePaths = paths,
        Summary = "claude reported is_error (writes refused)"
    };

    /// <summary>
    /// <paramref name="result"/> with a permission wall on <paramref name="refused"/> (#708): a refused write path,
    /// or, when <paramref name="isCommand"/>, a refused command, reported the way the Claude scanner reports one.
    /// </summary>
    private static PromptResult Refusing(PromptResult result, string refused, bool isCommand) => result with
    {
        BlockedWritePaths = [refused],
        RefusedCommands = isCommand ? [refused] : []
    };

    /// <summary>
    /// Build a one-task prompt-action plan (a trivial always-pass deterministic guardrail) and run it
    /// through the real Scheduler with the given fake runner, observer, retry budget and instant
    /// transient delay. Returns the run report + the journal entry for the single task.
    /// </summary>
    /// <summary>
    /// The plan root of the LAST <see cref="RunOneTaskAsync"/> call made with <c>keepRoot: true</c> —
    /// so a test that needs to read on-disk artifacts (e.g. the permission-wall <c>feedback.md</c>)
    /// can locate them after the run. The test that sets it is responsible for deleting the directory.
    /// </summary>
    private string? _lastPlanRoot;

    private async Task<(RunReport Report, TaskJournalEntry Entry, TRunner Runner)> RunOneTaskAsync<TRunner>(
        TRunner runner,
        IRunObserver observer,
        int defaultRetries,
        int transientPauseBudgetSeconds = 1800,
        bool keepRoot = false,
        bool guardrailPasses = true)
        where TRunner : IPromptRunner
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-reliability-" + Guid.NewGuid().ToString("N"));
        if (keepRoot)
        {
            _lastPlanRoot = root;
        }
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

        // Trivial deterministic guardrail (OS-appropriate, directly spawnable): always passes, or, for #708's control,
        // always fails.
        bool win = OperatingSystem.IsWindows();
        string guardrailName = guardrailPasses ? "01-ok" : "01-fail";
        int guardrailExit = guardrailPasses ? 0 : 1;
        string guardrailPath = Path.Combine(taskDir, "guardrails", guardrailName + (win ? ".cmd" : ".sh"));
        File.WriteAllText(guardrailPath,
            win ? $"@echo off\r\nexit /b {guardrailExit}\r\n" : $"#!/usr/bin/env bash\nexit {guardrailExit}\n");
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
                overwatch: null,
                // Instant delay — the backoff is exercised but never actually sleeps (TCS-style gate).
                transientDelay: (_, _) => Task.CompletedTask);

            var scheduler = new Scheduler(load.Plan!, executor, journal, observer: observer);
            RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

            JournalDocument doc = JournalReader.Read(RunJournal.PathFor(root));
            return (report, doc.Tasks["01-task"], runner);
        }
        finally
        {
            if (!keepRoot)
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
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

        TaskResult settled = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, settled.Outcome);
        Assert.Equal(3, used.Calls);                       // 2 transient + 1 success
        Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);

        // #381/doc 12 §4.2: a class-(b) transient that PAUSED and then cleared within budget must SURFACE a
        // resolved-transient signal on the succeeded result — the seam the autonomous layer reads to record a
        // `blocker-retried` forensic entry. Before this fix the executor swallowed the within-budget resolution
        // silently (settled Succeeded with no signal), so a resolved transient recorded nothing.
        Assert.NotNull(settled.ResolvedTransient);
        Assert.Equal(2, settled.ResolvedTransient!.Pauses);

        // The waited total is the sum of TWO DIFFERENT horizons since #511, and this fixture exercises one
        // of each — which is why the figure is composed from the policy's own constants rather than written
        // as a number. Pause 1 is "usage limit reached (resets 11:20am)": a limit that names its reset is a
        // quota window, so it POLLS, and with 11:20am hours away the probe interval wins the min(). Pause 2
        // is a bare "overloaded" with no reset — a blip — so it takes the exponential, at its SECOND step
        // (4s, not 2s): the schedule is indexed by how many pauses this TASK has taken, not by how many it
        // has taken on that particular horizon, so repeated trouble keeps escalating whatever its shape.
        //
        // Writing 6s here (2s + 4s) was correct before #511 and is now the assertion that a session limit is
        // being retried every two seconds.
        Assert.Equal(
            TransientBackoff.DefaultProbeInterval + TransientBackoff.BaseDelay * 2,
            settled.ResolvedTransient.Waited);

        // The retry budget was preserved: only ONE attempt was journaled (a paused re-run is not a new
        // attempt — it re-runs under the same number, which IS the no-retry-consumed contract), and it is
        // the succeeded one.
        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);

        // #515: and the pauses themselves are now DURABLE, on their own task-grained ledger rather than as
        // attempts. This line used to read "the paused retries are observe-only — never journaled", which
        // was the defect stated as a property: a green run that waited out two provider stalls was, in
        // every durable record, identical to one that ran clean. Both entries name the attempt they
        // suspended (1), so the association the attempt list cannot carry is not lost.
        Assert.NotNull(entry.TransientPauses);
        Assert.Equal(2, entry.TransientPauses!.Count);
        Assert.All(entry.TransientPauses, p => Assert.Equal(1, p.Attempt));
        Assert.Equal("11:20am", entry.TransientPauses[0].ResetHint);
        Assert.Null(entry.TransientPauses[1].ResetHint);   // the second transient named no reset time

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
        // settle with the DISTINCT TaskOutcome.RateLimited (issue #190 part 1 — no longer a generic
        // TaskOutcome.NeedsHuman) and a "re-run later" reason — NOT a generic action failure, and NOT a
        // burned retry budget.
        var runner = new SequencingRunner(Transient("session limit · resets 11:20am"));   // never clears

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2,
                transientPauseBudgetSeconds: 1);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.RateLimited, task.Outcome);
        Assert.False(task.IsGreen, "a rate-limited task must NOT be treated as green (dependents must block)");
        Assert.Contains("rate-limited", task.Summary);
        Assert.Contains("re-run later", task.Summary);

        // The JOURNAL status deliberately stays needs-human (issue #190 part 1 design decision, see the
        // AttemptJournaler.RateLimitExhausted doc comment): only the in-memory/per-run TaskOutcome the
        // CLI renders distinguishes rate-limited from a generic needs-human. Resume still treats it like
        // any other needs-human (fresh budget, §7) — unchanged by this issue.
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Contains(entry.Attempts, a => a.Outcome == AttemptOutcome.RateLimited);
        // The action retry budget (3 attempts) was NOT burned re-failing the rate limit.
        Assert.DoesNotContain(entry.Attempts, a => a.Outcome == AttemptOutcome.ActionFailed);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #763 — Claude Code's "individual spend limit" refusal, through the REAL ClaudePromptRunner
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The exact refusal a live Claude Code 2.1.276 printed as its whole output, three times, in #763.</summary>
    private const string LiveSpendLimit =
        "You've hit your individual spend limit · run /usage-credits to ask your admin for a higher limit";

    /// <summary>
    /// A fake <c>claude</c> that behaves like #763's live one: for its first <paramref name="refusals"/>
    /// invocations it prints <see cref="LiveSpendLimit"/> as plain stdout — NO stream envelope at all — and
    /// exits 1; after that it emits a clean terminal result. The invocation count persists in a counter file
    /// next to it, so the test can read how many times the runner really spawned it. OS-picked: a directly
    /// spawnable <c>.cmd</c> forwarding to a <c>.ps1</c> on Windows, a <c>.sh</c> elsewhere.
    /// </summary>
    private static (string CliPath, string CounterPath, string Dir) WriteSpendLimitFakeClaude(int refusals)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-spendlimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string counter = Path.Combine(dir, "invocations.txt");

        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.Combine(dir, "fake-claude.ps1");
            // The middle dot is written as [char]0x00B7 so the script file's own encoding cannot mangle it, and
            // stdout is pinned to UTF-8 because the harness decodes the child as UTF-8 (#55).
            File.WriteAllText(ps1,
                "$null = [Console]::In.ReadToEnd()\n" +
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" +
                $"$counter = '{counter.Replace("'", "''")}'\n" +
                "$n = 0; if (Test-Path $counter) { $n = [int](Get-Content -Raw $counter) }\n" +
                "Set-Content -NoNewline -Path $counter -Value ($n + 1)\n" +
                $"if ($n -lt {refusals}) {{\n" +
                "    [Console]::Out.WriteLine(\"You've hit your individual spend limit $([char]0x00B7) run /usage-credits to ask your admin for a higher limit\")\n" +
                "    exit 1\n" +
                "}\n" +
                "[Console]::Out.WriteLine('{\"type\":\"result\",\"is_error\":false,\"result\":\"done\",\"total_cost_usd\":0.01,\"num_turns\":1}')\n" +
                "exit 0\n");
            string cmd = Path.Combine(dir, "fake-claude.cmd");
            File.WriteAllText(cmd,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"\r\nexit /b %ERRORLEVEL%\r\n");
            return (cmd, counter, dir);
        }

        string sh = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(sh,
            "#!/usr/bin/env bash\n" +
            "cat > /dev/null\n" +
            $"counter='{counter}'\n" +
            "n=0; if [ -f \"$counter\" ]; then n=$(cat \"$counter\"); fi\n" +
            "printf '%s' \"$((n + 1))\" > \"$counter\"\n" +
            $"if [ \"$n\" -lt {refusals} ]; then\n" +
            "  printf 'You'\"'\"'ve hit your individual spend limit \\xc2\\xb7 run /usage-credits to ask your admin for a higher limit\\n'\n" +
            "  exit 1\n" +
            "fi\n" +
            "printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"done\",\"total_cost_usd\":0.01,\"num_turns\":1}\\n'\n" +
            "exit 0\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return (sh, counter, dir);
    }

    private static int Invocations(string counterPath) =>
        int.Parse(File.ReadAllText(counterPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// #763 end to end: the live refusal — plain stdout, no stream, exit 1 — through the REAL
    /// <see cref="ClaudePromptRunner"/> and <see cref="StreamJsonCliSession"/>, not a canned
    /// <see cref="PromptResult"/>. Budget = ONE attempt (defaultRetries 0), and two refusals precede success.
    /// Before the fix those refusals classified Error, so the one attempt was consumed by the first refusal and
    /// the task settled needs-human; #511's decided behavior is that a provider limit at the task door PAUSES
    /// without consuming the budget. Every assertion is on a decision the harness recorded — the outcome, the
    /// journaled attempts, the pause ledger and its horizon — never on elapsed time (the delay is injected
    /// instant).
    /// </summary>
    [Fact]
    public async Task LiveSpendLimitRefusal_ThroughTheRealClaudeRunner_PausesWithoutConsumingRetryBudget()
    {
        (string cli, string counter, string dir) = WriteSpendLimitFakeClaude(refusals: 2);
        try
        {
            var observer = new PauseRecordingObserver();
            var runner = new ClaudePromptRunner("claude", cli, new ProcessRunner());

            (RunReport report, TaskJournalEntry entry, _) =
                await RunOneTaskAsync(runner, observer, defaultRetries: 0);

            // The runner really spawned the fake three times: two refusals, then the success.
            Assert.Equal(3, Invocations(counter));

            TaskResult settled = Assert.Single(report.Tasks);
            Assert.Equal(TaskOutcome.Succeeded, settled.Outcome);
            Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);

            // No retry budget consumed: ONE journaled attempt, and it is the succeeded one — no action-failed
            // attempt for either refusal.
            AttemptRecord attempt = Assert.Single(entry.Attempts);
            Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);

            // Two transient pauses were recorded, both suspending attempt 1. The refusal names no reset time,
            // so there is no hint and each pause took the exponential (blip) horizon, not the quota poll (#511).
            Assert.NotNull(entry.TransientPauses);
            Assert.Equal(2, entry.TransientPauses!.Count);
            Assert.All(entry.TransientPauses, p =>
            {
                Assert.Equal(1, p.Attempt);
                Assert.Equal("exponential", p.Horizon);
                Assert.Null(p.ResetHint);

                // #763's second half: the reason carries the provider's own words, not a bare "claude exited 1".
                Assert.Equal($"claude exited 1: {LiveSpendLimit}", p.Reason);
            });

            Assert.Equal(2, observer.Pauses.Count);
            Assert.All(observer.Pauses, p => Assert.Equal($"claude exited 1: {LiveSpendLimit}", p.Reason));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The other side of the same bound: a spend limit that never clears settles the distinct
    /// <see cref="TaskOutcome.RateLimited"/> ("re-run later") once the pause budget is spent — still without a
    /// single action-failed attempt — and the needs-human line the operator reads names the refusal (#763).
    /// </summary>
    [Fact]
    public async Task LiveSpendLimitRefusal_ThatNeverClears_SettlesRateLimited_NamingTheRefusal_WithoutBurningRetries()
    {
        (string cli, _, string dir) = WriteSpendLimitFakeClaude(refusals: int.MaxValue);
        try
        {
            var runner = new ClaudePromptRunner("claude", cli, new ProcessRunner());

            (RunReport report, TaskJournalEntry entry, _) =
                await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2,
                    transientPauseBudgetSeconds: 1);

            TaskResult task = Assert.Single(report.Tasks);
            Assert.Equal(TaskOutcome.RateLimited, task.Outcome);
            Assert.Contains("re-run later", task.Summary);
            Assert.Contains($"claude exited 1: {LiveSpendLimit}", task.Summary);

            Assert.Contains(entry.Attempts, a => a.Outcome == AttemptOutcome.RateLimited);
            Assert.DoesNotContain(entry.Attempts, a => a.Outcome == AttemptOutcome.ActionFailed);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #129 / #94 — max-turns: distinct outcome, actionable feedback, AUTO-ESCALATE the turn budget
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MaxTurns_RecordsDistinctOutcome_NotGenericActionFailure()
    {
        // Always max-turns, budget 1 (one retry): two attempts, both max-turns, then needs-human. The
        // journal must show the DISTINCT max-turns outcome (not generic action-failed) so a human (and
        // §9 triage) sees a TURN-budget issue, not a logic failure.
        var runner = new SequencingRunner(MaxTurns());

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 1);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.ActionFailed, task.Outcome);
        Assert.Contains("ran out of turns", task.Summary);

        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.All(entry.Attempts, a => Assert.Equal(AttemptOutcome.MaxTurns, a.Outcome));
        Assert.Equal(2, used.Calls);
    }

    [Fact]
    public async Task MaxTurns_EscalatesTheTurnBudget_OnRetry()
    {
        // #129 / #94: a same-budget retry just re-exhausts at the same cap, so the retry must get a
        // LARGER turn budget. With defaultRetries 2 (3 attempts) all hitting max-turns, each successive
        // attempt's effective maxTurns must be strictly larger than the previous (1× → 1.5× → 2.25×).
        var runner = new SequencingRunner(MaxTurns());

        (_, _, SequencingRunner used) = await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        Assert.Equal(3, used.MaxTurnsSeen.Count);
        Assert.True(used.MaxTurnsSeen[1] > used.MaxTurnsSeen[0], "attempt 2 should get a larger turn budget than attempt 1");
        Assert.True(used.MaxTurnsSeen[2] > used.MaxTurnsSeen[1], "attempt 3 should get a larger turn budget than attempt 2");

        // Concretely: default 50 → 50, 75 (ceil 50×1.5), 113 (ceil 50×2.25).
        Assert.Equal(50, used.MaxTurnsSeen[0]);
        Assert.Equal(75, used.MaxTurnsSeen[1]);
        Assert.Equal(113, used.MaxTurnsSeen[2]);
    }

    [Fact]
    public async Task MaxTurns_OnlyEscalatesAfterAMaxTurnsFailure_NotAfterAGenericFailure()
    {
        // The escalation is SPECIFIC to max-turns: a generic action failure must NOT raise the turn
        // budget (it would not help). A timeout then a max-turns: the timeout attempt does not bump
        // turns, so the second attempt still sees the base budget; only a PRIOR max-turns bumps it.
        var runner = new SequencingRunner(Timeout(), MaxTurns(), MaxTurns());

        (_, _, SequencingRunner used) = await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        Assert.Equal(3, used.MaxTurnsSeen.Count);
        // Attempt 1 (timeout) and attempt 2 (first max-turns) both see the base budget — the timeout
        // did not escalate turns. Attempt 3 follows ONE prior max-turns, so it is bumped.
        Assert.Equal(50, used.MaxTurnsSeen[0]);
        Assert.Equal(50, used.MaxTurnsSeen[1]);
        Assert.Equal(75, used.MaxTurnsSeen[2]);
    }

    [Fact]
    public async Task MaxTurns_WritesActionableFeedback_ContinueFromPartialWork()
    {
        // The retry feedback must steer the agent to CONTINUE from preserved partial work and work
        // directly — not re-explore — and tell it the budget was raised. Distinct from a generic
        // "claude exited 1" so the retry changes behaviour instead of re-hitting the wall.
        var runner = new SequencingRunner(MaxTurns());

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 1, keepRoot: true);

        try
        {
            Assert.Equal(TaskOutcome.ActionFailed, Assert.Single(report.Tasks).Outcome);

            // The first failed attempt's feedback.md carries the max-turns guidance.
            AttemptRecord first = entry.Attempts[0];
            string feedbackPath = Path.Combine(_lastPlanRoot!, first.LogDir, "feedback.md");
            Assert.True(File.Exists(feedbackPath), $"expected feedback at {feedbackPath}");
            string feedback = File.ReadAllText(feedbackPath);
            Assert.Contains("ran out of turns", feedback);
            Assert.Contains("PARTIAL WORK", feedback);
            Assert.Contains("RAISED the turn budget", feedback);
        }
        finally
        {
            try { Directory.Delete(_lastPlanRoot!, recursive: true); } catch (IOException) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #104 — a .claude/ write wall is STRUCTURAL: needs-human on the FIRST hit, zero retries burned
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaudeDirWrite_Blocked_SettlesNeedsHuman_OnFirstAttempt_NoRetryBurn()
    {
        // The #104 repro: a task whose deliverable is under .claude/ is refused by the runtime. Even
        // with a generous retry budget (2 → 3 attempts), the harness must settle needs-human on the
        // FIRST attempt — a .claude/ wall is structural and no retry can clear it.
        var runner = new SequencingRunner(Blocked(".claude/skills/certify-knowledge/SKILL.md"));

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains(".claude/", task.Summary);
        Assert.Contains("structural", task.Summary);

        // Exactly ONE attempt journaled, with the DISTINCT permission-denied outcome — the remaining
        // two budgeted attempts were NOT burned on the identical, un-retryable wall (#104's core waste).
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        AttemptRecord attempt = Assert.Single(entry.Attempts);
        Assert.Equal(AttemptOutcome.PermissionDenied, attempt.Outcome);
        Assert.Equal(1, used.Calls);
    }

    [Fact]
    public async Task ClaudeDirWall_WritesTaskLevelFeedback_NamingThePathAndRemediation()
    {
        // The needs-human message must point a human at WHAT is blocked and HOW to fix it: the .claude/
        // path, the acceptEdits restriction, and the CURRENT remedies — needsHarnessWrite (#191) as the
        // primary autonomous fix, a staging path, and the session-wide bypassPermissions fallback — with
        // the old Write(.claude/**) settings grant named only to mark it RETIRED (#273, no longer works).
        var runner = new SequencingRunner(Blocked(".claude/agents/reviewer.md"));

        (RunReport report, TaskJournalEntry entry, _) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 1, keepRoot: true);

        try
        {
            Assert.Equal(TaskOutcome.NeedsHuman, Assert.Single(report.Tasks).Outcome);

            // feedback.md was written into the (sole) attempt's log dir; it names the path + remediations.
            AttemptRecord attempt = Assert.Single(entry.Attempts);
            string feedbackPath = Path.Combine(_lastPlanRoot!, attempt.LogDir, "feedback.md");
            Assert.True(File.Exists(feedbackPath), $"expected feedback at {feedbackPath}");
            string feedback = File.ReadAllText(feedbackPath);
            Assert.Contains(".claude/agents/reviewer.md", feedback);
            Assert.Contains("acceptEdits", feedback);
            Assert.Contains("needsHarnessWrite", feedback);                            // #191 primary remedy leads
            Assert.Contains("bypassPermissions", feedback);                            // session-wide fallback named
            Assert.Contains("Write(.claude/**)", feedback);                            // named only to mark it retired (#273)
            Assert.Contains("staging", feedback, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(_lastPlanRoot!, recursive: true); } catch (IOException) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #86 — the SAME write path refused across attempts: settle needs-human on the repeat, not burn out
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SameNonClaudePath_BlockedTwice_SettlesNeedsHuman_OnTheRepeat_NotAllRetries()
    {
        // #86: a non-.claude path refused on attempt 1 (one chance to clear) then refused AGAIN on
        // attempt 2 is a wall. With budget 3 (defaultRetries 2), the harness must settle needs-human
        // after the SECOND attempt — NOT spend the third on the identical wall.
        var runner = new SequencingRunner(Blocked("src/locked/Protected.cs"));   // every call hits the same wall

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains("permission wall", task.Summary);
        Assert.Contains("src/locked/Protected.cs", task.Summary);

        // Two attempts journaled: attempt 1 failed (action-failed), attempt 2 settled permission-denied.
        // The THIRD budgeted attempt was not burned on the same wall (#86's core waste).
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(2, entry.Attempts.Count);
        Assert.Equal(AttemptOutcome.PermissionDenied, entry.Attempts[^1].Outcome);
        Assert.Equal(2, used.Calls);
    }

    [Fact]
    public async Task NonClaudePath_BlockedOnceThenSucceeds_DoesNotEarlyHalt()
    {
        // A one-off non-.claude refusal must NOT short-circuit: the retry is given its chance, and when
        // it clears the task succeeds. Only a REPEAT (or a structural .claude/ path) halts — a single
        // blocked write that the retry resolves is normal retry behaviour, not a wall.
        var runner = new SequencingRunner(Blocked("src/locked/Protected.cs"), Success());

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);
        Assert.Equal(2, used.Calls);                       // 1 blocked + 1 success
        Assert.DoesNotContain(entry.Attempts, a => a.Outcome == AttemptOutcome.PermissionDenied);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #708 — a repeated refusal settles only an attempt whose ACTION FAILED; a succeeded action is settled by its
    // guardrails
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pwsh -NoProfile -File tasks/21-update-ssot/guardrails/01-ssot-records-the-contracts.ps1", true)]
    [InlineData("src/locked/Protected.cs", false)]
    public async Task RepeatedRefusal_OnAnAttemptWhoseActionSucceeded_RunsTheGuardrails_AndAPassIsGreen(string refused, bool isCommand)
    {
        // Plan 40, task 21: attempt 1 ran out of turns reaching for a self-check it was not granted, and attempt 2
        // FINISHED the deliverable and reached for the same self-check once more. The eager #86 halt settled attempt 2
        // permission-denied before its guardrail ran, a guardrail that would have passed. Only --autonomous got the
        // finished work past it.
        var observer = new PauseRecordingObserver();
        var runner = new SequencingRunner(Refusing(MaxTurns(), refused, isCommand), Refusing(Success(), refused, isCommand));

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, observer, defaultRetries: 2);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(JournalTaskStatus.Succeeded, entry.Status);
        Assert.Equal(new[] { AttemptOutcome.MaxTurns, AttemptOutcome.Succeeded }, entry.Attempts.Select(a => a.Outcome));
        // The guardrail certified the green: it ran once, on attempt 2. Attempt 1's action failed, so none ran there.
        Assert.Equal(1, observer.GuardrailsFinished);
        Assert.Equal(2, used.Calls);
    }

    [Fact]
    public async Task RepeatedCommand_OnAnAttemptWhoseGuardrailFails_Retries_WithTheRefusalAsSecondaryContext()
    {
        // #708's own case: a refused optional self-check. A succeeded action is settled by its guardrails, so a FAILING
        // guardrail fails the attempt, as guardrail-failed rather than permission-denied, and a refused COMMAND does not
        // halt it: the retry runs. This is also the control for the deferral, since a change that dropped the halt by
        // going straight to green, without running the guardrails, would settle this task green.
        const string selfCheck = "echo \"EXIT:$?\"";
        var observer = new PauseRecordingObserver();
        var runner = new SequencingRunner(
            Refusing(MaxTurns(), selfCheck, isCommand: true), Refusing(Success(), selfCheck, isCommand: true));

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) = await RunOneTaskAsync(
            runner, observer, defaultRetries: 2, keepRoot: true, guardrailPasses: false);

        try
        {
            TaskResult task = Assert.Single(report.Tasks);
            Assert.False(task.IsGreen);
            Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);   // the budget of three ran out
            Assert.Equal(
                new[] { AttemptOutcome.MaxTurns, AttemptOutcome.GuardrailFailed, AttemptOutcome.GuardrailFailed },
                entry.Attempts.Select(a => a.Outcome));
            Assert.Equal(3, used.Calls);
            Assert.Equal("01-fail", Assert.Single(entry.Attempts[1].FailedGuardrails).Name);
            Assert.Equal(2, observer.GuardrailsFinished);

            // Secondary context, in the summary and in the feedback the retry read.
            Assert.Contains($"secondary context: command repeatedly refused (permission wall) — {selfCheck}", task.Summary);
            string feedback = File.ReadAllText(Path.Combine(_lastPlanRoot!, entry.Attempts[1].LogDir, "feedback.md"));
            Assert.Contains("## Secondary context", feedback);
            Assert.Contains($"- command: `{selfCheck}`", feedback);
        }
        finally
        {
            try { Directory.Delete(_lastPlanRoot!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RepeatedWritePath_OnAnAttemptWhoseGuardrailFails_HaltsNeedsHuman_OnThatAttempt()
    {
        // The same shape with a refused WRITE PATH, which is not a route the agent can do without: nothing grants the
        // write between attempts, so a retry would be refused it again and fail the same guardrail. That is the budget
        // #86 protects, so this attempt halts on the repeat, as #86 did, and reports the guardrail failure that ran
        // before the wall (#329).
        const string lockedPath = "src/locked/Protected.cs";
        var observer = new PauseRecordingObserver();
        var runner = new SequencingRunner(
            Refusing(MaxTurns(), lockedPath, isCommand: false), Refusing(Success(), lockedPath, isCommand: false));

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) = await RunOneTaskAsync(
            runner, observer, defaultRetries: 2, keepRoot: true, guardrailPasses: false);

        try
        {
            TaskResult task = Assert.Single(report.Tasks);
            Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
            Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
            // Settled on attempt 2: the third budgeted attempt was not spent on a write nothing will grant.
            Assert.Equal(new[] { AttemptOutcome.MaxTurns, AttemptOutcome.GuardrailFailed }, entry.Attempts.Select(a => a.Outcome));
            Assert.Equal(2, used.Calls);
            Assert.Equal("01-fail", Assert.Single(entry.Attempts[1].FailedGuardrails).Name);
            Assert.Equal(1, observer.GuardrailsFinished);
            Assert.Equal(
                $"guardrail(s) failed: 01-fail — needs human; write repeatedly refused (permission wall) — {lockedPath} (see feedback)",
                task.Summary);

            string feedback = File.ReadAllText(Path.Combine(_lastPlanRoot!, entry.Attempts[1].LogDir, "feedback.md"));
            Assert.Contains("## A guardrail failed", feedback);
            Assert.Contains("## Repeatedly-refused path(s)", feedback);
            Assert.Contains($"- `{lockedPath}`", feedback);
        }
        finally
        {
            try { Directory.Delete(_lastPlanRoot!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SameRefusedCommand_OnTwoFailedAttempts_StillHaltsOnTheRepeat_AndIsCalledACommand()
    {
        // #708 keeps the #86 halt where it protects the budget: an attempt that could not finish re-refused the same
        // call. This command names a .claude/ path, which before #708 made it the STRUCTURAL write wall, settled on
        // attempt 1 as "write to .claude/ blocked". A refused grep is not a write, so it gets its retry and then halts
        // on the repeat, named as the command it is.
        const string grep = "grep -rn needsHarnessWrite /repo/.claude/skills";
        var runner = new SequencingRunner(Refusing(Blocked(), grep, isCommand: true));

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Equal(new[] { AttemptOutcome.ActionFailed, AttemptOutcome.PermissionDenied }, entry.Attempts.Select(a => a.Outcome));
        Assert.Equal(2, used.Calls);
        Assert.Equal($"needs human: command repeatedly refused (permission wall) — {grep}", task.Summary);
    }

    [Fact]
    public async Task AnAttemptThatPausedAndReran_CountsItsRefusalOnce_SoItIsNotARepeatOnItsOwn()
    {
        // #708 W5: the wall is observed before the transient-pause check, so an attempt that a rate limit paused and re-ran under
        // the same number was observed twice. One attempt then counted as two, and attempt 1 settled permission-denied as a
        // REPEATED wall before any second attempt existed.
        const string lockedPath = "src/locked/Protected.cs";
        var runner = new SequencingRunner(
            Refusing(Transient("overloaded"), lockedPath, isCommand: false),   // attempt 1, paused
            Refusing(Blocked(), lockedPath, isCommand: false));               // attempt 1 re-run, then attempt 2

        (RunReport report, TaskJournalEntry entry, SequencingRunner used) =
            await RunOneTaskAsync(runner, new PauseRecordingObserver(), defaultRetries: 2);

        Assert.Equal(TaskOutcome.NeedsHuman, Assert.Single(report.Tasks).Outcome);
        // Attempt 1 retried; the repeat is attempt 2's.
        Assert.Equal(new[] { AttemptOutcome.ActionFailed, AttemptOutcome.PermissionDenied }, entry.Attempts.Select(a => a.Outcome));
        Assert.Equal(3, used.Calls);
    }
}
