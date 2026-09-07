using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// A provider quota limit at a WAVE BARRIER must be waited out, not allowed to end the run (issue #511).
///
/// <para><b>The asymmetry these tests close.</b> The identical 429, arriving one minute earlier inside a
/// task, was ridden out silently by the #115 backoff; arriving at the barrier it ended the run. That was
/// never a detection gap — <c>429</c> is in <c>TransientStatus</c>, "session limit" is a pinned
/// <c>TransientPhrases</c> entry, and <c>ExtractResetHint</c> even parsed "resets 8:30pm" out of the
/// message. The harness knew exactly what had happened and when it would clear, and stopped anyway,
/// because provider-failure policy lived on the task side and the breakdown side never grew it.</para>
///
/// <para>Measured: wave 2 finished green (5/5 tasks, 880 passing), the wave-3 breakdown died one second in
/// on a 429, and the operator read the result as <i>"looks like it finished the wave, but is stuck and
/// won't start the next wave"</i>. Diagnosing it took reading <c>claude-stream.jsonl</c> by hand.</para>
///
/// <para>Every wait here runs through an injected delay, so these tests are sleep-free: what is asserted is
/// how many times the real unit of work was ATTEMPTED and what the operator was told — decisions, not
/// durations (#518).</para>
/// </summary>
public sealed class SchedulerBarrierProviderWaitTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The instant on the reporting run, so "8:30pm" resolves 2h39m ahead and the probe interval wins.
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 17, 51, 0, TimeSpan.FromHours(2));

    private sealed class GreenExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct) =>
            Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
    }

    private sealed class PausedObserver : IRunObserver
    {
        public List<(string Wave, string Reason, TimeSpan Wait, int Probe, DateTimeOffset? Reset, TimeSpan SoFar)>
            Paused { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void WaveBreakdownPaused(
            WaveBreakdownContext context, string reason, TimeSpan wait, int probe,
            DateTimeOffset? resetInstant, TimeSpan waitedSoFar) =>
            Paused.Add((context.WaveDir, reason, wait, probe, resetInstant, waitedSoFar));
    }

    /// <summary>
    /// Refuses with a provider limit for the first <c>refusals</c> calls, then authors the wave — which is
    /// what a quota window actually looks like from the harness's side, and why the probe IS the real unit of
    /// work rather than a synthetic ping.
    /// </summary>
    private sealed class LimitedThenAuthoringRunner(
        int refusals,
        Action<PromptInvocation> author,
        string? resetHint = null) : IPromptRunner
    {
        public int Calls { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= refusals)
            {
                return Task.FromResult(new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    CostUsd = 0m,   // the measured probe: one second, $0.00
                    FailureKind = PromptFailureKind.Transient,
                    ResetHint = resetHint,
                    Summary = "You've hit your session limit"
                });
            }

            author(invocation);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored the wave",
                CostUsd = 0.10m,
                FailureKind = PromptFailureKind.None,
                Summary = "breakdown authored the wave"
            });
        }
    }

    private static (WavePlanBuilder Builder, PlanDefinition Plan) JitPlan(
        int probeIntervalMinutes = 30, int maxProviderWaitHours = 12)
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n- package\n");
        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with
        {
            Config = plan.Config with
            {
                AutonomyPolicy = AutonomyPolicy.Auto,
                ProviderProbeIntervalMinutes = probeIntervalMinutes,
                MaxProviderWaitHours = maxProviderWaitHours
            }
        });
    }

    private static void AuthorTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, RunJournal journal, IRunObserver observer, WaveBreakdownInvoker invoker,
        List<TimeSpan> waited) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: observer, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null,
            providerWaitDelay: (d, _) => { waited.Add(d); return Task.CompletedTask; },
            clock: () => Now);

    [Fact]
    public async Task AProviderLimit_IsWaitedOutAndTheBreakdownRETRIED_ratherThanEndingTheRun()
    {
        // THE bug. Before #511 the first refusal produced authoredTaskCount == 0, which the segment loop
        // (a RESUME loop, not a retry loop) reads as no forward progress, so it went straight to
        // FailBreakdown and the run ended. Segment 2 was never reached.
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var runner = new LimitedThenAuthoringRunner(
            refusals: 1, author: inv => AuthorTask(inv.WorkingDirectory, "01-compile"));
        var observer = new PausedObserver();
        var waited = new List<TimeSpan>();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(
                plan, journal, observer, new WaveBreakdownInvoker(runner), waited)
            .RunAsync(plan, Ct);

        // The real unit of work was attempted a SECOND time — the probe is the retry.
        Assert.Equal(2, runner.Calls);
        Assert.Single(waited);

        // And the wave was authored, so the barrier is not where the run died.
        Assert.NotEqual(WaveHaltKind.BreakdownFailed, report.WaveHalt?.Kind);
    }

    [Fact]
    public async Task TheWaitIsANNOUNCED_becauseASilentTwelveHourWaitIsWorseThanTheCrashItReplaces()
    {
        // Observability is half the feature, and the reason is in the bug report itself: the operator called
        // a DEAD run "stuck". Trading that for a wait that is equally indistinguishable from a hang would be
        // a regression, only a longer one.
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var runner = new LimitedThenAuthoringRunner(
            refusals: 2, author: inv => AuthorTask(inv.WorkingDirectory, "01-compile"));
        var observer = new PausedObserver();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, new WaveBreakdownInvoker(runner), [])
            .RunAsync(plan, Ct);

        Assert.Equal(2, observer.Paused.Count);

        // Numbered, so "how long has this been going on" is answerable at a glance...
        Assert.Equal(1, observer.Paused[0].Probe);
        Assert.Equal(2, observer.Paused[1].Probe);

        // ...and cumulative, so the second line does not read like the first.
        Assert.Equal(TimeSpan.Zero, observer.Paused[0].SoFar);
        Assert.Equal(TimeSpan.FromMinutes(30), observer.Paused[1].SoFar);

        // Named as a LIMIT in the provider's own words, never a generic "transient".
        Assert.Contains("session limit", observer.Paused[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Wave2, observer.Paused[0].Wave);
    }

    [Fact]
    public async Task TheResetHintREACHESTheOperator_insteadOfBeingDroppedAtTheInvokerBoundary()
    {
        // WaveBreakdownOutcome never carried ResetHint, so the instant ExtractResetHint had ALREADY parsed
        // was discarded at that boundary — while the task door had been carrying the same value through to
        // the operator for releases.
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var runner = new LimitedThenAuthoringRunner(
            refusals: 1,
            author: inv => AuthorTask(inv.WorkingDirectory, "01-compile"),
            resetHint: "8:30pm");
        var observer = new PausedObserver();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, new WaveBreakdownInvoker(runner), [])
            .RunAsync(plan, Ct);

        var paused = Assert.Single(observer.Paused);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 20, 30, 0, TimeSpan.FromHours(2)), paused.Reset);
    }

    [Fact]
    public async Task WhenTheLimitNeverClears_TheRunHaltsWithTheHONESTHeadline_notFAILEDValidation()
    {
        // The bound is what makes waiting safe to default to: a permanently revoked key looks exactly like a
        // limit that has not cleared, and only this stops the run hanging forever.
        //
        // The headline is the second half. "breakdown FAILED validation" asserted the wrong thing for this
        // case — nothing failed validation, a provider refused to serve — and a halt that says a FALSE thing
        // costs more than one that says nothing, because the operator goes and debugs the thing it named
        // (here, a validate report for a wave that was never validated).
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan(probeIntervalMinutes: 30, maxProviderWaitHours: 1);
        using WavePlanBuilder _ = b;

        var runner = new LimitedThenAuthoringRunner(refusals: 99, author: _ => { });
        var observer = new PausedObserver();
        var waited = new List<TimeSpan>();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(
                plan, journal, observer, new WaveBreakdownInvoker(runner), waited)
            .RunAsync(plan, Ct);

        // A 1-hour bound at a 30-minute cadence is exactly two probes, then the run stops waiting.
        Assert.Equal(2, observer.Paused.Count);
        Assert.Equal(3, runner.Calls);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.Contains("PROVIDER LIMIT", report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("FAILED validation", report.WaveHalt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonProviderFailure_isNOTWaitedOut()
    {
        // The wait is for a PROVIDER refusal only. A timeout, a turn cap, an output cap, a skill bug or a
        // validate rejection are conditions where re-running the identical prompt is either useless or
        // exactly the wrong thing, and each already has its own remedy. Waiting on those would turn every
        // authoring bug into a twelve-hour silence.
        (WavePlanBuilder b, PlanDefinition plan) = JitPlan();
        using WavePlanBuilder _ = b;

        var runner = new TimeoutRunner();
        var observer = new PausedObserver();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, observer, new WaveBreakdownInvoker(runner), [])
            .RunAsync(plan, Ct);

        Assert.Empty(observer.Paused);
    }

    private sealed class TimeoutRunner : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptResult
            {
                Completed = false,
                IsError = false,
                CostUsd = 0.10m,
                FailureKind = PromptFailureKind.Timeout,
                Summary = "breakdown was cut off"
            });
    }
}
