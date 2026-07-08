using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// M2b wave-execution loop tests (SSOT §14): continuity (one integration handle + one journal + one plan
/// branch across waves), the hard barrier, cross-wave resume, and wave-level drift. Uses a real on-disk
/// waved plan (so PlanHash / WaveDefinitionHash read real files) + a real <see cref="RunJournal"/> + a
/// fake executor (no processes) + <see cref="RecordingWorktreeProvider"/> (no git). Green results route
/// through the Scheduler's B1 settle (<c>DeferredSettle</c>) so a succeeded task is journaled exactly as a
/// real worktree run journals it — which is what makes the journal-continuity + resume assertions real.
/// </summary>
public sealed class SchedulerWaveExecutionTests
{
    /// <summary>Fake executor: green tasks defer their settle to the Scheduler (which journals them); a scripted-fail task ends needs-human.</summary>
    private sealed class WaveFakeExecutor(params string[] failIds) : ITaskExecutor
    {
        private readonly HashSet<string> _fail = new(failIds, StringComparer.Ordinal);
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);
            bool fail = _fail.Contains(task.Id);
            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = fail ? TaskOutcome.NeedsHuman : TaskOutcome.Succeeded,
                Summary = fail ? "scripted needs-human" : "scripted success",
                DeferredSettle = !fail // green routes through SettleAsync → journals Succeeded (as a real run does)
            });
        }
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, ITaskExecutor exec, RunJournal journal, IWorktreeProvider provider,
        int parallelism = 4, IReadOnlySet<string>? waveDriftAuthorized = null) =>
        new(plan, exec, journal,
            worktreeProvider: provider, observer: IRunObserver.Null, maxParallelism: parallelism,
            reVerifier: null, waveDriftAuthorized: waveDriftAuthorized);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- 1. Continuity: one integration handle + one journal + both waves recorded --------------

    [Fact]
    public async Task TwoWavePlan_RunsBothWaves_OneIntegration_OneJournal_Wave1RecordsNotDropped()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-01-scaffold", "02-state", ["01-config"])
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan!;

        var provider = new RecordingWorktreeProvider();
        var exec = new WaveFakeExecutor();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);

        Assert.True(report.AllSucceeded);

        // ONE continuous integration handle across BOTH waves (the M2a continuity blocker) — never one per wave.
        Assert.Equal(1, provider.CreateIntegrationCallCount);

        // ONE continuous journal: wave-1's task records are NOT dropped when wave-2 runs.
        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("wave-01-scaffold/01-config"));
        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("wave-01-scaffold/02-state"));
        Assert.Equal(JournalTaskStatus.Succeeded, journal.StatusOf("wave-02-build/01-compile"));
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-01-scaffold")!.Status);
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-02-build")!.Status);

        // A Guardrails-Wave: marker commit per wave (decision E), in strict wave order.
        string[] markerOrder = [.. provider.WaveMarkerCalls.Select(m => m.WaveDir)];
        Assert.Equal(["wave-01-scaffold", "wave-02-build"], markerOrder);
    }

    // --- 2. Hard barrier: a wave-1 needs-human halts the run; later waves never start ------------

    [Fact]
    public async Task Barrier_Wave1NeedsHuman_HaltsRun_LaterWaveNeverStarts()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan!;

        var provider = new RecordingWorktreeProvider();
        var exec = new WaveFakeExecutor("wave-01-scaffold/01-config"); // wave-1 halts
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);

        Assert.False(report.AllSucceeded);
        // BARRIER: the second wave's task is NEVER dispatched.
        Assert.DoesNotContain("wave-02-build/01-compile", exec.Started);
        // wave-1 never completed → no marker commit for it (nor for wave-2).
        Assert.Empty(provider.WaveMarkerCalls);
        // The later wave's task is reported blocked (not started) — non-green → exit 2.
        Assert.Equal(TaskOutcome.Blocked, report.Tasks.Single(t => t.TaskId == "wave-02-build/01-compile").Outcome);
    }

    // --- 3. Ordering: wave-2 never starts until wave-1 has fully drained -------------------------

    [Fact]
    public async Task Barrier_AllOfWave1Drains_BeforeAnyOfWave2Starts()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-a")
         .Task("wave-01-scaffold", "02-b")
         .Task("wave-02-build", "01-c");
        PlanDefinition plan = b.Load().Plan!;

        var provider = new RecordingWorktreeProvider();
        var exec = new WaveFakeExecutor();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);

        Assert.True(report.AllSucceeded);
        string[] order = [.. exec.Started];
        int lastWave1 = Math.Max(Array.IndexOf(order, "wave-01-scaffold/01-a"), Array.IndexOf(order, "wave-01-scaffold/02-b"));
        int firstWave2 = Array.IndexOf(order, "wave-02-build/01-c");
        Assert.True(lastWave1 >= 0 && firstWave2 > lastWave1,
            $"wave-2 started before wave-1 fully drained (order: {string.Join(", ", order)})");
    }

    // --- 4a. Cross-wave resume: a completed wave is skipped ------------------------------------

    [Fact]
    public async Task Resume_AfterWave1Completes_SkipsWave1_RunsWave2()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan!;

        // Run 1: wave-1 completes; wave-2 halts (so a resume still has work to do).
        var e1 = new WaveFakeExecutor("wave-02-build/01-compile");
        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, e1, j1, new RecordingWorktreeProvider()).RunAsync(plan, Ct);
        Assert.Contains("wave-01-scaffold/01-config", e1.Started);

        // Run 2 (resume): wave-1 is complete → skipped entirely; wave-2 re-runs (now succeeds).
        var e2 = new WaveFakeExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, e2, j2, new RecordingWorktreeProvider()).RunAsync(plan, Ct);

        Assert.DoesNotContain("wave-01-scaffold/01-config", e2.Started); // wave-1 skipped (marker)
        Assert.Contains("wave-02-build/01-compile", e2.Started);         // wave-2 re-entered
        Assert.True(report.AllSucceeded);
    }

    // --- 4b. Cross-wave resume: killed mid-wave re-enters that wave via the per-task pre-pass ----

    [Fact]
    public async Task Resume_MidWave2_ReEntersWave2_SkipsItsDoneTask_RunsPendingTask()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-first")
         .Task("wave-02-build", "02-second", ["01-first"]);
        PlanDefinition plan = b.Load().Plan!;

        // Run 1: wave-1 completes; wave-2's first task succeeds; its second task halts.
        var e1 = new WaveFakeExecutor("wave-02-build/02-second");
        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, e1, j1, new RecordingWorktreeProvider(), parallelism: 1).RunAsync(plan, Ct);
        Assert.Contains("wave-02-build/01-first", e1.Started);

        // Run 2 (resume): wave-1 skipped; wave-2 RE-ENTERED — its succeeded first task is skipped by the
        // per-task pre-pass, only the pending second task runs.
        var e2 = new WaveFakeExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, e2, j2, new RecordingWorktreeProvider(), parallelism: 1).RunAsync(plan, Ct);

        Assert.DoesNotContain("wave-01-scaffold/01-config", e2.Started);
        Assert.DoesNotContain("wave-02-build/01-first", e2.Started);  // per-task resume skip inside the wave
        Assert.Contains("wave-02-build/02-second", e2.Started);
        Assert.True(report.AllSucceeded);
    }

    // --- 5. Wave drift: a completed wave's hash change on resume ---------------------------------

    [Fact]
    public async Task WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan! with { }; // loaded plan; config default (Prompt)

        // Run 1: both waves complete.
        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        RunReport r1 = await NewScheduler(plan, new WaveFakeExecutor(), j1, new RecordingWorktreeProvider()).RunAsync(plan, Ct);
        Assert.True(r1.AllSucceeded);

        // Change a wave-1 task's guardrail on disk → wave-1's WaveDefinitionHash drifts.
        File.WriteAllText(
            Path.Combine(plan.PlanDirectory, "wave-01-scaffold", "tasks", "01-config", "guardrails", "01-ok.sh"),
            "#!/bin/sh\n# edited\nexit 0\n");

        // Run 2 with autonomyPolicy=auto: the drifted completed wave-1 is rewound + re-run.
        PlanDefinition autoPlan = plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } };
        var e2 = new WaveFakeExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(autoPlan);
        RunReport r2 = await NewScheduler(autoPlan, e2, j2, new RecordingWorktreeProvider()).RunAsync(autoPlan, Ct);

        Assert.True(r2.AllSucceeded);
        Assert.Contains("wave-01-scaffold/01-config", e2.Started); // drifted wave re-run
        // A boundary:"wave" decision was recorded in the durable decisions[] log.
        Assert.Contains(j2.Document.Decisions ?? [], d => d.Boundary == "wave");
    }

    [Fact]
    public async Task WaveDrift_CompletedWaveChanged_HaltPolicy_HaltsWithWaveDrift_NotReRun()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan!;

        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, new WaveFakeExecutor(), j1, new RecordingWorktreeProvider()).RunAsync(plan, Ct);

        File.WriteAllText(
            Path.Combine(plan.PlanDirectory, "wave-01-scaffold", "tasks", "01-config", "guardrails", "01-ok.sh"),
            "#!/bin/sh\n# edited\nexit 0\n");

        PlanDefinition haltPlan = plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Halt } };
        var e2 = new WaveFakeExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(haltPlan);
        RunReport r2 = await NewScheduler(haltPlan, e2, j2, new RecordingWorktreeProvider()).RunAsync(haltPlan, Ct);

        Assert.False(r2.AllSucceeded);
        Assert.NotNull(r2.WaveHalt);
        Assert.Equal(WaveHaltKind.WaveDrift, r2.WaveHalt!.Kind);
        Assert.Equal("wave-01-scaffold", r2.WaveHalt.WaveDir);
        Assert.DoesNotContain("wave-01-scaffold/01-config", e2.Started); // NOT re-run under halt
    }

    // --- 6. A change to an all-pending FUTURE wave is NOT drift ----------------------------------

    [Fact]
    public async Task PendingFutureWaveEdit_IsNotDrift_RunsNormally()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config")
         .Task("wave-02-build", "01-compile");
        PlanDefinition plan = b.Load().Plan!;

        // Run 1: wave-1 completes; wave-2 halts, leaving wave-2 NOT completed (its task pending on resume).
        var e1 = new WaveFakeExecutor("wave-02-build/01-compile");
        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, e1, j1, new RecordingWorktreeProvider()).RunAsync(plan, Ct);

        // Edit the (never-completed) wave-2 task — a forward adjustment, not drift (§14.7 isCompleted).
        File.WriteAllText(
            Path.Combine(plan.PlanDirectory, "wave-02-build", "tasks", "01-compile", "guardrails", "01-ok.sh"),
            "#!/bin/sh\n# edited pending wave\nexit 0\n");

        // Run 2 under the default prompt policy, NON-interactive: a real wave drift would halt; a pending
        // future-wave edit must NOT — it just runs.
        var e2 = new WaveFakeExecutor();
        RunJournal j2 = RunJournal.LoadOrCreate(plan);
        RunReport r2 = await NewScheduler(plan, e2, j2, new RecordingWorktreeProvider()).RunAsync(plan, Ct);

        Assert.True(r2.AllSucceeded);
        Assert.Null(r2.WaveHalt);
        Assert.Contains("wave-02-build/01-compile", e2.Started);
    }

    // --- Between-wave JIT checkpoint: an unauthored (empty) next wave honest-halts ---------------

    [Fact]
    public async Task UnauthoredNextWave_HonestHalts_WithJitCheckpoint()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-config");
        // wave-02 folder exists but has no tasks (a JIT stub).
        b.RootDir(Path.Combine("wave-02-build", "tasks"));
        PlanDefinition plan = b.Load().Plan!;
        Assert.Equal(2, plan.Waves.Count);
        Assert.Empty(plan.Waves[1].Tasks);

        var e = new WaveFakeExecutor();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, e, journal, new RecordingWorktreeProvider()).RunAsync(plan, Ct);

        Assert.False(report.AllSucceeded);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Equal("wave-02-build", report.WaveHalt.WaveDir);
        // wave-1 still completed before the halt.
        Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-01-scaffold")!.Status);
    }

    // --- 7. Wave-scoped reset: reset a wave + its downstream waves to pending --------------------

    [Fact]
    public async Task WaveScopedReset_ResetsThatWaveAndDownstream_LeavesEarlierWavesComplete()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-a")
         .Task("wave-02-build", "01-b")
         .Task("wave-03-ship", "01-c");
        PlanDefinition plan = b.Load().Plan!;

        // Run to completion so the journal records all three waves complete.
        RunJournal j1 = RunJournal.LoadOrCreate(plan);
        RunReport r1 = await NewScheduler(plan, new WaveFakeExecutor(), j1, new RecordingWorktreeProvider()).RunAsync(plan, Ct);
        Assert.True(r1.AllSucceeded);

        // Wave-scoped reset of wave-2 rewinds wave-2 + wave-3 (its downstream), leaving wave-1 complete.
        RunReset.WaveResetResult result = RunReset.WaveReset(plan, "wave-02-build");
        Assert.Equal(RunReset.WaveResetOutcome.Done, result.Outcome);
        Assert.Equal(["wave-02-build", "wave-03-ship"], result.ResetWaves.ToArray());

        RunJournal reloaded = RunJournal.LoadOrCreate(plan);
        Assert.Equal(WaveStatus.Completed, reloaded.WaveEntryOf("wave-01-scaffold")!.Status);
        Assert.Equal(WaveStatus.Pending, reloaded.WaveEntryOf("wave-02-build")!.Status);
        Assert.Equal(WaveStatus.Pending, reloaded.WaveEntryOf("wave-03-ship")!.Status);
        Assert.Equal(JournalTaskStatus.Succeeded, reloaded.StatusOf("wave-01-scaffold/01-a"));
        Assert.Equal(JournalTaskStatus.Pending, reloaded.StatusOf("wave-02-build/01-b"));
        Assert.Equal(JournalTaskStatus.Pending, reloaded.StatusOf("wave-03-ship/01-c"));
        Assert.Contains(reloaded.Document.Decisions ?? [], d => d.Boundary == "wave");
    }

    [Fact]
    public void WaveScopedReset_UnknownWave_ReportsUnknown()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-a");
        PlanDefinition plan = b.Load().Plan!;
        RunJournal _ = RunJournal.LoadOrCreate(plan); // journal must exist

        RunReset.WaveResetResult result = RunReset.WaveReset(plan, "wave-99-nope");
        Assert.Equal(RunReset.WaveResetOutcome.UnknownWave, result.Outcome);
        Assert.Equal("wave-99-nope", result.UnknownWaveDir);
    }
}
