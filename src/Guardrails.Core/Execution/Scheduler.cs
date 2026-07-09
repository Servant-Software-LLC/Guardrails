using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M4 DAG scheduler. Kahn-style readiness (a task becomes ready when every
/// dependency is green) feeding an unbounded <see cref="Channel{T}"/> consumed by
/// <c>maxParallelism</c> workers. A task that ends <c>needs-human</c> (or otherwise
/// non-green) blocks its TRANSITIVE dependents immediately while independent branches
/// keep running — every completed task is durable progress in the journal, and one run
/// surfaces every needs-human halt instead of one per run.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly ISchedulerJournal _journal;
    private readonly IWorktreeProvider? _worktreeProvider;
    private readonly IRunObserver _observer;
    private readonly int _maxParallelism;
    private readonly IReVerifier? _reVerifier;

    private readonly object _gate = new();

    // Serialize-merges lock (plan 08 §3): one integration-settle at a time so that
    // non-FF union re-verify and B1 rollback are atomic w.r.t. other settling workers.
    private readonly SemaphoreSlim _integrationLock = new(1, 1);

    // First unexpected (non-cancellation) executor fault wins; surfaced after WhenAll so the
    // run terminates deterministically with a harness error instead of hanging (see WorkerLoopAsync).
    private Exception? _fault;

    private readonly IAiMergeWorker? _aiMergeWorker;

    // Part C (#274, SSOT §7.2): the operator-authorized safe-rewind plan captured by the CLI's pre-DAG
    // probe (S + reset target + the plan-branch tip the operator saw). Non-null ONLY on a Prompt-policy
    // run the CLI already confirmed OUTSIDE the live region with a `y`. Core never prompts itself, so
    // without this a Prompt-policy safe drift HALTS. The Scheduler executes the CAPTURED plan (verifying
    // it still matches + a tip compare-and-swap), never a possibly-diverged recompute. autonomyPolicy=auto
    // auto-resolves on its own fresh decision; halt/unconfirmed-prompt halt.
    private readonly DriftAuthorization? _driftAuthorization;

    // #254 M2b (SSOT §14.6): the wave dirs the CLI already confirmed rewinding for a Prompt-policy run
    // (an operator `y` OUTSIDE the live region). A wave-scoped rewind is ALWAYS a safe trailing suffix
    // (§14.8), so — unlike the task-level DriftAuthorization — this needs only the set of authorized wave
    // dirs. Empty (the default) for auto (resolves on its own) and halt / unconfirmed prompt (halts).
    private readonly IReadOnlySet<string> _waveDriftAuthorized;

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        ISchedulerJournal journal,
        IWorktreeProvider? worktreeProvider = null,
        IRunObserver? observer = null,
        int? maxParallelism = null,
        IReVerifier? reVerifier = null,
        IAiMergeWorker? aiMergeWorker = null,
        DriftAuthorization? driftAuthorization = null,
        IReadOnlySet<string>? waveDriftAuthorized = null)
    {
        _plan = plan;
        _executor = executor;
        _journal = journal;
        _worktreeProvider = worktreeProvider;
        _observer = observer ?? IRunObserver.Null;
        _reVerifier = reVerifier;
        _aiMergeWorker = aiMergeWorker;
        _driftAuthorization = driftAuthorization;
        _waveDriftAuthorized = waveDriftAuthorized ?? new HashSet<string>(StringComparer.Ordinal);

        int requested = Math.Max(1, maxParallelism ?? plan.Config.MaxParallelism);

        // F7 HARD GUARD: worktree mode (parallelism > 1) requires a worktree provider for
        // per-task isolation. With no provider, parallel workers would share the single
        // workspace and race undetectably (the rejected shared-workspace corruption class).
        // CLAMP to 1 (serial shared-workspace, the pre-plan-08 model) rather than running
        // an unsafe parallel run — and tell the observer so the demotion is not silent.
        if (requested > 1 && _worktreeProvider is null)
        {
            _observer.ParallelismClampedNoProvider(requested);
            requested = 1;
        }

        _maxParallelism = requested;
    }

    /// <summary>
    /// Run the plan to quiescence: every task green, blocked, or needs-human — or the
    /// token cancelled (in-flight attempts are journaled back to pending by the
    /// executor; unstarted tasks are reported <see cref="TaskOutcome.Cancelled"/>).
    /// </summary>
    public async Task<RunReport> RunAsync(PlanDefinition plan, CancellationToken cancellationToken = default)
    {
        var fullGraph = new DependencyGraph(plan.Tasks);
        if (fullGraph.FindCycle() is { } cycle)
        {
            // Validation (GR2007) catches this before a run; this guard keeps the
            // scheduler safe when embedded directly.
            throw new InvalidOperationException($"Dependency cycle: {string.Join(" -> ", cycle)}");
        }

        // Shared, CONTINUOUS run state across every wave (SSOT §14): ONE settled map (all waves' task
        // results coexist in the final report), ONE directoryOwner map for the end-of-run sweep, ONE
        // runId, ONE integration handle / plan branch, and ONE journal (_journal). A WAVED run drives N
        // per-wave DAG drains against THIS shared state — it never forks a fresh integration worktree /
        // runId / journal per wave (the M2a continuity blocker; SSOT §14.4).
        var settled = new Dictionary<string, TaskResult>(StringComparer.Ordinal);
        var directoryOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        // Create the ONE integration handle for the whole run (worktree mode only).
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle? integ;
        try
        {
            integ = _worktreeProvider?.CreateIntegration(
                planName: Path.GetFileName(plan.PlanDirectory),
                runId: runId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Issue #150/#160 — CreateIntegration runs BEFORE the worker loop's fault capture, so a
            // setup fault (e.g. a plan folder with no usable name component → the #160 guard's clear
            // diagnostic, or git unavailable) would otherwise escape unhandled as a raw stack trace.
            // Surface it through the same honest-halt ABORTED report the CLI renders cleanly.
            return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = BuildAbort(ex) };
        }

        // Whole-plan resume reconcile — ONCE, before any wave. Prune this run's stale segment refs,
        // replay a surviving Part C rewind-intent marker (crash-atomicity), then read the plan branch's
        // Guardrails-Task: trailers (the durable cross-run resume truth). Shared by every wave's drain.
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords =
            new Dictionary<string, PlanBranchTaskRecord>(StringComparer.Ordinal);
        bool trailerTracking = _worktreeProvider?.TracksPlanBranchTrailers == true && integ is not null;
        if (_worktreeProvider is { } wp && integ is { } activeInteg)
        {
            wp.PruneStaleRunBranches(runId, activeInteg);

            // Part C crash-atomicity (#274, SSOT §7.2): replay a rewind-intent marker left by a run killed
            // BETWEEN a plan-branch rewind and its journal-resets. Runs BEFORE the reconcile read below so
            // the replayed statuses are seen. Idempotent.
            ReplayRewindIntentIfPresent();

            planBranchRecords = wp.ReconcileFromPlanBranch(activeInteg);
        }

        // Dispatch: a WAVED plan runs its waves in strict order behind hard barriers (SSOT §14.4); a
        // FLAT plan is one drain over all tasks (the pre-M2b behaviour, unchanged).
        return plan.IsWaved
            ? await RunWavedAsync(plan, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken).ConfigureAwait(false)
            : await RunFlatAsync(plan, fullGraph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A FLAT plan: ONE drain over every task, then the legacy terminal integration gate (§3.3, when no
    /// plan-level <c>&lt;plan&gt;/guardrails/</c> folder supersedes it) + delivery + cleanup. Byte-for-byte
    /// the pre-M2b behaviour, now expressed on top of the shared <see cref="DrainAsync"/>.
    /// </summary>
    private async Task<RunReport> RunFlatAsync(
        PlanDefinition plan, DependencyGraph graph, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        DrainOutcome drain = await DrainAsync(
            plan, plan.Tasks, graph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken)
            .ConfigureAwait(false);

        if (drain.ReadAbort is { } readAbort)
        {
            // Pre-schedule read/git abort during the drift check — nothing scheduled, no sweep.
            return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = readAbort };
        }

        if (drain.Drift is { } drift)
        {
            // Pre-schedule definition-drift halt — nothing scheduled, no sweep.
            return BuildReport(plan, settled, cancelled: false) with { DefinitionDrift = drift };
        }

        if (drain.Faulted)
        {
            RunReport aborted = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = BuildAbort(_fault!) };
            if (!cancellationToken.IsCancellationRequested)
            {
                EndOfRunSweep(directoryOwner, settled, integ);
            }

            return aborted;
        }

        RunReport report = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
            with { Decision = drain.Decision };

        // Legacy terminal whole-repo integration gate (§3.3/§4a) — FLAT plans only, and only when the plan
        // declares no <plan>/guardrails/ folder (the CLI PlanGuardrailPhase supersedes it). A WAVED plan's
        // terminal soundness boundary is its LAST wave's exit gate (§14.3), so this never runs there.
        if (report.AllSucceeded && _reVerifier != null && integ != null && plan.PlanGuardrails.Count == 0)
        {
            IReadOnlyList<GuardrailDefinition> integrationSet =
                GuardrailScopeFilter.IntegrationSet(plan.Tasks.SelectMany(t => t.Guardrails));

            if (integrationSet.Count > 0)
            {
                ReVerifyResult gate = await _reVerifier
                    .ReVerifyAsync(integ.IntegrationWorktreePath, integrationSet, cancellationToken)
                    .ConfigureAwait(false);

                if (!gate.Passed)
                {
                    report = WithTerminalGateFailure(plan, report, gate);
                }
            }
        }

        return Finalize(plan, report, integ, directoryOwner, settled, cancellationToken);
    }

    /// <summary>
    /// A WAVED plan (SSOT §14.4): run each wave in strict order behind a HARD BARRIER — wave entry
    /// preflight, then drain the wave's DAG on the CONTINUOUS plan branch, then (full drain) the wave exit
    /// gate, then the <c>Guardrails-Wave:</c> marker commit + journal-complete. A completed wave is skipped
    /// on resume (with a wave-drift check, §14.6); an unauthored next wave honest-halts for JIT breakdown
    /// (§14.4); any needs-human/blocked/failed inside a wave, or a failed gate, HALTS the whole run — later
    /// waves never start.
    /// </summary>
    private async Task<RunReport> RunWavedAsync(
        PlanDefinition plan, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WaveNode> waves = plan.Waves; // strict total order (loader sorts by numeric prefix)

        // Durable wave-completion anchors from the plan branch (Guardrails-Wave: markers) — the backstop
        // when run.json is lost, and the source of predecessor-wave rewind targets (SSOT §14.5).
        IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers =
            _worktreeProvider is { } wpm && integ is { } integM
                ? wpm.ReconcileWavesFromPlanBranch(integM)
                : new Dictionary<string, PlanBranchWaveRecord>(StringComparer.Ordinal);

        DecisionEntry? lastDecision = null;

        for (int i = 0; i < waves.Count; i++)
        {
            WaveNode wave = waves[i];

            // 1. Completion + wave-drift (SSOT §14.5/§14.6).
            (bool complete, string? recordedHash) = EvaluateWaveCompletion(wave, planBranchRecords, waveMarkers, trailerTracking);
            if (complete)
            {
                string currentHash = Journal.WaveDefinitionHash.Compute(wave);
                if (recordedHash is { } rh && !string.Equals(rh, currentHash, StringComparison.Ordinal))
                {
                    // WAVE DRIFT: a COMPLETED wave's definition changed. auto (or a prompt the CLI already
                    // confirmed) rewinds + re-runs; halt / unconfirmed-prompt HALTS (SSOT §14.6).
                    bool authorized = _plan.Config.AutonomyPolicy == AutonomyPolicy.Auto
                        || (_plan.Config.AutonomyPolicy == AutonomyPolicy.Prompt && _waveDriftAuthorized.Contains(wave.Dir));
                    if (!authorized)
                    {
                        return BuildReport(plan, settled, cancelled: false)
                            with { WaveHalt = BuildWaveDriftHalt(waves, i, wave, rh, currentHash, unsafeRefusal: null) };
                    }

                    // The rewind is validated by the marker-aware SafeSuffixEvaluator + a tip CAS (BLOCKER /
                    // WEAK-4): a human hand-fix (trailer-less NON-marker commit) in the range, or a concurrent
                    // same-plan session that moved the tip, REFUSES the rewind → HALT rather than discard it.
                    WaveRewindResult resolved = ResolveWaveDrift(plan, waves, i, wave, integ, rh, currentHash,
                        ref planBranchRecords, ref waveMarkers);
                    if (resolved.Decision is null)
                    {
                        return BuildReport(plan, settled, cancelled: false)
                            with { WaveHalt = BuildWaveDriftHalt(waves, i, wave, rh, currentHash, unsafeRefusal: resolved.Refusal) };
                    }

                    lastDecision = resolved.Decision;
                    _journal.RecordDecision(lastDecision);
                    _observer.DecisionRecorded(lastDecision);
                    // fall through — this wave is no longer complete; run it.
                }
                else
                {
                    foreach (TaskNode t in wave.Tasks)
                    {
                        var s = new TaskResult
                        {
                            TaskId = t.Id,
                            Outcome = TaskOutcome.Skipped,
                            Summary = "already succeeded (resumed) — skipped"
                        };
                        settled[t.Id] = s;
                        _observer.TaskFinished(s);
                    }

                    _observer.WaveFinished(wave, Journal.WaveStatus.Completed, skipped: true);
                    continue;
                }
            }

            // 2. Between-wave JIT checkpoint (SSOT §14.4): an unauthored/empty wave honest-halts (exit 2).
            if (wave.Tasks.Count == 0)
            {
                return BuildReport(plan, settled, cancelled: false)
                    with { WaveHalt = BuildUnauthoredWaveHalt(wave, integ) };
            }

            _observer.WaveStarting(wave, i + 1, waves.Count);

            // 3. Wave ENTRY preflight (skip-once-per-hash; SSOT §14.3/§14.6).
            (bool entryPassed, IReadOnlyList<GuardrailResult> entryFailed) =
                await RunWaveEntryGateAsync(plan, wave, integ, cancellationToken).ConfigureAwait(false);
            if (!entryPassed)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                RunReport entryHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { WaveHalt = BuildGateHalt(wave, WaveHaltKind.EntryGateFailed, entryFailed) };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return entryHalt;
            }

            // 4. Drain the wave's DAG on the CONTINUOUS plan branch (shared integ / journal / settled).
            var waveGraph = new DependencyGraph(wave.Tasks);
            DrainOutcome drain = await DrainAsync(
                plan, wave.Tasks, waveGraph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken)
                .ConfigureAwait(false);

            if (drain.ReadAbort is { } readAbort)
            {
                return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested) with { Abort = readAbort };
            }

            if (drain.Drift is { } taskDrift)
            {
                return BuildReport(plan, settled, cancelled: false) with { DefinitionDrift = taskDrift };
            }

            if (drain.Faulted)
            {
                RunReport aborted = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { Abort = BuildAbort(_fault!) };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return aborted;
            }

            if (drain.Decision is not null)
            {
                lastDecision = drain.Decision;
            }

            // 5. HARD BARRIER (SSOT §14.4): the wave must fully drain green. Any needs-human/blocked/failed
            // HALTS the whole run here — later waves never start.
            if (!drain.AllGreen)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                RunReport barrierHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { Decision = lastDecision };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return barrierHalt;
            }

            // 6. Wave EXIT / terminal gate (SSOT §14.3): on the merged HEAD-so-far.
            (bool exitPassed, IReadOnlyList<GuardrailResult> exitFailed) =
                await RunWaveExitGateAsync(plan, wave, integ, cancellationToken).ConfigureAwait(false);
            if (!exitPassed)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                RunReport exitHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { WaveHalt = BuildGateHalt(wave, WaveHaltKind.ExitGateFailed, exitFailed) };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return exitHalt;
            }

            // 7. Wave-completion marker commit (decision E) + journal the wave complete (SSOT §14.5).
            string waveHash = Journal.WaveDefinitionHash.Compute(wave);
            string? markerSha = _worktreeProvider is { } wpc && integ is { } integC
                ? wpc.CommitWaveMarker(integC, wave.Dir, waveHash, cancellationToken)
                : null;
            _journal.RecordWaveCompleted(wave.Dir, waveHash, markerSha);
            if (markerSha is { Length: > 0 })
            {
                waveMarkers = WithWaveMarker(waveMarkers, wave.Dir, new PlanBranchWaveRecord(markerSha, waveHash));
            }

            _observer.WaveFinished(wave, Journal.WaveStatus.Completed, skipped: false);
        }

        // Every wave complete → deliver + sweep. No legacy terminal integ gate: the LAST wave's exit gate
        // is the whole-plan terminal soundness boundary (§14.3); a plan-root <plan>/guardrails/ is
        // optional-additive and run by the CLI PlanGuardrailPhase after this returns.
        RunReport report = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
            with { Decision = lastDecision };
        return Finalize(plan, report, integ, directoryOwner, settled, cancellationToken);
    }

    /// <summary>
    /// Drain ONE set of tasks (a whole flat plan, or one wave's DAG) against the shared integration
    /// handle + journal + <paramref name="settled"/>/<paramref name="directoryOwner"/> accumulators: the
    /// resume pre-pass + task-level definition-drift check (§7.2) for this subset, then the Channel
    /// scheduler's worker loop (workers, maxParallelism, retry, needs-human/blocked, B1 settle — all
    /// unchanged). Appends every result to <paramref name="settled"/>. Returns a <see cref="DrainOutcome"/>
    /// so the caller decides: a drift/abort halt, an infra fault, or whether the subset fully drained green.
    /// </summary>
    private async Task<DrainOutcome> DrainAsync(
        PlanDefinition plan, IReadOnlyList<TaskNode> tasksToRun, DependencyGraph graph, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        var byId = tasksToRun.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var pendingDeps = new Dictionary<string, int>(StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<TaskEnvelope>();

        HashSet<string> preSettledGreen;
        List<DefinitionDriftReporter.DriftInput> drifted;
        DecisionEntry? driftDecision = null;
        try
        {
            (preSettledGreen, drifted) = DetectDefinitionDrift(tasksToRun, planBranchRecords, trailerTracking);

            if (drifted.Count > 0)
            {
                DriftGateResult gate = TryResolveDrift(plan, graph, drifted, integ);
                if (gate.Decision is null)
                {
                    return DrainOutcome.DriftHalt(
                        DefinitionDriftReporter.Build(plan, graph, drifted, _worktreeProvider)
                            with { SafeToAutoResolve = gate.SafeToAutoResolve, RewindRefusal = gate.Refusal, RewindBlockingTask = gate.BlockingTask });
                }

                driftDecision = gate.Decision;
                _journal.RecordDecision(driftDecision);
                _observer.DecisionRecorded(driftDecision);

                IReadOnlyDictionary<string, PlanBranchTaskRecord> refreshed = planBranchRecords;
                if (_worktreeProvider is { } wpAfter && integ is { } integAfter)
                {
                    refreshed = wpAfter.ReconcileFromPlanBranch(integAfter);
                }

                (preSettledGreen, drifted) = DetectDefinitionDrift(tasksToRun, refreshed, trailerTracking);
                if (drifted.Count > 0)
                {
                    return DrainOutcome.DriftHalt(DefinitionDriftReporter.Build(plan, graph, drifted, _worktreeProvider));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DrainOutcome.Abort(BuildDefinitionReadAbort(ex));
        }
        catch (InvalidOperationException ex)
        {
            return DrainOutcome.Abort(BuildAbort(ex));
        }

        // Emit resume skips for the pre-settled-green candidates (in subset order).
        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id))
            {
                continue;
            }

            var skipped = new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Skipped,
                Summary = "already succeeded (resumed) — skipped"
            };
            settled[task.Id] = skipped;
            _observer.TaskFinished(skipped);
        }

        int remaining = 0;
        foreach (TaskNode task in tasksToRun)
        {
            if (preSettledGreen.Contains(task.Id))
            {
                continue;
            }

            remaining++;
            pendingDeps[task.Id] = task.DependsOn.Count(d => !preSettledGreen.Contains(d));
        }

        if (remaining == 0)
        {
            return new DrainOutcome { AllGreen = AllGreenFor(tasksToRun, settled), Decision = driftDecision };
        }

        var handles = new Dictionary<string, WorktreeHandle>(StringComparer.Ordinal);
        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                WorktreeHandle handle = _worktreeProvider != null && integ != null
                    ? _worktreeProvider.CreateSegment(task.Id, attempt: 1, integ, cancellationToken)
                    : new WorktreeHandle();
                handles[task.Id] = handle;
                if (!string.IsNullOrEmpty(handle.WorktreePath))
                {
                    directoryOwner[handle.WorktreePath] = task.Id;
                }
            }
        }

        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                channel.Writer.TryWrite(new TaskEnvelope(task, handles[task.Id]));
            }
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new RunContext(graph, byId, settled, pendingDeps, channel, remaining, handles, directoryOwner, integ);
        int workerCount = Math.Min(_maxParallelism, remaining);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(context, runCts), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (_fault is not null)
        {
            return DrainOutcome.Fault();
        }

        return new DrainOutcome { AllGreen = AllGreenFor(tasksToRun, settled), Decision = driftDecision };
    }

    /// <summary>Deliver (mergeOnSuccess) + end-of-run cleanup sweep — shared by the flat and waved paths.</summary>
    private RunReport Finalize(
        PlanDefinition plan, RunReport report, IntegrationHandle? integ,
        Dictionary<string, string> directoryOwner, IReadOnlyDictionary<string, TaskResult> settled,
        CancellationToken cancellationToken)
    {
        // Deliver the completed plan branch to the user's branch when every task succeeded and
        // mergeOnSuccess is enabled. AI-merge is withheld: a conflict halts with the plan branch intact.
        MergeOnSuccessResult? mergeOutcome = null;
        string? mergeDetail = null;
        if (report.AllSucceeded && plan.Config.MergeOnSuccess && _worktreeProvider != null && integ != null)
        {
            mergeOutcome = _worktreeProvider.MergePlanBranchIntoUserBranch(integ, cancellationToken);
            if (mergeOutcome == MergeOnSuccessResult.HookRejected)
            {
                mergeDetail = _worktreeProvider.LastMergeOnSuccessDetail;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            EndOfRunSweep(directoryOwner, settled, integ);
        }

        return report with { MergeOnSuccessOutcome = mergeOutcome, MergeOnSuccessDetail = mergeDetail };
    }

    private static bool AllGreenFor(IReadOnlyList<TaskNode> tasks, IReadOnlyDictionary<string, TaskResult> settled) =>
        tasks.All(t => settled.TryGetValue(t.Id, out TaskResult? r) && r.IsGreen);

    /// <summary>The outcome of one <see cref="DrainAsync"/>: a halt (drift/abort), an infra fault, or a completed drain.</summary>
    private sealed record DrainOutcome
    {
        /// <summary>True when every task in the drained subset is green (succeeded this run or skipped).</summary>
        public bool AllGreen { get; init; }

        /// <summary>Non-null on a pre-schedule task-level definition-drift halt (§7.2) — nothing scheduled.</summary>
        public DefinitionDriftReport? Drift { get; init; }

        /// <summary>Non-null on a pre-schedule read/git abort during the drift check — nothing scheduled (no sweep).</summary>
        public RunAbort? ReadAbort { get; init; }

        /// <summary>True when a worker loop hit an infra fault (<see cref="_fault"/> is set) — the caller sweeps.</summary>
        public bool Faulted { get; init; }

        /// <summary>A task-level drift auto-resolution decision recorded this drain (for the summary), or null.</summary>
        public DecisionEntry? Decision { get; init; }

        public static DrainOutcome DriftHalt(DefinitionDriftReport drift) => new() { Drift = drift };
        public static DrainOutcome Abort(RunAbort abort) => new() { ReadAbort = abort };
        public static DrainOutcome Fault() => new() { Faulted = true };
    }

    // --- wave loop helpers (SSOT §14, #254 M2b) -------------------------------------------

    /// <summary>
    /// Whether a wave is COMPLETE (SSOT §14.5): every task DURABLY green AND its completion is recorded
    /// (journal <c>completed</c> OR a <c>Guardrails-Wave:</c> marker). Also returns the recorded
    /// <c>WaveDefinitionHash</c> for the drift check (null ⇒ "unknown — assume unchanged").
    /// <para>
    /// "Durably green" is trailer-authoritative in worktree mode (#311 NIT-2): when the plan branch is the
    /// durable integration record (<paramref name="trailerTracking"/>), a task counts green ONLY if its
    /// <c>Guardrails-Task:</c> trailer is on the branch — a journal-<c>succeeded</c>-but-trailer-ABSENT task
    /// (a kill / rewind that discarded the commit) forces the wave INCOMPLETE, mirroring the flat path's
    /// "trailer-absent ⇒ re-run" reconciliation (<see cref="DetectDefinitionDrift"/>) so a no-drift crash
    /// can't leave a wave falsely complete over a missing base. In serial / non-trailer mode there are no
    /// trailers, so the journal <c>succeeded</c> status is authoritative (the pre-#311 behaviour).
    /// </para>
    /// </summary>
    private (bool Complete, string? RecordedHash) EvaluateWaveCompletion(
        WaveNode wave,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
        IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers,
        bool trailerTracking)
    {
        Journal.WaveJournalEntry? je = _journal.WaveEntryOf(wave.Dir);
        waveMarkers.TryGetValue(wave.Dir, out PlanBranchWaveRecord? marker);

        bool allTasksGreen = wave.Tasks.Count > 0 && wave.Tasks.All(t => trailerTracking
            ? planBranchRecords.ContainsKey(t.Id)
            : _journal.StatusOf(t.Id) == JournalTaskStatus.Succeeded);

        bool completionRecorded = je?.Status == Journal.WaveStatus.Completed || marker is not null;

        bool complete = allTasksGreen && completionRecorded;
        string? recordedHash = je?.DefinitionHash ?? marker?.WaveDefinitionHash;
        return (complete, recordedHash);
    }

    /// <summary>
    /// Run a wave's ENTRY preflight gate (SSOT §14.3) against the plan-branch HEAD (= materialized prior
    /// wave), or the workspace in serial mode. Skip-once: a passed entry marker for this wave is not
    /// re-evaluated on resume (a negative-baseline entry check runs exactly once; the wave-drift/reset path
    /// clears the marker so a changed wave re-runs it). Self-records the entry marker + sets the wave
    /// <c>running</c>. Returns the pass verdict + failing checks.
    /// </summary>
    private async Task<(bool Passed, IReadOnlyList<GuardrailResult> Failed)> RunWaveEntryGateAsync(
        PlanDefinition plan, WaveNode wave, IntegrationHandle? integ, CancellationToken ct)
    {
        if (wave.Preflights.Count == 0)
        {
            _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.Running);
            return (true, []);
        }

        if (_journal.WaveEntryOf(wave.Dir)?.Entry is { Status: Journal.PlanPhaseStatus.Passed })
        {
            _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.Running);
            return (true, []); // skip-once: already passed this run's journal.
        }

        string workspace = integ?.IntegrationWorktreePath ?? plan.Workspace;
        ReVerifyResult result = _reVerifier is not null
            ? await _reVerifier.ReVerifyAsync(workspace, wave.Preflights, ct).ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        var checks = wave.Preflights.Select(g =>
        {
            GuardrailResult? failure = result.FailedGuardrails
                .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
            return new Journal.PlanPreflightCheck { Name = g.Name, Passed = failure is null, Reason = failure?.Reason };
        }).ToList();

        _journal.RecordWaveEntry(wave.Dir, new Journal.PlanPreflightsSection
        {
            Status = result.Passed ? Journal.PlanPhaseStatus.Passed : Journal.PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = Journal.PlanHash.Compute(plan),
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks
        });

        return (result.Passed, result.FailedGuardrails);
    }

    /// <summary>
    /// Run a wave's EXIT / terminal gate (SSOT §14.3) on the merged HEAD-so-far — the per-wave analogue of
    /// the plan-terminal <c>&lt;plan&gt;/guardrails/</c> phase. Always re-evaluated (never skipped). The LAST
    /// wave's exit gate is the whole-plan terminal soundness boundary. Self-records the exit marker. Returns
    /// the pass verdict + failing checks.
    /// </summary>
    private async Task<(bool Passed, IReadOnlyList<GuardrailResult> Failed)> RunWaveExitGateAsync(
        PlanDefinition plan, WaveNode wave, IntegrationHandle? integ, CancellationToken ct)
    {
        if (wave.Guardrails.Count == 0)
        {
            return (true, []);
        }

        string workspace = integ?.IntegrationWorktreePath ?? plan.Workspace;
        ReVerifyResult result = _reVerifier is not null
            ? await _reVerifier.ReVerifyAsync(workspace, wave.Guardrails, ct).ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        var failed = result.FailedGuardrails
            .Select(f => new Journal.FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList();
        _journal.RecordWaveExit(wave.Dir, new Journal.PlanGuardrailsSection
        {
            Status = result.Passed ? Journal.PlanPhaseStatus.Passed : Journal.PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = Journal.PlanHash.Compute(plan),
            FailedChecks = failed
        });

        return (result.Passed, result.FailedGuardrails);
    }

    /// <summary>The outcome of a wave-drift rewind: a resolved <see cref="DecisionEntry"/>, or a REFUSE reason (halt).</summary>
    private sealed record WaveRewindResult(DecisionEntry? Decision, string? Refusal);

    /// <summary>
    /// Wave-level drift resolution (SSOT §14.6/§14.8): rewind the plan branch past this wave + all its
    /// downstream waves and journal-reset them, then refresh the reconciled maps. The rewind ROUTES THROUGH
    /// the marker-aware <see cref="SafeSuffixEvaluator"/> (via <see cref="IWorktreeProvider.EvaluateSafeSuffix"/>)
    /// exactly like the task-level Part C path (BLOCKER fix, #311): the evaluator DERIVES the reset target
    /// from the live first-parent history (always an ancestor of the tip — no dangling-sha sideways reset),
    /// EXEMPTS the harness's own <c>Guardrails-Wave:</c> markers, and REFUSES if a trailer-less NON-marker
    /// commit (a human #197 hand-fix) is in the removed range — so the §14.8 "always safe" property holds for
    /// pure-harness history but a rewind never silently eats a human's fix. A tip compare-and-swap (WEAK-4)
    /// guards a concurrent same-plan session. Crash-atomic via <see cref="State.RewindIntent"/> (now carrying
    /// the wave dirs too, BLOCKER-1b). Returns the <c>wave</c>-boundary decision, or a REFUSE reason to halt.
    /// </summary>
    private WaveRewindResult ResolveWaveDrift(
        PlanDefinition plan, IReadOnlyList<WaveNode> waves, int waveIndex, WaveNode wave, IntegrationHandle? integ,
        string oldHash, string newHash,
        ref IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
        ref IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers)
    {
        var affectedWaves = new List<WaveNode>();
        for (int j = waveIndex; j < waves.Count; j++)
        {
            affectedWaves.Add(waves[j]);
        }

        List<string> affectedTaskIds = affectedWaves.SelectMany(w => w.Tasks.Select(t => t.Id)).ToList();
        List<string> affectedWaveDirs = affectedWaves.Select(w => w.Dir).ToList();
        var safeSet = new HashSet<string>(affectedTaskIds, StringComparer.Ordinal);

        // Safe-suffix check against the plan branch (marker-aware). Serial / no provider → NothingToRewind
        // (a journal-only reset is sound where there is no branch to carry a stale commit).
        SafeSuffixDecision decision = _worktreeProvider is { } provider && integ is { } activeInteg
            ? provider.EvaluateSafeSuffix(activeInteg, safeSet)
            : SafeSuffixDecision.Nothing();

        // Refuse floor (un-overridable, exactly like the task path): a human hand-fix in the range refuses.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return new WaveRewindResult(null, decision.Refusal);
        }

        string? resetTarget = decision.Outcome == SafeSuffixOutcome.Safe ? decision.ResetTarget : null;

        // Compare-and-swap (WEAK-4): for a real rewind, the tip must still be where the decision saw it, or
        // a concurrent same-plan session moved it — REFUSE rather than discard its work.
        if (decision.Outcome == SafeSuffixOutcome.Safe)
        {
            string currentTip = _worktreeProvider is { } tp && integ is { } ti ? tp.CurrentPlanBranchTip(ti) : "";
            if (!string.Equals(currentTip, decision.ExpectedTip ?? "", StringComparison.Ordinal))
            {
                return new WaveRewindResult(null,
                    "the plan branch changed while the wave-drift rewind was deciding (a concurrent same-plan run?) — refusing.");
            }
        }

        // Crash-atomic: record the intent (affected task ids AND wave dirs, BLOCKER-1b) BEFORE the
        // destructive rewind so a kill in between is idempotently replayed on resume; clear only AFTER both
        // effects persist. The wave dirs ensure the replay clears the wave entries too (no dangling MarkerSha).
        bool useMarker = decision.Outcome == SafeSuffixOutcome.Safe;
        if (useMarker)
        {
            State.RewindIntent.Write(_plan.PlanDirectory, new State.RewindIntent
            {
                SafeSet = affectedTaskIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Waves = affectedWaveDirs,
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = resetTarget
            });
        }

        if (resetTarget is { Length: > 0 } && _worktreeProvider is { } wpr && integ is { } integR)
        {
            wpr.RewindPlanBranchTo(integR, resetTarget);
        }

        foreach (WaveNode w in affectedWaves)
        {
            _journal.ResetWaveToPending(w.Dir);
            foreach (TaskNode t in w.Tasks)
            {
                _journal.ResetTaskToPending(t.Id);
            }
        }

        if (useMarker)
        {
            State.RewindIntent.Clear(_plan.PlanDirectory);
        }

        // The drifted+downstream commits/markers are gone from the branch — refresh so a subsequent drain
        // does not treat them as pre-settled via a stale trailer.
        if (_worktreeProvider is { } wpf && integ is { } integF)
        {
            planBranchRecords = wpf.ReconcileFromPlanBranch(integF);
            waveMarkers = wpf.ReconcileWavesFromPlanBranch(integF);
        }

        return new WaveRewindResult(
            DriftDecisions.WaveDriftResolved(
                _plan.Config.AutonomyPolicy, wave.Dir, resetTarget, oldHash, newHash, affectedWaveDirs),
            null);
    }

    /// <summary>Represent every task in the waves AFTER a halted wave as <c>blocked</c> (SSOT §14.4: later waves never start).</summary>
    private static void BlockLaterWaves(
        IReadOnlyList<WaveNode> waves, int haltedIndex, WaveNode haltedWave, Dictionary<string, TaskResult> settled)
    {
        for (int j = haltedIndex + 1; j < waves.Count; j++)
        {
            foreach (TaskNode t in waves[j].Tasks)
            {
                if (settled.ContainsKey(t.Id))
                {
                    continue;
                }

                settled[t.Id] = new TaskResult
                {
                    TaskId = t.Id,
                    Outcome = TaskOutcome.Blocked,
                    Summary = $"not started — halted at wave '{haltedWave.Dir}' barrier (SSOT §14.4)"
                };
            }
        }
    }

    private WaveHalt BuildWaveDriftHalt(
        IReadOnlyList<WaveNode> waves, int waveIndex, WaveNode wave, string oldHash, string newHash,
        string? unsafeRefusal)
    {
        var affected = new List<string>();
        for (int j = waveIndex; j < waves.Count; j++)
        {
            affected.Add(waves[j].Dir);
        }

        string folder = Path.GetFileName(
            _plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // When the rewind was REFUSED as unsound (a human hand-fix / unattributed commit in the range, or a
        // concurrent tip move — BLOCKER/WEAK-4), steer to the always-sound full rebuild and name WHY; an
        // auto-resolve flag would just re-refuse. Otherwise (a policy/consent halt) offer --autonomy auto.
        string detail = unsafeRefusal is { Length: > 0 } refusal
            ? $"WaveDefinitionHash {ShortHash(oldHash)} -> {ShortHash(newHash)}. Cannot safely rewind this wave: "
              + $"{refusal} Resolve the plan branch manually, or 'guardrails reset {folder} -y' for a full rebuild."
            : $"WaveDefinitionHash {ShortHash(oldHash)} -> {ShortHash(newHash)}. Resolving would rewind + "
              + $"re-run this wave + {affected.Count - 1} downstream wave(s). Re-run with '--autonomy auto' to "
              + $"rewind + re-run, or 'guardrails reset {folder} {wave.Dir}' to reset it explicitly.";

        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.WaveDrift,
            Headline = $"Wave '{wave.Dir}' DRIFTED — its definition changed since it completed (SSOT §14.6).",
            Detail = detail,
            AffectedWaves = affected,
            OldHash = oldHash,
            NewHash = newHash
        };
    }

    private static WaveHalt BuildUnauthoredWaveHalt(WaveNode wave, IntegrationHandle? integ)
    {
        string? worktree = integ?.IntegrationWorktreePath;
        string at = worktree is not null ? $" at:\n  {worktree}" : "";
        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.NextWaveUnauthored,
            Headline = $"Wave '{wave.Dir}' has no authored tasks — halting for JIT breakdown (SSOT §14.4).",
            Detail = "The prior wave(s) completed and are materialized on the plan branch. Break down + review "
                   + $"'{wave.Dir}' against the materialized upstream artifacts{at}\nthen re-run 'guardrails run' to continue.",
            IntegrationWorktreePath = worktree
        };
    }

    private static WaveHalt BuildGateHalt(WaveNode wave, WaveHaltKind kind, IReadOnlyList<GuardrailResult> failed)
    {
        string gate = kind == WaveHaltKind.EntryGateFailed ? "entry preflight" : "exit gate";
        string names = failed.Count == 0 ? "(no per-check detail)" : string.Join(", ", failed.Select(f => f.Name));
        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = kind,
            Headline = $"Wave '{wave.Dir}' {gate} FAILED: {names}",
            Detail = string.Join("\n", failed.Select(f => $"{f.Name} — {f.Reason ?? "failed"}")),
            FailedGates = failed
        };
    }

    private static IReadOnlyDictionary<string, PlanBranchWaveRecord> WithWaveMarker(
        IReadOnlyDictionary<string, PlanBranchWaveRecord> map, string waveDir, PlanBranchWaveRecord record)
    {
        var copy = new Dictionary<string, PlanBranchWaveRecord>(map, StringComparer.Ordinal) { [waveDir] = record };
        return copy;
    }

    /// <summary>Shorten a <c>sha256:</c>-prefixed hash for display.</summary>
    private static string ShortHash(string hash)
    {
        const string prefix = "sha256:";
        string body = hash.StartsWith(prefix, StringComparison.Ordinal) ? hash[prefix.Length..] : hash;
        return body.Length <= 10 ? body : body[..10];
    }

    /// <summary>
    /// The resume drift pre-pass (§7.2, #274 Part A): determine the pre-settled-green candidates (journal
    /// <c>Succeeded</c> OR a plan-branch trailer) and, for each one carrying a recorded definition hash,
    /// recompute the current <see cref="Journal.TaskDefinitionHash"/> and record a drift when they differ.
    /// A recorded-absent candidate (pre-upgrade) is treated as "unknown — assume unchanged". Reads the
    /// task's definition files from disk, so its IO is wrapped by the caller's #150 honest-abort guard.
    /// </summary>
    private (HashSet<string> PreSettledGreen, List<DefinitionDriftReporter.DriftInput> Drifted)
        DetectDefinitionDrift(
            IReadOnlyList<TaskNode> tasks,
            IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
            bool trailerTracking)
    {
        var preSettledGreen = new HashSet<string>(StringComparer.Ordinal);
        var drifted = new List<DefinitionDriftReporter.DriftInput>();

        foreach (TaskNode task in tasks)
        {
            bool journalGreen = _journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded;
            planBranchRecords.TryGetValue(task.Id, out PlanBranchTaskRecord? trailer);
            if (!journalGreen && trailer is null)
            {
                continue;
            }

            // Part C resume reconciliation (#274, SSOT §7.2): where the plan branch is the authoritative
            // integration record (worktree mode), a task that the journal calls Succeeded but whose
            // integration trailer is ABSENT from the current plan-branch history had its commit rewound off
            // (a crash mid safe-drift-resolution, or an external rewind). Its work is NOT on the branch —
            // it MUST re-run, never be skipped. This closes the new invariant Part C's reset --hard can
            // break, catching the inconsistency however it arose. Serial / non-trailer providers keep the
            // journal-only semantics (no trailers to consult).
            if (trailerTracking && journalGreen && trailer is null)
            {
                _journal.ResetTaskToPending(task.Id);
                continue; // pending → scheduled (re-run)
            }

            preSettledGreen.Add(task.Id);

            // Recorded hash: prefer the journal's (the primary record); fall back to the plan-branch
            // trailer (covers a journal-reset resume where only the plan branch survives). Both are
            // stamped at the same settle, so they agree; either being present enables the check.
            string? recordedHash = _journal.RecordedDefinitionHash(task.Id) ?? trailer?.DefinitionHash;
            if (recordedHash is null)
            {
                continue;
            }

            string currentHash = Journal.TaskDefinitionHash.Compute(task);
            if (!string.Equals(recordedHash, currentHash, StringComparison.Ordinal))
            {
                drifted.Add(new DefinitionDriftReporter.DriftInput(
                    task.Id, recordedHash, currentHash, trailer?.CommitSha));
            }
        }

        return (preSettledGreen, drifted);
    }

    /// <summary>The outcome of the Part C gate: a decision (rewound + reset) or a halt, with the safe/unsafe distinction the CLI needs to render the right remedy.</summary>
    private sealed record DriftGateResult(
        DecisionEntry? Decision, bool SafeToAutoResolve, string? Refusal, string? BlockingTask)
    {
        public static DriftGateResult Resolved(DecisionEntry decision) => new(decision, true, null, null);

        /// <summary>Halt because the rewind is UNSOUND (a non-suffix / uncontained fan-in / trailer-less commit) — no flag authorizes it.</summary>
        public static DriftGateResult Unsafe(string? refusal, string? blockingTask) => new(null, false, refusal, blockingTask);

        /// <summary>Halt a provably-SAFE drift because the policy/consent did not authorize it (strict halt, unconfirmed prompt, a consent-void plan, or a moved tip).</summary>
        public static DriftGateResult HaltSafe() => new(null, true, null, null);
    }

    /// <summary>
    /// Part C safe-auto-resolve (issue #274, SSOT §7.2). The drifted set <c>S</c> = the drifted tasks ∪
    /// their <see cref="DependencyGraph.TransitiveDependentsOf"/> closure (a changed producer can change a
    /// consumer's inputs). Evaluate whether <c>S</c> forms a provably-safe trailing suffix of the plan
    /// branch (<see cref="SafeSuffixEvaluator"/> via the provider), then apply the gating:
    /// <list type="bullet">
    ///   <item>UNSAFE (Refused) → HALT (unsafe). No policy authorizes an unsound rewind.</item>
    ///   <item><see cref="AutonomyPolicy.Halt"/> → HALT (safe; strict opt-out, the Part A behavior).</item>
    ///   <item><see cref="AutonomyPolicy.Auto"/> → resolve on this run's own fresh decision (pre-authorized spend).</item>
    ///   <item><see cref="AutonomyPolicy.Prompt"/> → resolve ONLY when the CLI captured an operator <c>y</c>
    ///     (<see cref="_driftAuthorization"/>) AND that captured plan still matches this fresh decision AND
    ///     the branch tip has not moved; otherwise HALT (Core never prompts).</item>
    /// </list>
    /// The destructive section is CRASH-ATOMIC: a rewind-intent marker is written BEFORE the
    /// <c>git reset --hard</c> and cleared only AFTER both the rewind and every journal-reset persist, so a
    /// kill in between is idempotently replayed on resume. A COMPARE-AND-SWAP on the plan-branch tip guards
    /// against a concurrent same-plan session (or an operator editing during the blocking prompt) making the
    /// harness rewind a set the decision/human never saw — a mismatch HALTS, never rewinds.
    /// </summary>
    private DriftGateResult TryResolveDrift(
        PlanDefinition plan,
        DependencyGraph graph,
        List<DefinitionDriftReporter.DriftInput> drifted,
        IntegrationHandle? integ)
    {
        // S = drifted ∪ transitive descendants (this run's OWN fresh computation).
        var safeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (DefinitionDriftReporter.DriftInput d in drifted)
        {
            safeSet.Add(d.TaskId);
            foreach (string dependent in graph.TransitiveDependentsOf(d.TaskId))
            {
                safeSet.Add(dependent);
            }
        }

        // Safety check against the plan branch. Serial / no-provider → NothingToRewind (a journal-only
        // reset is sound where there is no branch to carry a stale commit).
        SafeSuffixDecision decision = _worktreeProvider is { } provider && integ is { } activeInteg
            ? provider.EvaluateSafeSuffix(activeInteg, safeSet)
            : SafeSuffixDecision.Nothing();

        // Refuse floor (un-overridable): an unsafe rewind ALWAYS halts, regardless of policy. Surface the
        // reason + blocker so the CLI steers to the always-sound rebuild rather than a re-halting flag.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return DriftGateResult.Unsafe(decision.Refusal, decision.BlockingTask);
        }

        // Authorization gate (only a provably-safe / nothing-to-rewind drift reaches here). The switch
        // decides resolve-vs-halt ONLY; the DecisionEntry is built from _plan.Config.AutonomyPolicy below.
        switch (_plan.Config.AutonomyPolicy)
        {
            case AutonomyPolicy.Auto:
                break;

            case AutonomyPolicy.Prompt when _driftAuthorization is { } auth:
                // Consent integrity: the operator approved a SPECIFIC plan (from the probe's preview). If
                // files edited during the blocking prompt changed what would be rewound (S / target
                // diverges), HALT — never rewind a set the human did not see.
                if (!AuthorizationMatches(auth, safeSet, decision))
                {
                    return DriftGateResult.HaltSafe();
                }

                break;

            default: // Halt policy, or an unconfirmed Prompt policy — Core never prompts.
                return DriftGateResult.HaltSafe();
        }

        // Compare-and-swap: for a real rewind, the branch must still be exactly where the decision (and,
        // for a prompt, the operator) saw it. A concurrent same-plan session that advanced/rewound the
        // branch since is DETECTED here → HALT rather than discard its work.
        string? resetTarget = decision.Outcome == SafeSuffixOutcome.Safe ? decision.ResetTarget : null;
        if (decision.Outcome == SafeSuffixOutcome.Safe)
        {
            string authorizedTip = _driftAuthorization?.ExpectedTip ?? decision.ExpectedTip ?? "";
            string currentTip = _worktreeProvider is { } tipProvider && integ is { } tipInteg
                ? tipProvider.CurrentPlanBranchTip(tipInteg)
                : "";
            if (!string.Equals(currentTip, authorizedTip, StringComparison.Ordinal))
            {
                return DriftGateResult.HaltSafe();
            }
        }

        // CRASH-ATOMIC destructive section. The marker is only needed for a real plan-branch rewind
        // (trailer-tracking); a serial journal-only reset has no discarded commits to lose and self-heals
        // via re-detection. Write BEFORE the rewind, clear only AFTER both effects persist.
        bool useMarker = decision.Outcome == SafeSuffixOutcome.Safe
            && _worktreeProvider?.TracksPlanBranchTrailers == true;
        if (useMarker)
        {
            State.RewindIntent.Write(_plan.PlanDirectory, new State.RewindIntent
            {
                SafeSet = safeSet.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = resetTarget
            });
        }

        if (resetTarget is not null && _worktreeProvider is { } rewindProvider && integ is { } rewindInteg)
        {
            rewindProvider.RewindPlanBranchTo(rewindInteg, resetTarget);
        }

        // Journal-reset every member of S so the next scheduling wave re-runs it from the clean base.
        foreach (string taskId in safeSet)
        {
            _journal.ResetTaskToPending(taskId);
        }

        if (useMarker)
        {
            State.RewindIntent.Clear(_plan.PlanDirectory);
        }

        return DriftGateResult.Resolved(DriftDecisions.AutoResolved(
            _plan.Config.AutonomyPolicy, resetTarget, BuildResolvedTasks(plan, drifted, safeSet)));
    }

    /// <summary>
    /// True when the operator-approved plan (<paramref name="auth"/>, captured by the CLI probe) still
    /// matches this run's fresh decision — same reset target and same safe set. A mismatch means files were
    /// edited during the blocking prompt so a rewind now would discard a set the human never saw → the
    /// caller HALTS (issue #274 Part C consent integrity).
    /// </summary>
    private static bool AuthorizationMatches(
        DriftAuthorization auth, HashSet<string> safeSet, SafeSuffixDecision decision)
    {
        if (!string.Equals(auth.ResetTarget, decision.ResetTarget, StringComparison.Ordinal))
        {
            return false;
        }

        if (auth.SafeSet.Count != safeSet.Count)
        {
            return false;
        }

        foreach (string t in auth.SafeSet)
        {
            if (!safeSet.Contains(t))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replay a surviving rewind-intent marker (issue #274 Part C crash-atomicity): a prior resolution was
    /// killed between the plan-branch rewind and its journal-resets. Idempotently re-reset the whole
    /// recorded set to <c>pending</c> (so a non-drifted descendant whose commit was already discarded
    /// re-runs, never silently skipped) — AND, for a WAVE-scoped rewind (#254 M2b, BLOCKER-1b), re-reset the
    /// recorded wave entries too, so a wave never survives as <c>Completed</c> with a now-dangling
    /// <c>MarkerSha</c> a later <c>reset --hard</c> could resolve SIDEWAYS. Then clear the marker.
    /// Best-effort: a read hiccup leaves the general trailer-reconciliation invariant as the safety net.
    /// </summary>
    private void ReplayRewindIntentIfPresent()
    {
        if (State.RewindIntent.TryRead(_plan.PlanDirectory) is not { } intent)
        {
            return;
        }

        foreach (string taskId in intent.SafeSet)
        {
            _journal.ResetTaskToPending(taskId);
        }

        foreach (string waveDir in intent.Waves)
        {
            _journal.ResetWaveToPending(waveDir);
        }

        State.RewindIntent.Clear(_plan.PlanDirectory);
    }

    /// <summary>
    /// Build the per-task old→new definition-hash audit for a Part C resolution (issue #274): a drifted
    /// task carries the hash pair the drift check already computed; a rebuilt descendant carries its
    /// last-recorded hash (or a sentinel when none) → its current on-disk hash. Emitted in plan order.
    /// </summary>
    private IReadOnlyList<DriftResolvedTask> BuildResolvedTasks(
        PlanDefinition plan, List<DefinitionDriftReporter.DriftInput> drifted, IReadOnlySet<string> safeSet)
    {
        var driftById = drifted.ToDictionary(d => d.TaskId, StringComparer.Ordinal);
        var resolved = new List<DriftResolvedTask>();

        foreach (TaskNode task in plan.Tasks)
        {
            if (!safeSet.Contains(task.Id))
            {
                continue;
            }

            if (driftById.TryGetValue(task.Id, out DefinitionDriftReporter.DriftInput input))
            {
                resolved.Add(new DriftResolvedTask { TaskId = task.Id, OldHash = input.OldHash, NewHash = input.NewHash });
            }
            else
            {
                // A rebuilt descendant that did not itself drift: report its recorded → current hash. This
                // runs AFTER the rewind + journal-reset, so a read failure here must NOT throw (which would
                // abort a run whose branch is already rewound) — the audit degrades to a sentinel instead.
                string oldHash = _journal.RecordedDefinitionHash(task.Id) ?? "(none recorded)";
                string newHash;
                try
                {
                    newHash = Journal.TaskDefinitionHash.Compute(task);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    newHash = "(unreadable)";
                }

                resolved.Add(new DriftResolvedTask { TaskId = task.Id, OldHash = oldHash, NewHash = newHash });
            }
        }

        return resolved;
    }

    /// <summary>
    /// The <see cref="RunAbort"/> for a definition-file read failure during the resume drift pre-pass
    /// (§7.2, #274 Part A): typically a transient file lock (an editor / antivirus / indexer holding a
    /// guardrail or <c>task.json</c>, common on Windows). Distinct from <see cref="BuildAbort"/> so the
    /// remedy is specific — and it makes explicit that the drift check ABORTS rather than silently skips,
    /// so a real definition change can never slip through unseen.
    /// </summary>
    private static RunAbort BuildDefinitionReadAbort(Exception fault) => new()
    {
        Headline = "The run was aborted: a task definition file could not be read during the resume "
                 + $"drift check: {fault.Message}",
        Remedy = "A definition file (task.json / the action / a guardrail) could not be read — often a "
               + "transient file lock (an editor, antivirus, or indexer holding it, common on Windows). "
               + "Release it and re-run. The drift check is aborted rather than skipped, so a real "
               + "definition change can never slip through unseen.",
        Detail = fault.ToString()
    };

    /// <summary>
    /// Build the <see cref="RunAbort"/> for an infrastructure fault (issue #150): a one-line headline
    /// + remedy for the console, and the full exception text for the run logs. A dev tool keeps the
    /// detail — just not as the headline.
    /// </summary>
    private static RunAbort BuildAbort(Exception fault) => new()
    {
        Headline = $"The run was aborted by an unexpected infrastructure fault: {fault.Message}",
        Remedy = "See the full exception in the run logs below. This is a harness/environment fault "
               + "(e.g. an offline or failing git hook on an internal commit, or git unavailable), "
               + "not a task failure — resolve it and re-run to resume.",
        Detail = fault.ToString()
    };

    /// <summary>
    /// plan 08 topology-wiring M2 §D (#126): remove every segment/fork worktree directory owned by a
    /// task that settled GREEN, then prune stale registrations. A green task's work is durable on the
    /// plan branch, so its directory is pure waste — this is the direct #126 fix (a wholly-green run
    /// leaves no segment worktree behind).
    ///
    /// A NON-green task (needs-human / failed / blocked) keeps its directory: the "fix, don't restart"
    /// invariant (§3.2, open-risk #4) requires a failed attempt's worktree to survive so a human — or
    /// a resume's reset-and-retry — can inspect the scoped-revert artifacts and WIP. The next run's
    /// PruneStaleRunBranches pre-pass reclaims those. The <c>_integration</c> worktree is never in
    /// <see cref="RunContext.DirectoryOwner"/> and is therefore never swept.
    ///
    /// Best-effort — a cleanup failure logs (via <see cref="IRunObserver.CleanupFailed"/>) and
    /// continues; it must NEVER flip an otherwise-green run off-green (GitWorktreeProvider.Discard
    /// throws on a non-zero git exit, so each call-site swallows).
    /// </summary>
    private void EndOfRunSweep(
        Dictionary<string, string> directoryOwner, IReadOnlyDictionary<string, TaskResult> settled, IntegrationHandle? integ)
    {
        if (_worktreeProvider is not { } provider || integ is null)
        {
            return;
        }

        // Snapshot under the gate (no workers are running now, but keep the discipline consistent).
        // Sweep only GREEN-owned directories; non-green tasks keep their worktree for fix/resume.
        List<KeyValuePair<string, string>> sweepable;
        lock (_gate)
        {
            sweepable = directoryOwner
                .Where(kv => settled.TryGetValue(kv.Value, out TaskResult? r) && r.IsGreen)
                .ToList();
        }

        foreach ((string path, string owner) in sweepable)
        {
            try
            {
                provider.Discard(new WorktreeHandle { WorktreePath = path });
            }
            catch (Exception ex)
            {
                _observer.CleanupFailed(owner, ex);
            }
            finally
            {
                lock (_gate)
                {
                    directoryOwner.Remove(path);
                }
            }
        }

        try
        {
            provider.PruneOrphans(Array.Empty<string>(), integ);
        }
        catch (Exception ex)
        {
            _observer.CleanupFailed("(prune-orphans)", ex);
        }
    }

    private async Task WorkerLoopAsync(RunContext context, CancellationTokenSource runCts)
    {
        CancellationToken cancellationToken = runCts.Token;
        try
        {
            await foreach (TaskEnvelope envelope in context.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                TaskNode task = envelope.Task;
                WorktreeHandle handle = MaterializeForkIfDeferred(context, envelope);

                if (CostCapHaltFor(task) is { } capped)
                {
                    await OnSettledAsync(context, task, capped, handle, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TaskResult result = await _executor.ExecuteAsync(task, handle, cancellationToken).ConfigureAwait(false);

                await OnSettledAsync(context, task, result, handle, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled drain.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault ??= ex;
            }

            context.Channel.Writer.TryComplete();
            runCts.Cancel();
        }
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §B: materialize a deferred fork-the-rest sibling's worktree at
    /// dequeue — the actual <c>git worktree add</c> runs HERE, OFF the <see cref="_gate"/> every
    /// settling worker contends for. The fork roots off the producer's RECORDED sha (captured in
    /// the request under <c>_gate</c> at assignment, W-2), never a live rev-parse of the segment
    /// branch the inheritor may have advanced. Returns the envelope's existing handle unchanged
    /// when there is no deferred fork.
    /// </summary>
    private WorktreeHandle MaterializeForkIfDeferred(RunContext context, TaskEnvelope envelope)
    {
        if (envelope.Fork is not { } fork || _worktreeProvider is not { } provider)
        {
            return envelope.Handle;
        }

        // git I/O off the gate.
        WorktreeHandle handle = provider.ForkFromTip(fork.ProducerRecordedSha, envelope.Task.Id, attempt: 1);

        // Bookkeeping under the gate: record the assigned handle + directory ownership.
        lock (_gate)
        {
            context.Handles[envelope.Task.Id] = handle;
            if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                context.DirectoryOwner[handle.WorktreePath] = envelope.Task.Id;
            }
        }

        return handle;
    }

    private TaskResult? CostCapHaltFor(TaskNode task)
    {
        if (_plan.Config.MaxCostUsd is not { } cap || _journal.CurrentCostUsd() < cap)
        {
            return null;
        }

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"cost cap reached: cumulative journaled cost has reached the configured " +
                      $"maxCostUsd (${cap}); task not launched."
        };
    }

    /// <summary>
    /// Called after a task finishes (executor or cost-cap halt). For worktree-mode green results,
    /// performs the B1 deferred settle (fragment merge → git integration commit → journal settle)
    /// under <see cref="_integrationLock"/> BEFORE updating the shared run context under
    /// <see cref="_gate"/>. This ordering ensures dependents only become ready after the upstream
    /// integration has advanced the plan branch, making lazy handle creation FF-compatible.
    /// </summary>
    private async Task OnSettledAsync(
        RunContext context, TaskNode task, TaskResult result, WorktreeHandle handle, CancellationToken ct)
    {
        // B1 deferred settle (worktree mode, real segment): ValidateFragmentForSettle sets
        // DeferredSettle=true, meaning the Scheduler owns the fragment merge → git commit →
        // journal settle sequence under the integration lock.
        //
        // Old path (serial mode or fake provider): the executor already merged + journaled;
        // just call provider.Integrate directly so IWorktreeProvider.IntegrateCallCount tests pass.
        if (result.IsGreen && _worktreeProvider is { } provider && context.Integ is { } integ)
        {
            if (result.DeferredSettle)
            {
                await _integrationLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    result = await SettleAsync(task, result, handle, provider, integ, ct).ConfigureAwait(false);
                }
                finally
                {
                    _integrationLock.Release();
                }
            }
            else if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                // Non-deferred: executor already handled journal; just integrate the segment. Stamp the
                // definition hash onto the handle so the integration commit still carries the
                // Guardrails-Task-Hash: trailer (§7.2) — the executor already recorded the journal hash.
                handle.DefinitionHash = Journal.TaskDefinitionHash.Compute(task);
                provider.Integrate(handle, integ, CancellationToken.None);
            }

            // #195 retry-salvage pruning (deliverable 6): once a task's FINAL settle (after the
            // deferred B1 settle above, which can still turn a green-looking result into NeedsHuman on
            // a failed union re-verify) is truly Succeeded, its salvage refs — its own prior
            // rolled-back partial attempts — have served their purpose and are pruned so they never
            // accumulate across a long-lived repo. Checked on result.Outcome (the POST-settle value),
            // never result.IsGreen (which is true before SettleAsync can still flip it to NeedsHuman).
            if (result.Outcome == TaskOutcome.Succeeded)
            {
                try { provider.PruneSalvageRefs(task.Id); }
                catch (Exception ex) { _observer.CleanupFailed(task.Id, ex); }
            }
        }

        var newlyReady = new List<TaskEnvelope>();
        var newlyBlocked = new List<TaskResult>();

        lock (_gate)
        {
            context.Settled[task.Id] = result;
            context.Remaining--;

            if (result.IsGreen)
            {
                // Which dependents of this producer just had their LAST pending dep cleared?
                // (A multi-producer dependent here is a fan-in: it has other, already-green
                // producers, and reaches the merged plan tip — never reused, M1 §A1.)
                var justReady = new List<string>();
                foreach (string dependent in context.Graph.DependentsOf(task.Id))
                {
                    if (!context.Settled.ContainsKey(dependent) && --context.PendingDeps[dependent] == 0)
                    {
                        justReady.Add(dependent);
                    }
                }

                AssignDependentHandles(context, task, justReady, newlyReady);
            }
            else if (result.Outcome != TaskOutcome.Cancelled)
            {
                foreach (string dependent in context.Graph.TransitiveDependentsOf(task.Id)
                             .OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (context.Settled.ContainsKey(dependent))
                    {
                        continue;
                    }

                    var blocked = new TaskResult
                    {
                        TaskId = dependent,
                        Outcome = TaskOutcome.Blocked,
                        Summary = $"blocked: dependency '{task.Id}' did not succeed"
                    };
                    context.Settled[dependent] = blocked;
                    context.Remaining--;
                    _journal.MarkBlocked(dependent);
                    newlyBlocked.Add(blocked);
                }

                // M2 §C / open-risk #4: a permanently-failed (needs-human/failed) task's segment is
                // NOT Discarded mid-run. The "fix, don't restart" invariant (§3.2) keeps a failed
                // attempt's worktree alive so a human (or a retry) can inspect the scoped-revert
                // artifacts and WIP. Its directory stays owned in DirectoryOwner and is reclaimed by
                // the end-of-run sweep at quiescence — which is what closes #126 (design §D point 2:
                // "#126 is closed by the run-end sweep alone"). Cancellation skips the sweep, so a
                // cancelled task's worktree survives for the resume prune (T-11).
            }

            if (context.Remaining == 0)
            {
                context.Channel.Writer.TryComplete();
            }
        }

        _observer.TaskFinished(result);
        foreach (TaskResult blocked in newlyBlocked)
        {
            _observer.TaskFinished(blocked);
        }

        // Each envelope already carries its assigned handle (fresh segment / reused directory) OR a
        // deferred fork request the worker materializes off-gate at dequeue (M1 §B).
        foreach (TaskEnvelope ready in newlyReady)
        {
            context.Channel.Writer.TryWrite(ready);
        }
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §A/§B: assign worktree handles to the dependents of a just-settled
    /// green producer <paramref name="producer"/>, choosing reuse vs fork vs fresh-segment.
    /// <list type="bullet">
    ///   <item><b>Multi-producer dependents (fan-in)</b> get a fresh <see cref="IWorktreeProvider.CreateSegment"/>
    ///     off the plan-branch tip, which already contains every producer's integrated work — never
    ///     reused (§A1).</item>
    ///   <item><b>Single-producer dependents</b> are the inherit-one/fork-rest fan-out. The inheritor
    ///     (longest downstream chain via <see cref="DependencyGraph.TransitiveDependentsOf"/>, ordinal-id
    ///     tiebreak) reuses the producer's segment directory via the pure-handle
    ///     <see cref="IWorktreeProvider.ReuseSegment"/> (safe under <see cref="_gate"/>; ownership
    ///     transfers to the inheritor). The rest fork off the producer's RECORDED sha — a DEFERRED
    ///     request the worker materializes off-gate (§B, W-2).</item>
    /// </list>
    /// Runs under <see cref="_gate"/>. All assignment + bookkeeping is here; only the fork's
    /// <c>git worktree add</c> is deferred off-gate.
    /// </summary>
    private void AssignDependentHandles(
        RunContext context, TaskNode producer, List<string> justReady, List<TaskEnvelope> newlyReady)
    {
        // Fan-in (multi-producer) dependents reach the merged plan tip with a fresh segment.
        var singleProducer = new List<string>();
        foreach (string dependent in justReady)
        {
            if (context.ById[dependent].DependsOn.Count > 1)
            {
                WorktreeHandle fanInHandle = CreateFreshSegment(context, dependent);
                context.Handles[dependent] = fanInHandle;
                RecordOwnership(context, fanInHandle, dependent);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], fanInHandle));
            }
            else
            {
                singleProducer.Add(dependent);
            }
        }

        if (singleProducer.Count == 0)
        {
            return;
        }

        // Inherit-one: the single-producer dependent with the longest downstream chain reuses the
        // producer's directory; ordinal-id tiebreak. The producer's handle carries the RecordedSha
        // that Integrate captured during this settle (strict happens-before).
        string inheritor = singleProducer
            .OrderByDescending(d => context.Graph.TransitiveDependentsOf(d).Count)
            .ThenBy(d => d, StringComparer.Ordinal)
            .First();

        WorktreeHandle? producerHandle =
            _worktreeProvider != null ? context.Handles.GetValueOrDefault(producer.Id) : null;

        foreach (string dependent in singleProducer)
        {
            if (dependent == inheritor && _worktreeProvider is { } reuseProvider && producerHandle is { } ph)
            {
                // Pure handle rewrite — no git, safe under _gate. Ownership of the producer's
                // directory transfers to the inheritor.
                WorktreeHandle reused = reuseProvider.ReuseSegment(ph, dependent, attempt: 1);
                context.Handles[dependent] = reused;
                if (!string.IsNullOrEmpty(reused.WorktreePath))
                {
                    context.DirectoryOwner[reused.WorktreePath] = dependent;
                }
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], reused));
            }
            else if (_worktreeProvider is not null && producerHandle is { } pf)
            {
                // Fork-the-rest: defer the git worktree add to the worker (off-gate). Root off the
                // producer's RECORDED sha — never the live segment-branch tip the inheritor advances.
                var fork = new ForkRequest(pf.RecordedCommitSha);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], new WorktreeHandle(), fork));
            }
            else
            {
                // No provider (serial/fake-less mode): an empty placeholder handle, as before.
                var placeholder = new WorktreeHandle();
                context.Handles[dependent] = placeholder;
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], placeholder));
            }
        }
    }

    /// <summary>Create a fresh segment off the plan-branch tip (or an empty handle without a provider).</summary>
    private WorktreeHandle CreateFreshSegment(RunContext context, string taskId) =>
        _worktreeProvider != null && context.Integ != null
            ? _worktreeProvider.CreateSegment(taskId, attempt: 1, context.Integ, CancellationToken.None)
            : new WorktreeHandle();

    /// <summary>Record directory ownership for a non-empty handle path (M0 bookkeeping; under <see cref="_gate"/>).</summary>
    private static void RecordOwnership(RunContext context, WorktreeHandle handle, string taskId)
    {
        if (!string.IsNullOrEmpty(handle.WorktreePath))
        {
            context.DirectoryOwner[handle.WorktreePath] = taskId;
        }
    }

    /// <summary>
    /// B1 fixed-order settle under <see cref="_integrationLock"/>:
    /// (1) deep-merge fragment into state.json,
    /// (2) git integration commit (FF or non-FF merge),
    /// (3) reserve mergeSequence + journal RecordSettle.
    /// On non-FF failure: restore state.json, reset integration worktree, journal NeedsHuman.
    /// </summary>
    private async Task<TaskResult> SettleAsync(
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        IWorktreeProvider provider,
        IntegrationHandle integ,
        CancellationToken ct)
    {
        string statePath = Path.Combine(_plan.PlanDirectory, "state", "state.json");
        string preMergeState = File.Exists(statePath) ? File.ReadAllText(statePath) : "{}";

        // §7.2 (#274 Part A): the task's definition hash, stamped onto BOTH the integration commit's
        // Guardrails-Task-Hash: trailer (via the handle for FF, the CommitStagedMerge param for non-FF)
        // AND the journal entry (RecordSucceededSettle) — computed once, under the integration lock,
        // from the current on-disk definition. This is what a later resume compares against.
        string definitionHash = Journal.TaskDefinitionHash.Compute(task);
        handle.DefinitionHash = definitionHash;

        // B1 step 1: merge fragment into state.json BEFORE the git commit.
        if (result.FragmentPath is { } fp && File.Exists(fp))
        {
            MergeFragmentIntoState(statePath, preMergeState, fp);
        }

        // B1 step 2: git integration commit (FF or non-FF union).
        IntegrationResult integResult = provider.Integrate(handle, integ, ct);

        if (integResult == IntegrationResult.FastForward)
        {
            // FF is free — no re-verify needed. Consume one merge sequence.
            long seq = _journal.ReserveMergeSequence();
            RecordSucceededSettle(task, result, seq, definitionHash);
            return result;
        }

        if (integResult == IntegrationResult.Conflict)
        {
            // AI merge worker resolves the conflict (§9.1). If no worker is wired or all
            // attempts fail, escalate to needs-human with a full B1 rollback.
            bool aiResolved = _aiMergeWorker != null
                && await _aiMergeWorker.TryResolveAsync(
                    integ.IntegrationWorktreePath,
                    handle.SegmentBranchName,
                    _plan.PlanDirectory,
                    _journal,
                    ct).ConfigureAwait(false);

            if (!aiResolved)
            {
                AtomicFile.WriteAllText(statePath, preMergeState);
                provider.RollbackMerge(integ, ct);
                _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
                return new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.NeedsHuman,
                    ActionExitCode = result.ActionExitCode,
                    Guardrails = result.Guardrails,
                    Summary = "merge conflict could not be AI-resolved; needs human"
                };
            }

            // AI merge succeeded: re-verify the merged bytes against the SAME integration
            // set as the non-AI-merge union path below (§4.3, v1 contract). Running the FULL
            // per-task set here false-fails by construction — it includes per-attempt
            // anti-tautology guardrails (tests-fail-on-current-code, which PASS post-merge),
            // scaffold-state and state-fragment-present checks (no action fragment exists at a
            // union point), and downstream tasks that have not run yet. The B-3 "AI may drop a
            // colliding sibling's hunk" concern is covered by the integration-scope union
            // guardrails (a well-authored integration/union-verify guardrail catches a dropped
            // hunk), the disjoint-scope CHECK, and the terminal integration gate — not by
            // re-running the full per-task set (which would be inconsistent with the union path).
            IReadOnlyList<GuardrailDefinition> aiIntegGuardrails =
                GuardrailScopeFilter.IntegrationSet(_plan.Tasks.SelectMany(t => t.Guardrails));

            ReVerifyResult aiReVerify = _reVerifier != null
                ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, aiIntegGuardrails, ct).ConfigureAwait(false)
                : new ReVerifyResult { Passed = true };

            if (aiReVerify.Passed)
            {
                // B2: commit the AI-resolved staged merge with the task trailer (incl. the §7.2
                // Guardrails-Task-Hash: line) BEFORE journaling.
                provider.CommitStagedMerge(integ, task.Id, ct, definitionHash);
                long seq = _journal.ReserveMergeSequence();
                RecordSucceededSettle(task, result, seq, definitionHash);
                return result;
            }

            // Re-verify failed after AI merge: B1 four-effect rollback.
            // #188: persist the failing integration guardrails' output + a feedback.md to the task log
            // dir BEFORE the rollback discards the merged bytes, so a human has the WHY on disk.
            string aiFeedbackPath = PersistUnionReVerifyFailure(task, result, integ, aiReVerify, aiMerge: true);
            AtomicFile.WriteAllText(statePath, preMergeState);
            provider.RollbackMerge(integ, ct);
            _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.NeedsHuman,
                ActionExitCode = result.ActionExitCode,
                Guardrails = result.Guardrails,
                Summary = "AI-merge resolution failed integration re-verify; needs human " +
                          $"(see {aiFeedbackPath})"
            };
        }

        // Non-FF union: re-verify the merged bytes in the integration worktree.
        IReadOnlyList<GuardrailDefinition> integGuardrails =
            GuardrailScopeFilter.IntegrationSet(_plan.Tasks.SelectMany(t => t.Guardrails));

        ReVerifyResult reVerify = _reVerifier != null
            ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, integGuardrails, ct).ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        if (reVerify.Passed)
        {
            // B2 step 2: commit the staged non-FF union with the task trailer BEFORE journaling,
            // so the plan branch carries this task's Guardrails-Task: trailer (the FF path commits
            // implicitly via the FF move; the non-FF path must commit the staged merge explicitly). The
            // §7.2 Guardrails-Task-Hash: line rides along on the same commit.
            provider.CommitStagedMerge(integ, task.Id, ct, definitionHash);
            long seq = _journal.ReserveMergeSequence();
            RecordSucceededSettle(task, result, seq, definitionHash);
            return result;
        }

        // #188: persist the failing integration guardrails' output + a feedback.md to the task log dir
        // BEFORE the four-effect rollback discards the merged bytes — otherwise the needs-human summary
        // points at a feedback.md that was never written and the failing guardrail output is lost.
        string feedbackPath = PersistUnionReVerifyFailure(task, result, integ, reVerify, aiMerge: false);

        // B1 four-effect rollback:
        // 1. Restore state.json (undo fragment merge).
        AtomicFile.WriteAllText(statePath, preMergeState);
        // 2. Reset integration worktree to pre-merge HEAD.
        provider.RollbackMerge(integ, ct);
        // 3. Journal NeedsHuman — mergeSequence NOT consumed.
        _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = result.ActionExitCode,
            Guardrails = result.Guardrails,
            Summary = $"non-FF union re-verify failed; rolled back (B1 four-effect) (see {feedbackPath})"
        };
    }

    /// <summary>
    /// Persist a failed union re-verify's evidence to the task log dir (issue #188): one
    /// <c>union-reverify-&lt;guardrail&gt;.stdout.log</c> per failing integration guardrail carrying its
    /// captured output, plus the <c>feedback.md</c> the needs-human summary points at (which the B1
    /// rollback path previously PROMISED but never wrote). Called BEFORE the rollback resets the
    /// integration worktree, so the merged-bytes evidence survives the discard. Returns the absolute
    /// <c>feedback.md</c> path for the summary. Best-effort: an IO failure here must never mask the
    /// underlying re-verify failure, so it degrades to returning the intended path.
    /// </summary>
    private string PersistUnionReVerifyFailure(
        TaskNode task, TaskResult result, IntegrationHandle integ, ReVerifyResult reVerify, bool aiMerge)
    {
        // The task log dir is the PARENT of this attempt's log dir. Derive it from the attempt's own
        // relative logDir (logs/<runId>/<taskId>/attempt-N) — which uses the JOURNAL's runId, the same
        // runId the executor writes attempt artifacts under — so the evidence lands beside them. Fall
        // back to integ.RunId only if no attempt data threaded through (defensive).
        string taskLogDir = result.PendingAttempt?.LogDir is { Length: > 0 } relLogDir
            ? Path.GetDirectoryName(Path.Combine(
                _plan.PlanDirectory, relLogDir.Replace('/', Path.DirectorySeparatorChar)))!
            : Path.Combine(_plan.PlanDirectory, "logs", integ.RunId, task.Id);
        string feedbackPath = Path.Combine(taskLogDir, "feedback.md");

        try
        {
            Directory.CreateDirectory(taskLogDir);

            var failingNames = new List<string>();
            foreach (GuardrailResult failed in reVerify.FailedGuardrails)
            {
                failingNames.Add(failed.Name);
                string safe = SanitizeGuardrailName(failed.Name);
                // GuardrailResult.Output is the full captured output on failure (stdout, or stderr when
                // stdout was empty); Reason is its first line. Persist both so the evidence is complete.
                string body = failed.Output ?? failed.Reason ?? "(no output captured)";
                AtomicFile.WriteAllText(
                    Path.Combine(taskLogDir, $"union-reverify-{safe}.stdout.log"), body);
            }

            string mergeKind = aiMerge ? "AI-merge resolution" : "non-FF union merge";
            string detail = reVerify.FailedGuardrails.Count == 0
                ? "The integration re-verify failed but reported no per-guardrail detail."
                : string.Join("\n\n", reVerify.FailedGuardrails.Select(g =>
                    $"## {g.Name}\n\n{g.Reason ?? "(no reason)"}\n\n" +
                    $"Full output persisted to `union-reverify-{SanitizeGuardrailName(g.Name)}.stdout.log`."));

            string feedback =
                $"# Task '{task.Id}' — union re-verify failed\n\n" +
                $"Task: {task.Description}\n\n" +
                $"The {mergeKind} produced bytes that FAILED the integration-guardrail re-verify, so the " +
                "harness rolled the merge back (state.json restored, integration worktree reset) and settled " +
                "this task `needs-human`. The merged bytes were discarded, but each failing integration " +
                "guardrail's output was persisted next to this file:\n\n" +
                $"{detail}\n\n" +
                "This is typically a MERGE COLLISION (two colliding contributions combined into something " +
                "that no longer builds/passes) — inspect the persisted output, fix the offending task(s), " +
                "and re-run.\n";
            AtomicFile.WriteAllText(feedbackPath, feedback);
        }
        catch
        {
            // Best-effort — never let a logging IO failure mask the re-verify failure itself.
        }

        return feedbackPath;
    }

    /// <summary>Filename-safe form of a guardrail name for the #188 union-reverify log artifacts.</summary>
    private static string SanitizeGuardrailName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }

    /// <summary>
    /// Record a worktree-mode SUCCESS settle (issue #196): journal a real
    /// <see cref="Journal.AttemptRecord"/> for the just-completed attempt TOGETHER with the reserved
    /// <paramref name="mergeSequence"/>, so a succeeded worktree task has a populated <c>Attempts</c>
    /// list exactly like a succeeded serial task (SSOT §7). The attempt data was computed by the
    /// executor and threaded here on <see cref="TaskResult.PendingAttempt"/>; its stamped
    /// <see cref="AttemptOutcome.Succeeded"/> outcome carries the #198 provenance (model + segment
    /// worktree + base commit). A result missing its <see cref="TaskResult.PendingAttempt"/> (a
    /// fake-provider path that never went through <c>ValidateFragmentForSettle</c>) falls back to the
    /// attempt-less <see cref="ISchedulerJournal.RecordSettle"/>, so no path regresses.
    /// </summary>
    private void RecordSucceededSettle(
        TaskNode task, TaskResult result, long mergeSequence, string? definitionHash = null)
    {
        if (result.PendingAttempt is not { } pending)
        {
            _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, mergeSequence, definitionHash);
            return;
        }

        var record = new Journal.AttemptRecord
        {
            Attempt = pending.Attempt,
            StartedAt = pending.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = pending.ActionExitCode,
            Outcome = Journal.AttemptOutcome.Succeeded,
            CostUsd = pending.CostUsd,
            LogDir = pending.LogDir,
            Provenance = pending.Provenance
        };
        _journal.RecordSettleWithAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, definitionHash);
    }

    /// <summary>
    /// Shallow-merge <paramref name="fragmentPath"/> into state.json at <paramref name="statePath"/>.
    /// The fragment was already validated (valid JSON object, no foreign keys). Uses atomic write.
    /// </summary>
    private static void MergeFragmentIntoState(string statePath, string preMergeState, string fragmentPath)
    {
        var stateObj = (JsonNode.Parse(preMergeState) as JsonObject) ?? new JsonObject();
        string rawFrag = File.ReadAllText(fragmentPath);
        var fragObj = (JsonNode.Parse(rawFrag) as JsonObject) ?? new JsonObject();

        foreach (var (key, value) in fragObj)
        {
            stateObj[key] = value?.DeepClone();
        }

        AtomicFile.WriteAllText(statePath, stateObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Convert an all-green report into a needs-human one when the terminal integration gate
    /// (§3.3) failed on the final plan-branch HEAD. The failure is attributed to the
    /// <c>integrationGate:true</c> sink task (or, absent one, the last task in plan order) so the
    /// run is not certified and mergeOnSuccess is refused.
    /// <para>
    /// #175 attribution: a terminal-gate (typically whole-repo build/test) failure on the merged HEAD
    /// is frequently a MERGE COLLISION — two tasks with OVERLAPPING <c>writeScope</c> on a shared file
    /// both wrote new content there and git's 3-way merge silently kept both (a semantic duplicate, no
    /// textual conflict marker). The harness cannot generically detect the duplicate (that is the build
    /// guardrail's job), but it CAN name the suspects: the diagnosis lists every overlapping-writeScope
    /// task pair and the shared path so a human immediately sees "this looks like a merge collision
    /// between task A and task B on &lt;file&gt;" instead of a bare build error. Advisory and robust —
    /// based PURELY on the writeScope-overlap structure (never the error text / a CS-code), and adds
    /// nothing when no writeScopes overlap.
    /// </para>
    /// </summary>
    private static RunReport WithTerminalGateFailure(
        PlanDefinition plan, RunReport report, ReVerifyResult gate)
    {
        string gateTaskId =
            plan.Tasks.LastOrDefault(t => t.IntegrationGate)?.Id
            ?? plan.Tasks[^1].Id;

        string failed = string.Join(", ", gate.FailedGuardrails.Select(g => g.Name));
        string summary = $"terminal integration gate failed on final HEAD: {failed}";

        string? collisionHint = WriteScope.OverlappingWriteScopeHint(plan);
        if (collisionHint is not null)
        {
            summary += $". {collisionHint}";
        }

        var rewritten = report.Tasks.Select(t => t.TaskId == gateTaskId
            ? t with
            {
                Outcome = TaskOutcome.NeedsHuman,
                Guardrails = gate.FailedGuardrails,
                Summary = summary
            }
            : t).ToList();

        return report with { Tasks = rewritten };
    }

    private static RunReport BuildReport(
        PlanDefinition plan,
        IReadOnlyDictionary<string, TaskResult> settled,
        bool cancelled)
    {
        var results = new List<TaskResult>(plan.Tasks.Count);
        foreach (TaskNode task in plan.Tasks)
        {
            results.Add(settled.TryGetValue(task.Id, out TaskResult? result)
                ? result
                : new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.Cancelled,
                    Summary = "not started (run cancelled)"
                });
        }

        return new RunReport { Tasks = results, Cancelled = cancelled };
    }

    /// <summary>
    /// Per-task channel item pairing a task with its assigned worktree handle. When
    /// <see cref="Fork"/> is non-null the handle is a placeholder and the worker materializes the
    /// real fork worktree off-gate at dequeue (M1 §B); otherwise <see cref="Handle"/> is the final
    /// assigned segment/reused directory.
    /// </summary>
    private readonly record struct TaskEnvelope(TaskNode Task, WorktreeHandle Handle, ForkRequest? Fork = null);

    /// <summary>
    /// A deferred fork-the-rest request (M1 §B): the producer's RECORDED commit sha to fork off
    /// (W-2 — never a live rev-parse of the inheritor-advanced segment branch). Materialized by the
    /// worker via <see cref="IWorktreeProvider.ForkFromTip"/> before the task's action runs.
    /// </summary>
    private readonly record struct ForkRequest(string ProducerRecordedSha);

    /// <summary>Mutable shared state of one run, guarded by the scheduler's gate.</summary>
    private sealed class RunContext(
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> byId,
        Dictionary<string, TaskResult> settled,
        Dictionary<string, int> pendingDeps,
        Channel<TaskEnvelope> channel,
        int remaining,
        Dictionary<string, WorktreeHandle> handles,
        Dictionary<string, string> directoryOwner,
        IntegrationHandle? integ)
    {
        public DependencyGraph Graph { get; } = graph;
        public IReadOnlyDictionary<string, TaskNode> ById { get; } = byId;
        public Dictionary<string, TaskResult> Settled { get; } = settled;
        public Dictionary<string, int> PendingDeps { get; } = pendingDeps;
        public Channel<TaskEnvelope> Channel { get; } = channel;
        public int Remaining { get; set; } = remaining;
        public Dictionary<string, WorktreeHandle> Handles { get; } = handles;

        /// <summary>
        /// plan 08 topology-wiring M0 bookkeeping: worktree path → current owning task id. Written
        /// under <see cref="_gate"/> only (CreateSegment/ForkFromTip set it; ReuseSegment transfers
        /// ownership to the inheritor; Discard removes the entry). The single source of truth for
        /// "is this directory free to Discard / reuse?".
        /// </summary>
        public Dictionary<string, string> DirectoryOwner { get; } = directoryOwner;
        public IntegrationHandle? Integ { get; } = integ;
    }
}
