using System.Collections.Concurrent;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// Design 39 §4 ("The record, pinned at review") / §5 ("Wiring and halts"): the WIRING between the barrier
/// delivery task 08 built (<see cref="Scheduler"/>'s <c>AttemptBarrierDeliveryAsync</c>) and the record +
/// event tasks 09-13 built (<see cref="RunJournal.RecordWaveDelivery"/>, <see cref="WaveDeliveredRecord"/>,
/// <see cref="IRunObserver.WaveDelivered"/>). Nothing connects them on this tree — #120's shape, a feature
/// green in every unit test and dead in the product — so every row here drives the REAL
/// <see cref="Scheduler"/> and reads what it left on <c>run.json</c>, never an in-memory shortcut.
///
/// <para><b>Five rows are exempt from the red census</b> (noted on each): they pin a guarantee that already
/// holds by construction on this base, because nothing here writes a record, fills
/// <see cref="RunReport.WaveDeliveries"/>, or raises the event for ANY plan yet.</para>
/// </summary>
public sealed class WaveDeliveryWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One recognizable 40-hex-char sha shared by every row that needs a stand-in plan-branch tip.</summary>
    private static readonly string PlanTip = new('a', 40);

    /// <summary>
    /// A SECOND, DISTINCT 40-hex-char sha for row 16 — the one row whose trial is NOT built from the plan
    /// tip (the user's branch moved), so its <c>UserTip</c> must differ from <see cref="PlanTip"/> for the
    /// <c>git log &lt;planTip10&gt;..&lt;userTip10&gt;</c> range to mean anything.
    /// </summary>
    private static readonly string TrialUserTip = new('b', 40);

    private const string DeliversTrueBrief = "---\ndelivers: true\n---\n\nintent\n";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixture builders
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Give <paramref name="waveDir"/> the brief + exit gate that make <c>IsDeliveryPoint</c> true.</summary>
    private static WavePlanBuilder MakeDelivering(WavePlanBuilder b, string waveDir, string exitGateName = "01-exit.sh") =>
        b.WaveGuardrail(waveDir, exitGateName, "exit 0\n").WaveBrief(waveDir, DeliversTrueBrief);

    private static PlanDefinition Load(WavePlanBuilder b)
    {
        PlanLoadResult result = b.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Plan!;
    }

    /// <summary>Read a wave's journal entry straight off <c>run.json</c> — never the in-memory document,
    /// which cannot tell a persisted record from an unpersisted one.</summary>
    private static WaveJournalEntry? WaveEntryOnDisk(RunJournal journal, string waveDir)
    {
        JournalDocument doc = JournalReader.Read(journal.JournalPath);
        return doc.Waves is not null && doc.Waves.TryGetValue(waveDir, out WaveJournalEntry? entry) ? entry : null;
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, ITaskExecutor exec, RunJournal journal, IWorktreeProvider? provider,
        IRunObserver? observer = null, IReVerifier? reVerifier = null, int parallelism = 4) =>
        new(plan, exec, journal,
            worktreeProvider: provider, observer: observer ?? IRunObserver.Null, maxParallelism: parallelism,
            reVerifier: reVerifier);

    /// <summary>
    /// Fake executor shaped like <c>SchedulerWaveExecutionTests.WaveFakeExecutor</c>: named tasks fail, every
    /// other task succeeds and defers its settle. Two extra, optional, single-use hooks this file's rows need:
    /// planting a wave-attributed decision (task 06's <see cref="DecisionEntry.Wave"/>) while a task runs, and
    /// a task editing its OWN <c>task.json</c> mid-execution (the #556 divergence fixture).
    /// </summary>
    private sealed class WaveFakeExecutor(
        RunJournal? journal = null,
        IReadOnlyCollection<string>? failIds = null,
        (string TaskId, string Wave)? plantDecisionAt = null,
        string? editOwnDefinitionAt = null) : ITaskExecutor
    {
        private readonly HashSet<string> _fail = new(failIds ?? [], StringComparer.Ordinal);
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken cancellationToken)
        {
            Started.Enqueue(task.Id);

            if (plantDecisionAt is { } pd && pd.TaskId == task.Id)
            {
                journal!.RecordDecision(new DecisionEntry
                {
                    Boundary = "wave",
                    Policy = "auto",
                    Decision = DecisionTokens.ProceededBestGuess,
                    Subject = task.Id,
                    Headline = $"best-guessed while running {task.Id}",
                    Wave = pd.Wave
                });
            }

            bool fail = _fail.Contains(task.Id);

            if (!fail && task.Id == editOwnDefinitionAt)
            {
                File.WriteAllText(
                    Path.Combine(task.Directory, "task.json"),
                    """{ "description": "edited mid-run by its own action", "writeScope": [] }""");
            }

            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = fail ? TaskOutcome.NeedsHuman : TaskOutcome.Succeeded,
                Summary = fail ? "scripted needs-human" : "scripted success",
                DeferredSettle = !fail
            });
        }
    }

    /// <summary>
    /// Fails ONLY the guardrail set / worktree path a row configures; passes every other call. Handed the
    /// worktree path it gates, so it can fail a gate on a trial's <c>WorktreePath</c> alone (row 16) or on a
    /// named wave's own exit gate regardless of workspace (row 8).
    /// </summary>
    private sealed class SelectiveReVerifier(Func<string, IReadOnlyList<GuardrailDefinition>, bool> shouldFail) : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default)
        {
            bool fail = shouldFail(worktreePath, guardrails);
            ReVerifyResult result = fail
                ? new ReVerifyResult
                {
                    Passed = false,
                    FailedGuardrails = [.. guardrails.Select(g => new GuardrailResult { Name = g.Name, Passed = false, Reason = "boom" })]
                }
                : new ReVerifyResult { Passed = true };
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Wraps a <see cref="RecordingWorktreeProvider"/> (sealed) and delegates EVERY member it implements
    /// explicitly — an interface default does not fall through to the wrapped instance. Implements the three
    /// trial-delivery members itself (the recorder keeps their throwing defaults) plus
    /// <see cref="CurrentPlanBranchTip"/> (the recorder does not implement it either, so without this override
    /// the interface default would return an empty string).
    /// </summary>
    private sealed class DeliveryTestProvider(RunJournal journal, string planTip) : IWorktreeProvider
    {
        private readonly RecordingWorktreeProvider _inner = new();

        /// <summary>Per-wave trial builders a row configures; unconfigured waves get a plain successful trial.</summary>
        public Dictionary<string, Func<string, TrialDelivery>> TrialBuilders { get; } = new(StringComparer.Ordinal);

        /// <summary>Per-wave promotion outcome + detail a row configures; unconfigured waves fast-forward.</summary>
        public Dictionary<string, Func<TrialDelivery, (MergeOnSuccessResult Result, string? Detail)>> PromoteBuilders { get; } =
            new(StringComparer.Ordinal);

        public int CreateTrialDeliveryCalls;
        public List<string> CreateTrialDeliveryWaveOrder { get; } = [];
        public int PromoteTrialDeliveryCalls;
        public List<string> PromoteTrialDeliveryWaveOrder { get; } = [];

        /// <summary>What <c>run.json</c> held for the wave AT THE MOMENT of each call — never the in-memory document.</summary>
        public List<(string WaveDir, WaveJournalEntry? OnDisk)> RecordAtCreate { get; } = [];
        public List<(string WaveDir, WaveJournalEntry? OnDisk)> RecordAtPromote { get; } = [];

        public string? LastMergeOnSuccessDetail { get; private set; }

        public string CurrentPlanBranchTip(IntegrationHandle integ) => planTip;

        public TrialDelivery CreateTrialDelivery(IntegrationHandle integ, string waveDir, CancellationToken ct)
        {
            CreateTrialDeliveryCalls++;
            CreateTrialDeliveryWaveOrder.Add(waveDir);
            RecordAtCreate.Add((waveDir, WaveEntryOnDisk(journal, waveDir)));

            if (TrialBuilders.TryGetValue(waveDir, out Func<string, TrialDelivery>? build))
            {
                return build(waveDir);
            }

            // Recognizable, and neither the plan tip nor any marker sha — row 2 needs to tell this apart
            // from the plan-branch tip. UserTipWasAncestor = true in every default trial: task 15's
            // post-delivery refresh runs git in the INTEGRATION worktree only after a promotion fast-forwards
            // a trial whose UserTipWasAncestor is false, and RecordingWorktreeProvider's integration path
            // (`integ://<runId>/_integration`) is not a real directory — a false default would send every
            // ordinary row into the #150 fault path the moment task 15 lands. Row 16 opts out explicitly.
            return new TrialDelivery
            {
                WaveDir = waveDir,
                TrialRef = $"refs/guardrails/trial/{waveDir}",
                Commit = $"trial-{waveDir}",
                UserTip = planTip,
                UserTipWasAncestor = true,
                AlreadyDelivered = false
            };
        }

        public MergeOnSuccessResult PromoteTrialDelivery(IntegrationHandle integ, TrialDelivery trial, CancellationToken ct)
        {
            PromoteTrialDeliveryCalls++;
            PromoteTrialDeliveryWaveOrder.Add(trial.WaveDir);
            RecordAtPromote.Add((trial.WaveDir, WaveEntryOnDisk(journal, trial.WaveDir)));

            (MergeOnSuccessResult result, string? detail) = PromoteBuilders.TryGetValue(trial.WaveDir, out var build)
                ? build(trial)
                : (MergeOnSuccessResult.FastForwarded, null);
            LastMergeOnSuccessDetail = detail;
            return result;
        }

        public void DiscardTrialDelivery(IntegrationHandle integ, string waveDir) { }

        // Explicit delegation of every member RecordingWorktreeProvider implements (see class remarks).
        public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
            _inner.CreateIntegration(planName, runId, ct);
        public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
            _inner.CreateSegment(taskId, attempt, integ, ct);
        public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
            _inner.ReuseSegment(upstreamSegment, taskId, attempt);
        public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
            _inner.ForkFromTip(producerRecordedSha, taskId, attempt);
        public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct) =>
            _inner.Integrate(segment, integ, ct);
        public void Discard(WorktreeHandle handle) => _inner.Discard(handle);
        public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) =>
            _inner.PruneOrphans(liveTaskIds, integ);
        public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
            _inner.MergePlanBranchIntoUserBranch(integ, ct);
        public string CommitWaveMarker(IntegrationHandle integ, string waveDir, string waveHash, CancellationToken ct) =>
            _inner.CommitWaveMarker(integ, waveDir, waveHash, ct);
    }

    /// <summary>Records every <see cref="IRunObserver.WaveDelivered"/> call, together with what <c>run.json</c>
    /// held for that wave AT CALL TIME — never the in-memory document.</summary>
    private sealed class RecordingDeliveryObserver(RunJournal journal) : IRunObserver
    {
        public List<(WaveNode Wave, WaveDeliveredRecord Delivery, WaveJournalEntry? OnDisk)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void WaveDelivered(WaveNode wave, WaveDeliveredRecord delivery) =>
            Calls.Add((wave, delivery, WaveEntryOnDisk(journal, wave.Dir)));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. The running write precedes the trial build
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task TheDeliveryIsJournaledRunning_BeforeTheTrialIsBuilt()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t"); // the plan's FINAL wave never barrier-delivers (§3) — see class remarks.
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Equal(1, provider.CreateTrialDeliveryCalls);
        Assert.Equal(1, provider.PromoteTrialDeliveryCalls);

        foreach ((string waveDir, WaveJournalEntry? onDisk) in provider.RecordAtCreate.Concat(provider.RecordAtPromote))
        {
            Assert.Equal("wave-01-deliver", waveDir);
            WaveDeliveredRecord? delivered = onDisk?.Delivered;
            Assert.NotNull(delivered);
            Assert.Equal(WaveDeliveryStatus.Running, delivered!.Status);
            Assert.NotEqual(default, delivered.StartedAt);
            Assert.Equal(["wave-01-deliver"], delivered.Covers);
            Assert.Null(delivered.At);
            Assert.Null(delivered.Commit);
            Assert.Null(delivered.Outcome);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. A delivered wave's settled record
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ADeliveredWave_IsRecordedDeliveredWithThePromotedCommit()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);
        Assert.NotNull(delivered.At);
        Assert.Equal(DeliveryOutcome.FastForwarded, delivered.Outcome);
        Assert.Equal("trial-wave-01-deliver", delivered.Commit);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. Covers spans every wave since the last delivery
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ADeliveryCovers_EveryWaveSinceTheLastDelivery()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-scaffold", "01-a");
        b.Task("wave-02-first", "01-b");
        MakeDelivering(b, "wave-02-first");
        b.Task("wave-03-second", "01-c");
        MakeDelivering(b, "wave-03-second");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.Null(WaveEntryOnDisk(journal, "wave-01-scaffold")?.Delivered);
        Assert.Equal(["wave-01-scaffold", "wave-02-first"], WaveEntryOnDisk(journal, "wave-02-first")?.Delivered?.Covers);
        Assert.Equal(["wave-03-second"], WaveEntryOnDisk(journal, "wave-03-second")?.Delivered?.Covers);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. Covers after a resume starts after the last DELIVERED wave, not the last RUN wave
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task CoversAfterAResume_StillStartsAfterTheLastDeliveredWave()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-first", "01-a");
        MakeDelivering(b, "wave-01-first");
        b.Task("wave-02-mid", "01-b");
        b.Task("wave-03-second", "01-c");
        MakeDelivering(b, "wave-03-second");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal1 = RunJournal.LoadOrCreate(plan);
        var provider1 = new DeliveryTestProvider(journal1, PlanTip);
        var exec1 = new WaveFakeExecutor(failIds: ["wave-03-second/01-c"]);
        RunReport report1 = await NewScheduler(plan, exec1, journal1, provider1).RunAsync(plan, Ct);
        Assert.False(report1.AllSucceeded);

        RunJournal journal2 = RunJournal.LoadOrCreate(plan);
        var provider2 = new DeliveryTestProvider(journal2, PlanTip);
        var exec2 = new WaveFakeExecutor();
        RunReport report2 = await NewScheduler(plan, exec2, journal2, provider2).RunAsync(plan, Ct);

        Assert.True(report2.AllSucceeded, string.Join("; ", report2.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Equal(["wave-02-mid", "wave-03-second"], WaveEntryOnDisk(journal2, "wave-03-second")?.Delivered?.Covers);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. WaveDelivered raises only AFTER the record settles delivered, never on a refusal/suppression
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task TheSchedulerRaisesWaveDelivered_AfterTheRecordIsPersisted()
    {
        // Run A: wave-01 delivers cleanly; wave-02 self-suppresses via its own planted decision.
        using var bA = new WavePlanBuilder();
        bA.Task("wave-01-deliver", "01-a");
        MakeDelivering(bA, "wave-01-deliver");
        bA.Task("wave-02-deliver", "01-b");
        MakeDelivering(bA, "wave-02-deliver");
        bA.Task("wave-99-tail", "01-t");
        PlanDefinition planA = Load(bA);

        RunJournal journalA = RunJournal.LoadOrCreate(planA);
        var providerA = new DeliveryTestProvider(journalA, PlanTip);
        var execA = new WaveFakeExecutor(journalA, plantDecisionAt: ("wave-02-deliver/01-b", "wave-02-deliver"));
        var observerA = new RecordingDeliveryObserver(journalA);

        RunReport reportA = await NewScheduler(planA, execA, journalA, providerA, observerA).RunAsync(planA, Ct);
        Assert.True(reportA.AllSucceeded, string.Join("; ", reportA.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.Single(observerA.Calls);
        (WaveNode wave, WaveDeliveredRecord delivery, WaveJournalEntry? onDisk) = observerA.Calls[0];
        Assert.Equal("wave-01-deliver", wave.Dir);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivery.Status);
        Assert.NotNull(onDisk?.Delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, onDisk!.Delivered!.Status);
        Assert.Equal(delivery.Commit, onDisk.Delivered.Commit);
        Assert.Equal(delivery.At, onDisk.Delivered.At);

        // Run B: one delivering wave whose promotion reports BranchMoved — nothing is raised.
        using var bB = new WavePlanBuilder();
        bB.Task("wave-01-deliver", "01-a");
        MakeDelivering(bB, "wave-01-deliver");
        bB.Task("wave-99-tail", "01-t");
        PlanDefinition planB = Load(bB);

        RunJournal journalB = RunJournal.LoadOrCreate(planB);
        var providerB = new DeliveryTestProvider(journalB, PlanTip);
        providerB.PromoteBuilders["wave-01-deliver"] = _ => (MergeOnSuccessResult.BranchMoved, "branch moved mid-run");
        var execB = new WaveFakeExecutor();
        var observerB = new RecordingDeliveryObserver(journalB);

        await NewScheduler(planB, execB, journalB, providerB, observerB).RunAsync(planB, Ct);

        Assert.Empty(observerB.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6. A refused delivery records its outcome and raises nothing
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ARefusedDelivery_IsRecordedRefusedWithItsOutcome_AndRaisesNoEvent()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.PromoteBuilders["wave-01-deliver"] = _ => (MergeOnSuccessResult.BranchMoved, "the user moved branches mid-run");
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Refused, delivered!.Status);
        Assert.Equal(DeliveryOutcome.BranchMoved, delivered.Outcome);
        Assert.Contains("the user moved branches mid-run", delivered.Detail ?? "");
        Assert.NotNull(delivered.At);
        Assert.Null(delivered.Commit);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 7. A suppressed delivery never touches the trial primitive
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ASuppressedDelivery_IsRecordedSuppressed_AndNeverMovesTheUsersBranch()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-decide", "01-a");
        b.Task("wave-02-deliver", "01-b");
        MakeDelivering(b, "wave-02-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor(journal, plantDecisionAt: ("wave-01-decide/01-a", "wave-01-decide"));

        await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-02-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Suppressed, delivered!.Status);
        Assert.Contains(DecisionTokens.ProceededBestGuess, delivered.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("wave-01-decide/01-a", delivered.Detail ?? "", StringComparison.Ordinal);
        Assert.Null(delivered.Commit);

        Assert.Equal(0, provider.CreateTrialDeliveryCalls);
        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 8. A halted run's report still carries earlier wave deliveries
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AHaltedRunsReport_StillCarriesEarlierWaveDeliveries()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-plain", "01-a");
        b.Task("wave-02-deliver", "01-b");
        MakeDelivering(b, "wave-02-deliver");
        b.Task("wave-03-fail", "01-c");
        b.WaveGuardrail("wave-03-fail", "01-always-fail.sh", "exit 1\n");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();
        var reVerifier = new SelectiveReVerifier((_, gs) => gs.Any(g => g.Name.Contains("always-fail", StringComparison.Ordinal)));

        RunReport report = await NewScheduler(plan, exec, journal, provider, reVerifier: reVerifier).RunAsync(plan, Ct);

        Assert.NotNull(report.WaveHalt);

        Assert.True(report.WaveDeliveries.TryGetValue("wave-02-deliver", out WaveDeliveredRecord? recorded));
        Assert.Equal(WaveDeliveryStatus.Delivered, recorded!.Status);
        Assert.False(report.WaveDeliveries.ContainsKey("wave-01-plain"));
        Assert.False(report.WaveDeliveries.ContainsKey("wave-03-fail"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 9. EXEMPT — a plan marking no wave records no delivery and reports none
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task APlanMarkingNoWave_RecordsNoDeliveryAndReportsNone()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-a", "01-a");
        b.Task("wave-02-b", "01-b");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        RunReport report = await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        JournalDocument doc = JournalReader.Read(journal.JournalPath);
        Assert.All(doc.Waves?.Values ?? [], w => Assert.Null(w.Delivered));
        Assert.Empty(report.WaveDeliveries);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 10. A trial that cannot be built is recorded refused, never promoted
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ATrialThatCannotBeBuilt_IsRecordedRefused_AndIsNeverPromoted()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            UserTip = PlanTip,
            UserTipWasAncestor = true,
            Refusal = MergeOnSuccessResult.HookRejected,
            RefusalDetail = "the user's pre-commit hook rejected the trial merge"
        };
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Refused, delivered!.Status);
        Assert.Equal(DeliveryOutcome.HookRejected, delivered.Outcome);
        Assert.Contains("the user's pre-commit hook rejected the trial merge", delivered.Detail ?? "");
        Assert.NotNull(delivered.At);
        Assert.Null(delivered.Commit);

        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 11. A resumed crash mid-delivery settles the seeded running record as delivered, once
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AResumeAfterACrashMidDelivery_RecordsAnAlreadyDeliveredTrialAsDelivered()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal seed = RunJournal.LoadOrCreate(plan);
        seed.RecordWaveDelivery("wave-01-deliver", new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Running,
            StartedAt = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero),
            Covers = ["wave-01-deliver"]
        });

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            UserTip = PlanTip,
            Commit = PlanTip,
            UserTipWasAncestor = true,
            AlreadyDelivered = true
        };
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);
        Assert.Equal(DeliveryOutcome.FastForwarded, delivered.Outcome);
        Assert.Equal(PlanTip, delivered.Commit);

        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
        Assert.Single(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 12. EXEMPT — a resume over an already-delivered record keeps it and raises nothing
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AResumeOverADeliveredRecord_KeepsItAndRaisesNoEvent()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        var seededAt = new DateTimeOffset(2026, 9, 14, 6, 30, 0, TimeSpan.Zero);
        var seededRecord = new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = new DateTimeOffset(2026, 9, 14, 6, 29, 0, TimeSpan.Zero),
            At = seededAt,
            Commit = "already-shipped-sha",
            Outcome = DeliveryOutcome.FastForwarded,
            Covers = ["wave-01-deliver"]
        };
        RunJournal seed = RunJournal.LoadOrCreate(plan);
        seed.RecordWaveDelivery("wave-01-deliver", seededRecord);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            UserTip = PlanTip,
            Commit = PlanTip,
            UserTipWasAncestor = true,
            AlreadyDelivered = true
        };
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);
        Assert.Equal(seededAt, delivered.At);
        Assert.Equal("already-shipped-sha", delivered.Commit);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 13. A forced delivery names the decision it overrode
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AForcedDelivery_NamesTheDecisionItOverrodeInItsDetail()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-decide", "01-a");
        b.Task("wave-02-deliver", "01-b");
        MakeDelivering(b, "wave-02-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition loaded = Load(b);
        PlanDefinition plan = loaded with { Config = loaded.Config with { MergeOnSuccessForcedByOperator = true } };

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor(journal, plantDecisionAt: ("wave-01-decide/01-a", "wave-01-decide"));

        RunReport report = await NewScheduler(plan, exec, journal, provider).RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-02-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);
        Assert.Equal("trial-wave-02-deliver", delivered.Commit);
        Assert.Contains(DecisionTokens.ProceededBestGuess, delivered.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("wave-01-decide/01-a", delivered.Detail ?? "", StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 14. EXEMPT — a serial waved run never delivers at a barrier
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ASerialWavedRun_NeverDeliversAtABarrier()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-02-final", "01-b");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        RunReport report = await NewScheduler(plan, exec, journal, provider: null, observer, parallelism: 1)
            .RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        JournalDocument doc = JournalReader.Read(journal.JournalPath);
        Assert.All(doc.Waves?.Values ?? [], w => Assert.Null(w.Delivered));
        Assert.Empty(report.WaveDeliveries);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 15. EXEMPT — mergeOnSuccess off writes no record
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ABarrierDelivery_WithMergeOnSuccessOff_WritesNoRecord()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition loaded = Load(b);
        PlanDefinition plan = loaded with { Config = loaded.Config with { MergeOnSuccess = false } };

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        RunReport report = await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        JournalDocument doc = JournalReader.Read(journal.JournalPath);
        Assert.All(doc.Waves?.Values ?? [], w => Assert.Null(w.Delivered));
        Assert.Equal(0, provider.CreateTrialDeliveryCalls);
        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
        Assert.Empty(report.WaveDeliveries);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 16. A failed trial-tree gate is recorded refused, never promoted
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AFailedTrialTreeGate_IsRecordedRefused_AndIsNeverPromoted()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        const string trialWorktree = "trial-worktree://wave-01-deliver";

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            Commit = $"trial-{waveDir}",
            UserTip = TrialUserTip,
            // The one row that reports UserTipWasAncestor = false: the user's branch moved, so the trial
            // needed a real merge commit and task 08 gates it in that merge's own worktree.
            UserTipWasAncestor = false,
            WorktreePath = trialWorktree
        };
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);
        var reVerifier = new SelectiveReVerifier((ws, _) => ws == trialWorktree);

        WaveNode wave1 = plan.Waves.Single(w => w.Dir == "wave-01-deliver");
        string checkName = wave1.Guardrails[0].Name;

        await NewScheduler(plan, exec, journal, provider, observer, reVerifier).RunAsync(plan, Ct);

        WaveDeliveredRecord? delivered = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Refused, delivered!.Status);
        Assert.Equal(DeliveryOutcome.TrialGateFailed, delivered.Outcome);
        Assert.NotNull(delivered.At);
        Assert.Null(delivered.Commit);

        string planTip10 = PlanTip[..10];
        string userTip10 = TrialUserTip[..10];
        Assert.Contains(checkName, delivered.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains(TrialUserTip, delivered.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains($"git log {planTip10}..{userTip10}", delivered.Detail ?? "", StringComparison.Ordinal);

        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 17. EXEMPT — a diverged task definition blocks the barrier delivery
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ADivergedTaskDefinition_BlocksTheBarrierDelivery()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        var exec = new WaveFakeExecutor(editOwnDefinitionAt: "wave-01-deliver/01-a");
        var observer = new RecordingDeliveryObserver(journal);

        RunReport report = await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        // Positive control: the scenario really happened.
        Assert.True(report.HasExecutedDefinitionDivergence);

        Assert.Equal(0, provider.CreateTrialDeliveryCalls);
        Assert.Equal(0, provider.PromoteTrialDeliveryCalls);
        JournalDocument doc = JournalReader.Read(journal.JournalPath);
        Assert.All(doc.Waves?.Values ?? [], w => Assert.Null(w.Delivered));
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 18. A later barrier after a hook rejection is recorded suppressed, naming it
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task ALaterBarrierAfterAHookRejection_IsRecordedSuppressedNamingIt()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-02-deliver", "01-b");
        MakeDelivering(b, "wave-02-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var provider = new DeliveryTestProvider(journal, PlanTip);
        provider.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            UserTip = PlanTip,
            UserTipWasAncestor = true,
            Refusal = MergeOnSuccessResult.HookRejected,
            RefusalDetail = "hook rejected wave-01's trial merge"
        };
        var exec = new WaveFakeExecutor();
        var observer = new RecordingDeliveryObserver(journal);

        await NewScheduler(plan, exec, journal, provider, observer).RunAsync(plan, Ct);

        WaveDeliveredRecord? wave1 = WaveEntryOnDisk(journal, "wave-01-deliver")?.Delivered;
        Assert.NotNull(wave1);
        Assert.Equal(WaveDeliveryStatus.Refused, wave1!.Status);
        Assert.Equal(DeliveryOutcome.HookRejected, wave1.Outcome);

        WaveDeliveredRecord? wave2 = WaveEntryOnDisk(journal, "wave-02-deliver")?.Delivered;
        Assert.NotNull(wave2);
        Assert.Equal(WaveDeliveryStatus.Suppressed, wave2!.Status);
        Assert.Null(wave2.Commit);
        Assert.Contains("wave-01-deliver", wave2.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("hook-rejected", wave2.Detail ?? "", StringComparison.Ordinal);

        Assert.Equal(1, provider.CreateTrialDeliveryCalls);
        Assert.Equal(["wave-01-deliver"], provider.CreateTrialDeliveryWaveOrder);
        Assert.Empty(observer.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 19. A resume after a hook rejection still holds later deliveries
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task AResumeAfterAHookRejection_StillHoldsLaterDeliveries()
    {
        using var b = new WavePlanBuilder();
        b.Task("wave-01-deliver", "01-a");
        MakeDelivering(b, "wave-01-deliver");
        b.Task("wave-02-deliver", "01-b");
        MakeDelivering(b, "wave-02-deliver");
        b.Task("wave-99-tail", "01-t");
        PlanDefinition plan = Load(b);

        RunJournal journal1 = RunJournal.LoadOrCreate(plan);
        var provider1 = new DeliveryTestProvider(journal1, PlanTip);
        provider1.TrialBuilders["wave-01-deliver"] = waveDir => new TrialDelivery
        {
            WaveDir = waveDir,
            TrialRef = $"refs/guardrails/trial/{waveDir}",
            UserTip = PlanTip,
            UserTipWasAncestor = true,
            Refusal = MergeOnSuccessResult.HookRejected,
            RefusalDetail = "hook rejected wave-01's trial merge"
        };
        var exec1 = new WaveFakeExecutor(failIds: ["wave-02-deliver/01-b"]);
        RunReport report1 = await NewScheduler(plan, exec1, journal1, provider1).RunAsync(plan, Ct);
        Assert.False(report1.AllSucceeded);

        // Seed the record the first process would have journaled before it stopped (task 29's write path
        // does not exist on this tree, so the test fabricates its effect to isolate the resume behaviour).
        var startedAt = new DateTimeOffset(2026, 9, 14, 5, 0, 0, TimeSpan.Zero);
        var settledAt = new DateTimeOffset(2026, 9, 14, 5, 1, 0, TimeSpan.Zero);
        journal1.RecordWaveDelivery("wave-01-deliver", new WaveDeliveredRecord
        {
            Status = WaveDeliveryStatus.Refused,
            StartedAt = startedAt,
            At = settledAt,
            Outcome = DeliveryOutcome.HookRejected,
            Detail = "hook rejected wave-01's trial merge",
            Covers = ["wave-01-deliver"]
        });

        RunJournal journal2 = RunJournal.LoadOrCreate(plan);
        var provider2 = new DeliveryTestProvider(journal2, PlanTip);
        var exec2 = new WaveFakeExecutor();
        var observer2 = new RecordingDeliveryObserver(journal2);

        RunReport report2 = await NewScheduler(plan, exec2, journal2, provider2, observer2).RunAsync(plan, Ct);
        Assert.True(report2.AllSucceeded, string.Join("; ", report2.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.Equal(0, provider2.CreateTrialDeliveryCalls);

        WaveDeliveredRecord? wave2 = WaveEntryOnDisk(journal2, "wave-02-deliver")?.Delivered;
        Assert.NotNull(wave2);
        Assert.Equal(WaveDeliveryStatus.Suppressed, wave2!.Status);
        Assert.Contains("wave-01-deliver", wave2.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("hook-rejected", wave2.Detail ?? "", StringComparison.Ordinal);

        Assert.Empty(observer2.Calls);
    }
}
